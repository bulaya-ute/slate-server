# Slate v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Read the spec first**: `slate-server/docs/specs/2026-07-06-slate-v1-design.md` — it is the source of truth; this plan adds contracts and ordering.

**Goal:** Working Slate v1 — ASP.NET Core server + React web client, live sync, Docker deploy.

**Architecture:** Two repos (`slate-server`, `slate-web`) developed in parallel tracks. Server track S1→S6 sequential; web track W1→W5 sequential; D1 last. Web track builds against the API contract below and may read `../slate-server` source for ground truth.

**Tech Stack:** .NET 8 (SDK 8.0.422 installed) · EF Core 8 + Npgsql · SignalR · xUnit + Testcontainers.PostgreSql · React 18 + TS + Vite + Tailwind · TanStack Query + Zustand · CodeMirror 6 · d3-force · Vitest.

## Global Constraints

- Dev machine is Windows 11 / PowerShell; Docker available. All commands must run there.
- JSON over the wire is **camelCase**. All endpoints under `/api`. `apiVersion: 1`.
- **No note bodies in Postgres** — files on disk are canonical. All disk IO through `IVaultStorage`.
- Every mutating note/folder/attachment operation appends a `revisions` row and broadcasts on SignalR group `vault:{vaultId}` as event `revision`.
- Tests: server = xUnit integration tests (WebApplicationFactory + Testcontainers Postgres); web = Vitest for non-trivial logic (API client, stores, parsers, fuzzy match). Each task ends with all tests green + a commit.
- Commit style: `feat(scope): …` / `fix:` / `chore:`, ending with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- UI must meet the spec's polish bar (skeletons, 150–200 ms transitions, empty states, system/light/dark theme via CSS variables).
- Vault content paths are vault-relative, forward-slash, no leading slash (e.g. `folder/note.md`).

## Shared API contract (both tracks conform; server may add fields, never rename)

```
GET  /api/system/info            → 200 {name:"Slate", version, apiVersion:1, serverName, setupRequired}
POST /api/system/setup           → 204 {username, password, displayName}    (410 once users exist)
POST /api/auth/login             → 200 {accessToken, refreshToken, user}    user={id,username,displayName,role,isDisabled}
POST /api/auth/refresh           → 200 {accessToken, refreshToken}          body {refreshToken}
POST /api/auth/register          → 200 (login shape)                        body {inviteToken, username, password, displayName}
GET  /api/auth/me                → 200 user
GET  /api/vaults                 → 200 [{id,name,role,noteCount,sizeBytes,createdAt}]
POST /api/vaults                 → 201 vault                                body {name}
GET  /api/vaults/{v}/tree        → 200 {folders:[string], notes:[{id,path,title,hasConflict,sizeBytes,updatedAt}]}
POST /api/vaults/{v}/notes       → 201 note-meta                            body {path, content?}
GET  /api/notes/{id}/content     → 200 text/markdown, headers X-Rev-Id, X-Content-Hash
PUT  /api/notes/{id}/content     → 200 {revId, contentHash}                 body {content, baseRevId, deviceId}
                                 → 409 {headRevId, conflictRevId}           (conflict stored server-side)
POST /api/notes/{id}/rename      → 200 note-meta                            body {newPath}
DELETE /api/notes/{id}           → 204
POST /api/vaults/{v}/folders     → 204 {path} · rename: POST /folders/rename {path,newPath} · DELETE /folders?path=
GET  /api/vaults/{v}/changes?since={seq}&limit= → 200 {results:[rev], lastSeq}
     rev = {seq, noteId, kind:"create|edit|delete|rename|resolve|attach", path, oldPath?, contentHash, deviceId, isConflict, createdAt}
GET  /api/vaults/{v}/conflicts   → 200 [{noteId, path, conflicts:[{revId, deviceId, createdAt}]}]
GET  /api/conflicts/{revId}/content → 200 text/markdown
POST /api/notes/{id}/resolve     → 200 {revId}                              body {content, resolvedRevIds:[..]}
GET  /api/vaults/{v}/search?q=   → 200 [{noteId, path, title, snippetHtml, score}]
GET  /api/vaults/{v}/tags        → 200 [{name, count}]
GET  /api/notes/{id}/backlinks   → 200 [{noteId, path, title, contextSnippet}]
GET  /api/vaults/{v}/graph       → 200 {nodes:[{id,path,title,linkCount}], edges:[{source,target}]}
POST /api/vaults/{v}/attachments → 201 {path, sizeBytes, mime}              multipart; field "file", optional "folder"
GET  /api/vaults/{v}/files/{**path} → attachment bytes (accepts ?access_token= for <img> use)
Admin: GET|POST /api/users, PATCH /api/users/{id} {role?,isDisabled?,newPassword?}, DELETE /api/users/{id}
       GET|POST|DELETE /api/invites (POST → {token, expiresAt, role}), GET /api/system/health, GET /api/admin/stats
SignalR hub /hubs/sync (JWT via accessTokenFactory): client calls JoinVault(vaultId)/LeaveVault(vaultId); server emits "revision" (rev shape above, plus vaultId).
Errors: {error:{code, message}}; 401 triggers client refresh-then-retry once.
```

