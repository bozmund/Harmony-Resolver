# Harmony Resolver Agent Guide

## First steps

- Read `README.md`, the latest accepted plan in `plans/`, and relevant source before changing behavior.
- Run `scripts/agent-check.ps1` on Windows or `scripts/agent-check.sh` on Linux before handoff.
- Use `scripts/agent-diagnose.*` or the read-only MCP tools before guessing at runtime failures.
- Never expose Auth0 tokens, M2M secrets, MinIO credentials, signed URLs, raw subjects, or client IPs.

## Architecture rules

- Keep API replicas stateless; PostgreSQL, MinIO, and Valkey are shared state.
- Validate video IDs before invoking extractors. Never accept arbitrary upstream URLs.
- Invoke yt-dlp and FFmpeg with argument lists, never shell interpolation.
- Object upload must complete before PostgreSQL marks a track ready.
- All leases, retries, range behavior, and fault profiles require automated tests.
- MCP tools are read-only, bounded, audited, and use the shared redaction layer.

## Plans

- Save every accepted plan verbatim under `plans/YYYY-MM-DD_HHmm_<slug>.md`.
- Add accepted plans to `plans/index.md`. Do not save drafts or rejected plans.

## Git

- Do not commit, push, rewrite history, or change remotes without explicit authorization for that exact operation.
- Preserve unrelated work and report every validation command.
