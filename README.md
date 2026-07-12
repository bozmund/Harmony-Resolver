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

The API configuration uses deterministic media fixtures, so it does not contact YouTube. Stop the full stack with Rider's Stop button or `./scripts/agent-down.ps1`.

Raw OpenAPI JSON is available at `/openapi/v1.json` in Development. Swagger and OpenAPI are intentionally unavailable outside Development.

See `http/` for request suites and `plans/` for accepted architecture decisions.

### Production configuration

Production refuses to start without PostgreSQL, MinIO, Valkey, and a non-development identity HMAC key. Use `.env.example` as a key reference, but keep real values in a root-readable environment file outside the repository and Nix store. Auth0 remains optional for resolver requests; when configured, valid tokens receive authenticated quotas and invalid supplied tokens receive `401`.

Database schema changes are EF Core migrations. Compose runs the one-shot `migrate` service and starts both API replicas only after migration succeeds.

The public endpoints are available through Nginx. `/metrics` and `/internal/*` are blocked publicly. MCP is routed at `/mcp`; outside Development it requires an Auth0 token containing `diagnostics:read`.

### Diagnostics

`agent-diagnose` returns a sanitized distributed snapshot from the internal network. The MCP server exposes bounded, read-only diagnostic tools, and the local stdio bridge obtains and refreshes Auth0 Machine-to-Machine tokens from environment variables. Prometheus retains metrics for 15 days, Loki retains logs for 7 days, and Grafana is provisioned at `http://localhost:3000`.

## Security

Never commit `.env` or runtime credentials. Production MCP access requires an Auth0 token with `diagnostics:read`. Resolver requests may remain anonymous at lower quotas.

## License

GPL-3.0.
