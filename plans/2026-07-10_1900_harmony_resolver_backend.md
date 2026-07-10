# Harmony Resolver Backend and Agent-Operable Diagnostics

Accepted: 2026-07-10

## Goal

Create a public `bozmund/Harmony-Resolver` repository and local checkout at `C:\MyRepositories\Harmony-Resolver`. Build a self-hosted, horizontally scalable .NET 10 service that resolves YouTube audio with pluggable yt-dlp/YoutubeExplode adapters, normalizes to Ogg Opus, streams a first cache miss while storing it, and serves range-enabled hits from MinIO.

## Architecture

- ASP.NET Core Minimal API; PostgreSQL metadata/leases; MinIO objects; Valkey quotas/coordination; two stateless replicas behind Nginx.
- Validate video IDs, reject arbitrary URLs, invoke processes with safe argument lists.
- Defaults: 15-minute duration, 50 MiB object, two-minute extraction timeout, one-day inactivity expiry.
- Lease winner tees FFmpeg output to the first caller and MinIO; followers receive `202`; disconnect does not cancel ingestion.
- Auth0 bearer tokens are optional. Anonymous clients receive 10 ingestions/hour and two concurrent streams; authenticated subjects receive 100/hour and five streams.
- HMAC-redact subjects/IPs while retaining video IDs in operator diagnostics.

## Agent operability

- PowerShell/Bash `agent-up`, `agent-check`, `agent-diagnose`, `agent-down`, and confirmed `agent-reset` workflows.
- Deterministic fake extractors and development-only failure profiles.
- A read-only Auth0-protected Streamable HTTP MCP server plus local stdio M2M bridge.
- MCP tools: system/dependency snapshots, failed ingestion listing, ingestion/track inspection, bounded log/metric/trace queries, deployment info, and diagnostic checks.
- OpenTelemetry JSON logs, Prometheus (15 days), Loki (7 days), and Grafana dashboards.

## Deployment and tests

- Nix flake builds OCI images and provides a NixOS example for API replicas, MCP, PostgreSQL, MinIO, Valkey, Prometheus, Loki, Grafana, and host Nginx.
- Runtime secrets stay outside the Nix store.
- Unit/integration tests cover validation, extractor failover, concurrency/leases, leader streaming, disconnect continuation, range hits, cleanup, expiry, quotas, JWT validation, MCP redaction/audit, and multi-replica behavior.
- `.http` suites cover every public endpoint and MCP diagnostic tool.

## Deferred

Flutter integration, cloud sync, playback events, listening rooms, recommendations, notifications, and Saga workflows are later milestones. Resolver ingestion is a single service-owned state machine, not a Saga.
