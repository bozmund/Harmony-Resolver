# Harmony Resolver

Self-hosted distributed media resolver and Opus cache for Harmony Music.

## Development

Prerequisites: .NET 10 and Docker Desktop.

```powershell
./scripts/agent-up.ps1
./scripts/agent-check.ps1
./scripts/agent-diagnose.ps1
```

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

### Diagnostics

`agent-diagnose` returns a sanitized distributed snapshot from the internal network. The MCP server exposes bounded, read-only diagnostic tools, and the local stdio bridge obtains and refreshes Auth0 Machine-to-Machine tokens from environment variables. Prometheus retains metrics for 15 days, Loki retains logs for 7 days, and Grafana is provisioned at `http://localhost:3000`.

## Security

Never commit `.env` or runtime credentials. Production MCP access requires an Auth0 token with `diagnostics:read`. Resolver requests may remain anonymous at lower quotas.

## License

GPL-3.0.