---

## Server track (repo: `slate-server`)

### Task S1: Scaffold, domain entities, EF Core, Testcontainers harness

**Files:** `Slate.sln`, `src/Slate.Server/` (webapi project), `src/Slate.Server/Domain/*.cs` (entities per spec schema), `src/Slate.Server/Data/SlateDbContext.cs`, `Data/Migrations/`, `tests/Slate.Server.Tests/` (+ `TestApp.cs` fixture: WebApplicationFactory wired to a Testcontainers Postgres, migrations applied).
**Produces:** entities exactly matching the spec's schema section (table/column names snake_case via Npgsql naming or explicit config); `SlateDbContext`; test fixture other tasks reuse; config binding for env vars `SLATE_DB_CONNECTION`, `SLATE_DATA_DIR`, `SLATE_JWT_SECRET`, `SLATE_SERVER_NAME`.

- [ ] Scaffold solution + projects; add packages (Npgsql.EFCore.PostgreSQL, Konscious.Security.Cryptography.Argon2, JwtBearer, Swashbuckle, xunit, Testcontainers.PostgreSql, Mvc.Testing).
- [ ] Entities + DbContext + initial migration (`revisions.id` = bigserial PK = change seq; `notes.search_vector` tsvector + GIN; unique (vault_id, path) filtered on not-deleted).
- [ ] `GET /api/system/info` implemented (setupRequired = no users).
- [ ] Test: fixture boots app against container DB, migrations apply, `/api/system/info` returns apiVersion 1. Green → commit.

### Task S2: Auth, users, setup, invites

**Files:** `src/Slate.Server/Auth/` (PasswordHasher, TokenService, AuthController, endpoints), `Admin/UsersController.cs`, `Admin/InvitesController.cs`.
**Produces:** `IPasswordHasher.Hash/Verify` (Argon2id); JWT with claims `sub`, `role`; policies `AdminOnly`; refresh rotation (old token revoked on use, reuse → revoke family); contract endpoints: setup, login, refresh, logout, me, register(invite), users CRUD, invites.

- [ ] Tests first (fixture): setup→login→me roundtrip; setup twice → 410; refresh rotates and old token rejected; invite register honors role + single use + expiry; disabled user login → 401; non-admin hitting /api/users → 403.
- [ ] Implement; rate-limit `/api/auth/*` (fixed window, e.g. 10/min/IP). Green → commit.

### Task S3: Vault storage layer + vault CRUD + tree/folders

