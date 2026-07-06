# Slate v1 — Design Spec

Self-hosted, Obsidian-compatible note platform. Client-server from the ground up (Jellyfin model): user runs the Slate server in Docker, clients connect by URL + credentials. Approved 2026-07-06.

## Repos

| Repo | Contents | Stack |
|---|---|---|
| `bulaya-ute/slate-server` | API, sync engine, Dockerfile, docker-compose, admin/setup docs, this spec | ASP.NET Core 8, EF Core 8 + Npgsql, SignalR |
| `bulaya-ute/slate-web` | Web client | React 18 + TypeScript + Vite + Tailwind, CodeMirror 6 |

License: AGPL-3.0 (both). Web client is baked into the server Docker image (served from wwwroot) — one container serves UI + API. Future desktop/mobile clients talk to the same API.

## Core principles

1. **Notes are plain `.md` files on disk** under `/data/vaults/{vaultId}/…`. Postgres stores metadata only (never note bodies). An admin can open the volume in Obsidian/any editor at any time. Standard YAML frontmatter passes through untouched.
2. **Every change is a revision** in an append-only log; sync = replaying the log. Conflicts are surfaced, never silently resolved.
3. **API-first**: web client uses only the public REST + SignalR surface.

## Server architecture

Single project `src/Slate.Server` (feature folders: `Domain`, `Data`, `Auth`, `Vaults`, `Notes`, `Sync`, `Search`, `Attachments`, `Admin`, `Storage`), plus `tests/Slate.Server.Tests` (xUnit + Testcontainers-Postgres integration tests via WebApplicationFactory).

- **Auth**: Argon2id (Konscious.Security.Cryptography), JWT access tokens (15 min) + rotating refresh tokens (30 days, hashed in DB, revocable). Roles: `Admin`, `User`. `external_logins` table exists but unused (future OIDC — no rearchitecture needed later).
- **First run**: if user count == 0, `POST /api/system/setup` creates the first admin (web shows a setup wizard). Endpoint permanently 410 afterward. No open registration; admins create users directly or issue one-time invite links.
- **Storage layer** (`IVaultStorage`): all disk access goes through one service — path normalization/traversal protection (reject `..`, absolute paths, reserved names), atomic writes (temp file + rename), SHA-256 content hashing. Vault dir layout mirrors the user's folder tree exactly; `.slate/` subfolder inside each vault holds conflict blobs (like `.obsidian/`, ignored by indexing).
- **Indexer**: on note write, re-extracts title (first `# h1` else filename), tags (`#tag` inline + frontmatter `tags:`), wikilinks (`[[target]]`, `[[target|alias]]`, `![[embed]]`), and updates `search_vector` (Postgres tsvector, GIN index).
- **File watcher**: `FileSystemWatcher` on the vaults root, 500 ms debounce per path. Echo suppression: the storage layer registers `(path, hash)` write-markers in memory before writing; watcher events matching a fresh marker are dropped. Genuine external edits (someone edited the .md on disk) become revisions with `author = null` ("filesystem").

## Database schema (Postgres, EF Core migrations)

- `users` (id uuid, username unique, display_name, email?, password_hash, role, is_disabled, created/updated_at)
- `refresh_tokens` (id, user_id, token_hash, device_name, expires_at, revoked_at?)
- `invites` (id, token_hash, created_by, role, expires_at, used_by?, used_at?)
- `external_logins` (id, user_id, provider, subject) — stub for OIDC
- `vaults` (id uuid, name, owner_id, created_at)
- `vault_members` (vault_id, user_id, access: owner|edit|read)
- `notes` (id uuid, vault_id, path unique-per-vault, title, content_hash, head_rev_id, size_bytes, has_conflict, is_deleted, search_vector tsvector, created/updated_at)
- `revisions` (id bigserial — doubles as the global change sequence, vault_id, note_id, parent_rev_id?, author_id?, device_id, kind: create|edit|delete|rename|resolve|attach, path, old_path?, content_hash, is_conflict, created_at). Append-only. Clients page it with `WHERE vault_id = @v AND id > @since`.
- `attachments` (id, vault_id, path, content_hash, size_bytes, mime, created_at)
- `tags` (id, vault_id, name) / `note_tags` (note_id, tag_id)
- `links` (source_note_id, target_note_id?, target_text) — `target_note_id` null = unresolved link; resolved lazily when a matching note appears. Powers backlinks + graph.

