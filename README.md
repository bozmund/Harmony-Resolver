# Harmony Resolver

Self-hosted distributed media resolver and permanent Opus library for Harmony Music.

## Development

Prerequisites: .NET 10 and Docker Desktop.

```powershell
./scripts/agent-up.ps1
./scripts/agent-check.ps1
./scripts/agent-diagnose.ps1
```

### Local delegated-downloader test

The standard development stack uses inline fake extraction. To test the delegated ingestion flow and
RabbitMQ doorbells locally, start the dedicated overlay instead:

```powershell
./scripts/agent-up-delegated.ps1
```

This starts RabbitMQ at `amqp://localhost:5672` and its management UI at
`http://localhost:15672` (`harmony` / `development-only-rabbitmq`). These credentials and plaintext
AMQP are development-only; the broker is not part of the normal stack.

In a second terminal, start a local Docker downloader:

```powershell
cd deploy/downloader
Copy-Item .env.local.example .env
docker compose up --build
```

The local downloader connects through Docker Desktop to `host.docker.internal`; it deliberately has no
Auth0 settings because the Development worker endpoints are open. It uses real `yt-dlp`, so request a
valid, publicly downloadable YouTube video ID (the normal resolver fake-extractor fixture is not used in
delegated mode). A first `GET /v1/tracks/{videoId}/audio` returns `202`; watch the downloader log for
`Claimed` and `Uploaded`, then repeat the request and expect `200` with an Ogg Opus response. Stop the
test stack with `./scripts/agent-down-delegated.ps1` and the downloader with `docker compose down` from
`deploy/downloader`.

Linux/NixOS equivalents are available as `.sh` scripts. The deterministic fake extractor is enabled by default in development, so contributors and AI agents can reproduce the service without contacting YouTube.

### Rider

Open `Harmony.Resolver.slnx`, choose a shared configuration in the toolbar, and click Run:

- **Harmony Resolver API** starts a fast, debugger-attached API at `http://localhost:5080` and opens Swagger at `http://localhost:5080/swagger`.
- **Harmony Resolver Full Stack** starts the two API replicas, Nginx, PostgreSQL, MinIO, Valkey, Prometheus, Loki, and Grafana with Docker Compose. Open Swagger at `http://localhost:8088/swagger` and Grafana at `http://localhost:3000`.
- **Harmony Resolver Phone Stack** starts the full stack plus the Windows host mDNS advertiser for `_harmony-resolver._tcp`.

The API configuration uses deterministic media fixtures, so it does not contact YouTube. Stop the full stack with Rider's Stop button or `./scripts/agent-down.ps1`.

For physical-phone testing, run `scripts/lan-firewall.ps1 enable` once from an elevated PowerShell, then run `scripts/phone-check.ps1` to print and validate the laptop LAN URL. The firewall rules are restricted to Private networks; no router port forwarding is needed.

Raw OpenAPI JSON is available at `/openapi/v1.json` in Development. Swagger and OpenAPI are intentionally unavailable outside Development.

See `http/` for request suites and `plans/` for accepted architecture decisions.

### Production configuration

Production refuses to start without PostgreSQL, MinIO, Valkey, and a non-development identity HMAC key. Use `.env.example` as a key reference, but keep real values in a root-readable environment file outside the repository and Nix store. Auth0 remains optional for resolver requests; when configured, valid tokens receive authenticated quotas and invalid supplied tokens receive `401`.

Database schema changes are EF Core migrations. Compose runs the one-shot `migrate` service and starts both API replicas only after migration succeeds.

The public endpoints are available through Nginx. `/metrics` and `/internal/*` are blocked publicly. MCP is routed at `/mcp`; outside Development it requires an Auth0 token containing `diagnostics:read`.

### Production deployment

Production infrastructure is owned by the separate `Harmony-Platform` repository. This repository
tests and publishes immutable Resolver, Downloader and MCP images, then dispatches the Platform
deployment workflow. The public Resolver base URL is
`https://harmony-resolver.duckdns.org/resolver/`. The older bare-VPS scripts remain only as migration
references and are not the production deployment entry point.