**Files:** `Storage/IVaultStorage.cs` + `VaultStorage.cs`, `Vaults/VaultsController.cs`, `Vaults/TreeController.cs`.
**Produces:** `IVaultStorage`: `ReadNote`, `WriteNoteAtomic(vaultId, path, content) → (sha256, size)`, `Delete`, `Move`, `ListAll`, `SafePath(path)` (throws on `..`/rooted/invalid); write-markers API `RegisterWrite(path, hash)` + `WasOurWrite(path, hash)` for S5's watcher. Vault CRUD + membership checks (middleware/filter `RequireVaultAccess(edit|read)`); tree endpoint; folder create/rename/delete (rename moves files + updates note paths + appends `rename` revisions per affected note).

- [ ] Tests first: path traversal (`../x`, `a/../../x`, `C:\x`, backslashes) rejected; atomic write produces file + correct hash; folder rename updates child note paths; non-member gets 404.
- [ ] Implement. Green → commit.

### Task S4: Notes CRUD, indexer, search, tags, links, attachments

**Files:** `Notes/NotesController.cs`, `Notes/NoteService.cs`, `Notes/MarkdownIndexer.cs`, `Search/SearchController.cs`, `Tags/TagsController.cs`, `Links/GraphController.cs`, `Attachments/AttachmentsController.cs`.
**Produces:** `MarkdownIndexer.Extract(content) → {title, tags[], links[{target, alias, isEmbed}]}` (regex-based; tags from inline `#tag` — not inside code fences — and frontmatter `tags:` list/scalar; links `[[t]]`, `[[t|a]]`, `![[t]]`; title = first `# ` heading else filename). `NoteService.Create/UpdateContent/Rename/Delete` — each writes disk via IVaultStorage, updates notes row, reindexes (tags, links, tsvector from content with markdown stripped), **appends revision**, returns new revId. UpdateContent enforces baseRevId: mismatch → 409 per contract (conflict blob + is_conflict rev + has_conflict flag — full behavior here, S5 only adds feed/hub/watcher). Link resolution: on note create/rename, resolve `links.target_text` matches (case-insensitive, with/without `.md`, basename match like Obsidian).

- [ ] Tests first: indexer unit cases (tags in fences ignored, frontmatter tags, all 3 link forms); create→get content roundtrip with X-Rev-Id; stale baseRevId → 409 + conflict blob exists + head file unchanged; rename resolves previously-unresolved links; search finds phrase with `<mark>` snippet via ts_headline; backlinks + graph shapes; attachment upload → served bytes + `attach` revision.
- [ ] Implement. Green → commit.

### Task S5: Sync — changes feed, SignalR hub, resolve, file watcher

