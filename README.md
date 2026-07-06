# Slate Server

Self-hosted, Obsidian-compatible note-taking server. Notes live as plain Markdown files on disk; PostgreSQL tracks metadata, search, and sync state. Clients (see [slate-web](https://github.com/bulaya-ute/slate-web)) connect to your server URL, Jellyfin-style.

> **Status: pre-release, under active development.** Full setup docs land with v1.

- **Design spec**: [docs/specs/2026-07-06-slate-v1-design.md](docs/specs/2026-07-06-slate-v1-design.md)
- **Stack**: ASP.NET Core 8 · PostgreSQL · SignalR live sync · Docker
- **License**: AGPL-3.0