### Delegated extraction (downloader fleet)

YouTube blocks extraction from datacenter IPs (the bot check yt-dlp reports as "Sign in to confirm you're not a bot"), so a VPS-hosted resolver cannot extract audio itself. With `Resolver__ExtractionMode=Delegated`, the resolver stops contacting YouTube: a library miss is recorded as a pending ingestion job (`202 ingestion_in_progress`) and the listener polls, while a small fleet of **downloader agents** running on residential IPs claim jobs, fetch the audio with yt-dlp, and upload it back. The resolver normalizes each upload to Ogg Opus server-side and stores it permanently, so the next listener gets a library hit. The default `Inline` mode (dev/LAN) is unchanged — the API extracts in-process.

The worker endpoints (`/v1/worker/*`) require an Auth0 machine-to-machine token with the `tracks:ingest` scope, mirroring how MCP requires `diagnostics:read`. Provision one M2M client per agent and revoke a compromised agent by disabling its client. Delegated mode refuses to start outside Development without Auth0 configured, so the ingest path is never exposed unauthenticated.

Instead of the agents polling, the resolver publishes a lightweight **RabbitMQ** "doorbell" message whenever a job is enqueued, and the agents subscribe to a durable queue (`harmony.ingest.jobs`) and react immediately. The message is only a wake-up — the agent still claims through the HTTP worker protocol, so the Postgres lease remains the single arbiter of ownership and a lost or duplicate message is harmless (a `JobRepublisher` on the resolver re-rings any job still pending). The `rabbitmq` service is part of `compose.prod.yaml`; the API replicas publish over the internal plaintext port `5672`, and home agents connect over TLS on `5671` — the one new public port, opened by `deploy/bootstrap-oracle-vps.sh` on the host firewall (you must also open `5671` in the Oracle Security List). The bootstrap script creates a private CA and a separate CA-signed server certificate for that listener; downloaders pin the server-certificate fingerprint it prints. Give each agent its own RabbitMQ login (`rabbitmqctl add_user <name> <pass>` + `set_permissions -p / <name> '^harmony\.ingest\.jobs$' '' '^harmony\.ingest\.jobs$'`, revoke with `delete_user`). An agent therefore holds two credentials: a RabbitMQ user (to hear doorbells) and its Auth0 M2M token (to upload audio). If `RABBITMQ_URI` is unset the agent falls back to interval polling.

Run an agent at home from `deploy/downloader/` (`cp .env.example .env`, fill in `RESOLVER_BASE_URL`, `RABBITMQ_URI`, `RABBITMQ_CERT_SHA256`, and the M2M credentials, then `docker compose up -d --build`), or install it directly with the provided `harmony-resolver-downloader.service` systemd unit. The VPS bootstrap prints the SHA-256 fingerprint for RabbitMQ's server certificate; pinning that exact certificate keeps peer verification enabled. Production runs `Delegated` extraction as deployment configuration, so every GitHub deploy preserves that mode; keep at least one downloader connected. Otherwise cold misses queue with nothing to fill them (the stuck-job reaper fails jobs older than `Resolver__JobMaxAge`, default 10 minutes, so listeners eventually get a definitive error rather than polling forever). See `plans/2026-07-19_0138_delegated_extraction_downloader_fleet.md` and `plans/2026-07-19_1240_rabbitmq_job_notifications.md` for the full design.

### Diagnostics

`agent-diagnose` returns a sanitized distributed snapshot from the internal network. The MCP server exposes bounded, read-only diagnostic tools, and the local stdio bridge obtains and refreshes Auth0 Machine-to-Machine tokens from environment variables. Prometheus retains metrics for 15 days, Loki retains logs for 7 days, and Grafana is provisioned at `http://localhost:3000`.

## Security

Never commit `.env` or runtime credentials. Production MCP access requires an Auth0 token with `diagnostics:read`. Resolver requests may remain anonymous at lower quotas.

## License

GPL-3.0.