**Files:** `Sync/SyncHub.cs`, `Sync/ChangesController.cs`, `Sync/ConflictsController.cs`, `Sync/VaultWatcher.cs` (BackgroundService), `Sync/RevisionBroadcaster.cs`.
**Produces:** contract endpoints changes/conflicts/conflict-content/resolve; hub with JoinVault (authorizes membership) and `revision` event emitted by all NoteService mutations (via `IRevisionBroadcaster` injected into S4's service); `VaultWatcher`: 500 ms debounce per path, ignores `.slate/`, uses `WasOurWrite` echo suppression, external md change → revision (author null, deviceId "filesystem") + reindex, external create/delete/rename handled; hub connection count exposed for health.

- [ ] Tests first: changes?since pagination + lastSeq; resolve merges (creates `resolve` rev, clears has_conflict, deletes blobs, 409s if resolvedRevIds stale); watcher: write file directly to disk → revision appears with null author (poll with timeout); server-originated write → no duplicate revision.
- [ ] Implement (hub integration test via SignalR client through WebApplicationFactory if feasible; else broadcaster unit-tested via fake hub context). Green → commit.

### Task S6: Admin health/stats, SPA hosting, hardening

**Files:** `Admin/HealthController.cs`, `Admin/StatsController.cs`, `Program.cs` additions.
**Produces:** `/api/system/health` (disk free/total for data dir, DB size via pg_database_size, active hub connections, uptime, version); `/api/admin/stats` (per-user and per-vault note counts + bytes); static file hosting of `wwwroot` with SPA fallback (API/hubs excluded) and `/config.json` served from `SLATE_CONFIG_PATH` when set; CORS for dev origins (`http://localhost:5173`); Swagger in dev; JWT secret auto-generate-and-persist to data dir when env unset; global error envelope `{error:{code,message}}`.

- [ ] Tests first: health admin-only + fields present; stats numbers correct after creating notes; unknown `/some/route` returns SPA index when wwwroot has one, `/api/unknown` returns 404 JSON.
- [ ] Implement. Green → commit. Update README quick-start (dev run instructions).

## Web track (repo: `slate-web`)

*All web tasks: follow spec's Web client + polish sections. Design tokens/components established in W1 are the only styling primitives later tasks use. Verify with `npm run build` + `npm test` green; `npm run dev` against a running local server when server track is far enough (final integration in D1).*

### Task W1: Scaffold, design system, connect/login/setup flow, API client

**Files:** Vite scaffold; `src/lib/api/` (client.ts — fetch wrapper with auth header, 401→refresh→retry-once, error envelope; types.ts — all contract DTOs; signalr.ts stub), `src/lib/config.ts` (fetch `/config.json`, defaults `{serverUrl:null, allowServerSelection:true, serverName:null}`), `src/stores/` (auth, servers, theme — Zustand, persisted), `src/theme.css` (CSS variable tokens: bg/surface/text/accent/borders, light+dark values, `prefers-color-scheme` + `data-theme` override), `src/components/ui/` (Button, Input, Modal, Toast, Skeleton, Spinner, Tooltip — the design system), routes: Connect, Login, Setup wizard, app shell placeholder.
**Produces:** `api.get/post/put/del`, `useAuth()`, `useServer()` (current server URL, remembered servers list, switch/forget), theme store; route guards (no server → /connect unless config pins one; no auth → /login; setupRequired → /setup).
**Design:** invoke the `frontend-design` skill for direction; clean modern, Obsidian-familiar; dark default matching system.

- [ ] Vitest first for: config resolution matrix (pinned+locked / pinned+selectable / unpinned), refresh-retry-once logic, theme override behavior.
- [ ] Implement screens with full polish (server validation states: checking / incompatible (apiVersion) / unreachable / ok→slide to login). Green + `npm run build` clean → commit.

### Task W2: Workspace shell, vault switcher, file explorer, tabs

**Files:** `src/features/workspace/` (Layout with resizable persisted panels), `src/features/explorer/` (tree view: folders collapsible, notes, drag-drop move, context menu: new note/folder, rename, delete; inline rename; conflict badge), `src/features/tabs/` (tab bar: open/close/reorder, dirty dot, middle-click close, persisted per vault), vault create/switch UI, empty states.
**Consumes:** W1 api/types/ui kit. Tree from `GET /vaults/{v}/tree` (client builds nested structure from flat paths).
- [ ] Vitest: tree-building from flat paths incl. empty folders; tab store ops.
- [ ] Implement; TanStack Query keys `['tree', vaultId]` etc, optimistic rename/move with rollback toast. Commit.

### Task W3: Editor (CodeMirror 6 live preview) + sync client

**Files:** `src/features/editor/` (cm setup: @codemirror/lang-markdown + GFM; live-preview extensions: heading sizes, bold/italic/strike inline render with syntax revealed on cursor-line, wikilink widget (click→open, unresolved styled dim, Ctrl+click new tab), inline `#tag` chips, task checkboxes toggle+persist, fenced code highlight, `![[img]]` renders via files endpoint; wikilink autocomplete on `[[` (titles+paths), tag autocomplete on `#`; split preview pane (markdown-it + wikilink/task/highlight plugins) toggleable), `src/features/sync/` (signalr client: connect per vault, JoinVault, `revision` handler; catch-up via /changes on (re)connect using persisted lastSeq; autosave: 800 ms debounce, baseRev tracking, offline queue with backoff, status indicator saved/saving/offline/conflict).
**Consumes:** contract PUT content 200/409 semantics; rev event shape.
- [ ] Vitest: autosave state machine (dirty→saving→saved; 409→conflict state; offline queue flush order); markdown-it wikilink plugin output.
- [ ] Implement. Editor must feel Obsidian-native (typography per design tokens, smooth cursor reveal). Commit.

### Task W4: Search, tags pane, backlinks, outline, command palette

**Files:** `src/features/search/` (debounced FTS pane, `<mark>` snippets, click→open), `src/features/tags/` (tag pane counts, click filters explorer or opens tag results), `src/features/backlinks/` (right panel, context snippets), `src/features/outline/` (headings tree from open note, click scrolls), `src/features/palette/` (Ctrl+P fuzzy quick-switcher over tree — own scorer, subsequence + word-boundary bonus; Ctrl+Shift+P command list: new note, toggle theme/preview, open graph/settings/vault switcher; both keyboard-navigable).
- [ ] Vitest: fuzzy scorer ranking cases; outline parser.
- [ ] Implement with keyboard-first UX. Commit.

### Task W5: Graph view, conflict UX, admin panel, settings

**Files:** `src/features/graph/` (canvas d3-force: nodes sized by linkCount, labels on zoom, hover highlights neighbors, click opens, pan/zoom, unresolved ghost nodes optional), `src/features/conflicts/` (banner on conflicted note → resolve view: side-by-side head vs conflict with diff highlighting (`diff` npm pkg), pick-side or edit-merged, calls resolve), `src/features/admin/` (role-gated routes: users table CRUD + reset pw + disable, invites create/copy-link/delete, storage stats bars per user/vault, health dashboard w/ auto-refresh), `src/features/settings/` (theme choice, editor prefs, server management incl. disconnect/switch, about w/ server version).
- [ ] Vitest: diff hunk mapping for resolve view.
- [ ] Implement. Full app keyboard+polish pass (focus rings, transitions, skeletons everywhere data loads). Commit.

## Deploy track (repo: `slate-server`, after S6 + W5)

### Task D1: Docker, compose, CI, docs

**Files:** `Dockerfile` (stage1 node:24 clone+build slate-web ARG `SLATE_WEB_REF=main`; stage2 sdk:8.0 publish; final aspnet:8.0 + wwwroot from stage1; non-root user, `VOLUME /data`, healthcheck), `docker-compose.yml` (ghcr image + postgres:16 + volumes + healthchecks + env), `docker-compose.dev.yml` (build local, web from `../slate-web`), `.github/workflows/ci.yml` (build+test on push/PR), `.github/workflows/publish.yml` (GHCR on main/tags), slate-web `.github/workflows/ci.yml` (build+test), full `README.md` both repos (setup, env vars, reverse-proxy Caddy example, connecting a client, sync/conflict model explainer), `docs/architecture.md`, `docs/sync-protocol.md`, `docs/admin-guide.md`, `docs/development.md`.
- [ ] `docker compose -f docker-compose.dev.yml up --build` → full stack works locally: setup wizard → create vault → two browser tabs live-sync a note → conflict flow → search/graph/admin verified (manual smoke, then scripted where cheap).
- [ ] Push; verify GH Actions green; verify GHCR image published. Commit.

## Self-review notes

- Spec coverage: every spec section maps to a task (auth/setup→S2, storage→S3, indexer/search/tags/links/attachments→S4, sync/conflicts/watcher→S5, health/SPA/config→S6, connect/config.json/theme→W1, explorer/tabs→W2, editor/autosave/live-feed→W3, search/tags/backlinks/palette→W4, graph/conflict-UX/admin→W5, Docker/CI/README→D1). Frontmatter passthrough is implicit (files never rewritten by server). ✓
- Contract names are single-sourced in this file; both tracks reference it. ✓
- Ordering: conflict behavior lives in S4 (not S5) so 409 semantics exist before web W3 needs them. ✓