## Sync protocol (the CouchDB-equivalent)

- Every write request carries the client's `baseRevId` for that note (ETag/If-Match style).
- **Fast path**: `baseRevId == head_rev_id` → write file atomically, append revision, broadcast.
- **Conflict path**: head moved → server stores the incoming content as `.slate/conflicts/{revId}.md`, appends a revision with `is_conflict = true`, sets `notes.has_conflict`, broadcasts. Nothing is overwritten.
- **Catch-up**: `GET /api/vaults/{v}/changes?since={seq}` returns ordered revision entries (metadata only; client fetches content per note as needed). Identical semantics to CouchDB `_changes`.
- **Live feed**: SignalR hub `/hubs/sync`. Client authenticates (JWT), joins group `vault:{id}`, receives `revision` events `{seq, noteId, kind, path, oldPath, contentHash, deviceId}`. Client applies: if the note isn't open or is clean → refetch; if open + dirty → keep typing, its next save will take the conflict path and the banner appears.
- **Resolution**: `POST /api/vaults/{v}/notes/{id}/resolve` with merged content + the conflict rev ids being resolved → new `resolve` revision, clears flag, deletes conflict blobs. Web UX: banner on conflicted note → side-by-side diff (head vs conflicting), user picks a side or edits a merged result.
- **Client save loop**: debounced autosave (800 ms idle); on success adopt new revId. On disconnect: saves queue and retry with backoff; on reconnect, catch-up runs before the queue flushes. (Full offline-first vault cache is a **non-goal** for v1; graceful reconnect only.)
- Renames/moves/deletes and folder operations are revisions too (`rename`/`delete` kinds), so structure syncs the same way. Attachments sync as `attach` revisions.

Why this design: Postgres LISTEN/NOTIFY alone drops events across disconnects; polling isn't live. Append-only log gives durable catch-up + audit; SignalR gives push; the pair reproduces CouchDB's `_changes + continuous` semantics on the required stack.

## REST surface (all under `/api`, JWT bearer except where noted)

| Area | Endpoints |
|---|---|
| System | `GET /system/info` (anon: name, version, apiVersion, setupRequired) · `GET /system/health` (admin: disk, DB size, active sync connections) · `POST /system/setup` (anon, first-run only) |
| Auth | `POST /auth/login` · `POST /auth/refresh` · `POST /auth/logout` · `GET /auth/me` · `POST /auth/register` (invite token required) |
| Admin | `GET|POST|PATCH|DELETE /users` (create, role, disable, reset pw, delete) · `GET|POST|DELETE /invites` · per-user/per-vault storage stats |
| Vaults | `GET|POST|PATCH|DELETE /vaults` · `GET /vaults/{v}/stats` |
| Tree | `GET /vaults/{v}/tree` (full folder/note tree, one call) · `POST /vaults/{v}/folders` · `DELETE|rename` folders |
| Notes | `POST /vaults/{v}/notes` · `GET /notes/{id}` (meta) · `GET /notes/{id}/content` (text + revId header) · `PUT /notes/{id}/content` (body: content + baseRevId) · `POST /notes/{id}/rename` · `DELETE /notes/{id}` |
| Sync | `GET /vaults/{v}/changes?since=` · `GET /vaults/{v}/conflicts` · `GET /conflicts/{revId}/content` · `POST /notes/{id}/resolve` |
| Search | `GET /vaults/{v}/search?q=` (FTS, snippets + highlights) |
| Tags | `GET /vaults/{v}/tags` (with counts) · `GET /vaults/{v}/tags/{tag}/notes` |
| Graph | `GET /vaults/{v}/graph` (nodes + edges) · `GET /notes/{id}/backlinks` |
| Files | `POST /vaults/{v}/attachments` (multipart) · `GET /vaults/{v}/files/{**path}` (attachments; auth via cookie or short-lived signed URL for `<img>` tags) |

Versioning: `apiVersion` integer in `/system/info`; client refuses to connect if incompatible (Jellyfin-style compatibility gate).

