# Harmony Resolver

Self-hosted distributed media resolver and Opus cache for Harmony Music.

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

### Deploying to a bare VPS

`deploy/bootstrap-oracle-vps.sh` sets up the full stack (Docker Engine, firewall rules, a root-owned secrets file, and a `harmony-resolver` systemd unit) on a plain Ubuntu VPS such as an Oracle Cloud Ampere A1 instance, using `compose.prod.yaml` — a production variant of `compose.yaml` that pulls the prebuilt `ghcr.io/bozmund/harmony-resolver-*` images instead of building, and fronts Nginx with Caddy for automatic Let's Encrypt HTTPS (see `deploy/Caddyfile`). Grafana and the PostgreSQL relay are intentionally bound to `127.0.0.1` only; reach them through SSH rather than exposing them publicly:

```powershell
ssh -N -L 3000:localhost:3000 -L 15432:localhost:15432 <user>@<vps-ip>
```

In Rider, create a PostgreSQL data source at `localhost:15432` using database `harmony`, user `harmony`, and the production `POSTGRES_PASSWORD`. Do not add Oracle Cloud or host firewall rules for ports `5432` or `15432`.

Run `deploy/setup-ci-deploy-user.sh` once afterward to let `.github/workflows/deploy.yml` redeploy automatically after every successful image publish: it creates a minimally-privileged `deploy` user restricted by sudoers to exactly `systemctl restart harmony-resolver.service`, and prints an SSH keypair to register as the `VPS_HOST`/`VPS_SSH_KEY` repository secrets.

### Delegated extraction (downloader fleet)

YouTube blocks extraction from datacenter IPs (the bot check yt-dlp reports as "Sign in to confirm you're not a bot"), so a VPS-hosted resolver cannot extract audio itself. With `Resolver__ExtractionMode=Delegated`, the resolver stops contacting YouTube: a cache miss is recorded as a pending ingestion job (`202 ingestion_in_progress`) and the listener polls, while a small fleet of **downloader agents** running on residential IPs claim jobs, fetch the audio with yt-dlp, and upload it back. The resolver normalizes each upload to Ogg Opus server-side and caches it, so the next listener gets a cache hit. The default `Inline` mode (dev/LAN) is unchanged — the API extracts in-process.

The worker endpoints (`/v1/worker/*`) require an Auth0 machine-to-machine token with the `tracks:ingest` scope, mirroring how MCP requires `diagnostics:read`. Provision one M2M client per agent and revoke a compromised agent by disabling its client. Delegated mode refuses to start outside Development without Auth0 configured, so the ingest path is never exposed unauthenticated.

Instead of the agents polling, the resolver publishes a lightweight **RabbitMQ** "doorbell" message whenever a job is enqueued, and the agents subscribe to a durable queue (`harmony.ingest.jobs`) and react immediately. The message is only a wake-up — the agent still claims through the HTTP worker protocol, so the Postgres lease remains the single arbiter of ownership and a lost or duplicate message is harmless (a `JobRepublisher` on the resolver re-rings any job still pending). The `rabbitmq` service is part of `compose.prod.yaml`; the API replicas publish over the internal plaintext port `5672`, and home agents connect over TLS on `5671` — the one new public port, opened by `deploy/bootstrap-oracle-vps.sh` on the host firewall (you must also open `5671` in the Oracle Security List). The bootstrap script generates a self-signed cert for that listener; give each agent its own RabbitMQ login (`rabbitmqctl add_user <name> <pass>` + `set_permissions -p / <name> '^harmony\.ingest\.jobs$' '' '^harmony\.ingest\.jobs$'`, revoke with `delete_user`). An agent therefore holds two credentials: a RabbitMQ user (to hear doorbells) and its Auth0 M2M token (to upload audio). If `RABBITMQ_URI` is unset the agent falls back to interval polling.

Run an agent at home from `deploy/downloader/` (`cp .env.example .env`, fill in `RESOLVER_BASE_URL`, `RABBITMQ_URI`, `RABBITMQ_CERT_SHA256`, and the M2M credentials, then `docker compose up -d --build`), or install it directly with the provided `harmony-resolver-downloader.service` systemd unit. The VPS bootstrap prints the SHA-256 fingerprint for its self-signed RabbitMQ certificate; pinning that exact certificate keeps peer verification enabled. Production initially sets `RESOLVER_EXTRACTION_MODE=Inline`; after at least one downloader is connected, change it to `Delegated` in `/etc/harmony-resolver/harmony-resolver.env` and restart `harmony-resolver.service`. Otherwise cold misses queue with nothing to fill them (the stuck-job reaper fails jobs older than `Resolver__JobMaxAge`, default 10 minutes, so listeners eventually get a definitive error rather than polling forever). See `plans/2026-07-19_0138_delegated_extraction_downloader_fleet.md` and `plans/2026-07-19_1240_rabbitmq_job_notifications.md` for the full design.

### Diagnostics

`agent-diagnose` returns a sanitized distributed snapshot from the internal network. The MCP server exposes bounded, read-only diagnostic tools, and the local stdio bridge obtains and refreshes Auth0 Machine-to-Machine tokens from environment variables. Prometheus retains metrics for 15 days, Loki retains logs for 7 days, and Grafana is provisioned at `http://localhost:3000`.

## Security

Never commit `.env` or runtime credentials. Production MCP access requires an Auth0 token with `diagnostics:read`. Resolver requests may remain anonymous at lower quotas.

## License

GPL-3.0.