## Web client (slate-web)

Stack: React 18 + TS + Vite + Tailwind. TanStack Query (server state) + Zustand (UI state). React Router. CodeMirror 6 editor. d3-force graph on canvas. Vitest + RTL for logic-level tests.

**Why CodeMirror 6 over ProseMirror**: Obsidian itself is CM6 — markdown source stays the document; live preview is decoration-based (syntax hides when the cursor leaves the line, wikilinks/tags/checkboxes/headings render in place). Preserves exact-markdown fidelity (critical for the files-on-disk principle) and is far simpler than bidirectional rich-text↔markdown mapping.

**Connection model**: on load, fetch `/config.json` → `{ "serverUrl": string|null, "allowServerSelection": bool, "serverName": string|null }`. If `serverUrl` set and selection disabled → straight to login against it. Else Connect screen: enter URL, client hits `/api/system/info` to validate compatibility, stores URL in localStorage, "change server" action always available from login + settings. Multiple remembered servers listed on the Connect screen.

**Screens/panels**:
- Connect → Login → (first-run Setup wizard) → Workspace
- Workspace: left sidebar (file explorer with drag-drop + context menus, search pane, tags pane), center editor with **tabs**, right sidebar (backlinks, outline). Panels collapsible, widths draggable, layout persisted.
- Editor: CM6 live preview + optional split markdown preview; tables, task lists (clickable checkboxes), code blocks with syntax highlighting, image embeds `![[img.png]]`, wikilink autocomplete on `[[`, tag autocomplete on `#`.
- Command palette: `Ctrl+P` quick switcher (fuzzy over titles/paths, client-side) and `Ctrl+Shift+P` command list (new note, toggle theme, open graph, …).
- Graph view: vault-wide force graph; node size ~ link count, click to open note, hover highlights neighborhood; local graph in right sidebar (v1 if time allows, else vault-wide only).
- Conflict UX: persistent banner on conflicted notes + badge in explorer → side-by-side resolve view.
- Admin panel: `/admin` routes (role-gated): users CRUD, invites, storage usage per user/vault, server health.
- Settings: theme (System default / Light / Dark — CSS variables + `prefers-color-scheme`, override persisted), editor prefs, server management.

**Polish bar** (explicit requirement): skeleton loading states, route/panel transitions (150–200 ms ease), hover/focus states everywhere, empty states with guidance, toasts for background events, no layout jank. Design language: clean modern, Obsidian-familiar keybindings.

## Deployment

- `slate-server/Dockerfile`: multi-stage — stage 1 clones + builds `slate-web` (ARG `SLATE_WEB_REF=main`), stage 2 builds server, final image serves web dist from wwwroot. Published to `ghcr.io/bulaya-ute/slate-server` via GitHub Actions on tag/main.
- `docker-compose.yml`: `slate` (GHCR image) + `postgres:16`, volumes `slate_data` (vaults) + `pg_data`, healthchecks. `docker-compose.dev.yml`: builds from sibling `../slate-web` checkout for local dev.
- Env vars: `SLATE_DB_CONNECTION` (or discrete PG vars), `SLATE_DATA_DIR=/data`, `SLATE_JWT_SECRET` (auto-generated to data dir if unset), `SLATE_SERVER_NAME`. TLS = reverse proxy's job (Caddy/Traefik examples in README).
- Web `config.json` overridable by mounting `/app/wwwroot/config.json`.

## Scope decisions

- **In v1**: everything above including graph view.
- **Deferred**: plugins (revisit only after v1 verified working), full offline-first cache, OIDC, desktop/mobile apps, per-note revision *content* history (log stores hashes/metadata only — conflict blobs are the exception).

## Delivery approach

Fable writes specs/plans/reviews only; Sonnet subagents implement in slices: (1) server foundation (auth/users/vaults/storage/migrations), (2) notes+indexing+search+attachments, (3) sync engine+watcher+conflicts, (4) admin+health, (5) web shell (connect/login/routing/theme), (6) editor, (7) explorer/tags/backlinks/palette, (8) graph+conflict UX+admin UI, (9) Docker/CI/README/docs. Each slice lands as a commit with tests green.
