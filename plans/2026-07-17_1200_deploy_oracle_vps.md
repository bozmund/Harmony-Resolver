# Deploy Harmony Resolver to an Oracle Cloud free-tier VPS (Docker Compose, HTTPS via DuckDNS)

## Context

The user has an Oracle Cloud "Always Free" Ampere A1 VPS (2 OCPU / 12GB RAM, Ubuntu, arm64) and initially wanted a container-free deploy. Investigation showed `src/Harmony.Resolver.Api/Program.cs:83-93` hard-refuses to start outside Development unless PostgreSQL, MinIO, and Valkey are all configured with a non-default HMAC key — so a bare-metal deploy would mean hand-rolling native installs of three separate services plus the API. The user agreed the existing [compose.yaml](../compose.yaml) (already multi-arch, works unmodified on arm64) is the better path.

Additional constraints gathered from the user:
- No owned domain → use a free DuckDNS hostname: `harmony-resolver.duckdns.org` (user will create this at duckdns.org and point its A record at the VPS's public IP before we request a cert).
- Wants HTTPS.
- Wants the observability stack (Prometheus/Loki/Grafana/Alloy) included, but Grafana should stay private (SSH-tunnel only) rather than publicly exposed, since it has no auth hardening in this repo yet.
- CI already publishes prebuilt images to `ghcr.io/bozmund/harmony-resolver-api` and `ghcr.io/bozmund/harmony-resolver-mcp` on every push to `main` ([.github/workflows/publish.yml](../.github/workflows/publish.yml)) — production should pull these instead of building from source on a small ARM VPS.

Goal: a repeatable, root-owned-secrets, systemd-managed Compose deployment reachable at `https://harmony-resolver.duckdns.org`.

## Files added

**`compose.prod.yaml`** (repo root) — standalone production compose file (not a merge overlay, to avoid Compose `!reset`/merge-semantics footguns). Mirrors `compose.yaml`'s service topology but:
- `postgres`, `minio`: same images, credentials sourced from env vars (`${POSTGRES_PASSWORD}`, `${MINIO_ROOT_USER}`, `${MINIO_ROOT_PASSWORD}`) instead of the `development-only` literals.
- `migrate`, `api-1`, `api-2`, `mcp`: `image: ghcr.io/bozmund/harmony-resolver-api:latest` (and `harmony-resolver-mcp:latest` for mcp) instead of `build:`. `ASPNETCORE_ENVIRONMENT: Production`. Drop `Resolver__UseFakeExtractor` and `ENABLE_FAULT_INJECTION` (dev-only). Real connection strings/object-storage creds from env vars. `Quotas__IdentityHmacKey` / `Audit__HmacKey` from `${IDENTITY_HMAC_KEY}` / `${AUDIT_HMAC_KEY}`.
- `nginx`: same `deploy/nginx.conf`, but **no host port published** — only reachable inside the compose network as `nginx:80`, since Caddy becomes the sole internet-facing entrypoint.
- new `caddy` service: `caddy:2-alpine`, publishes `80:80` and `443:443`, mounts `deploy/Caddyfile` read-only plus `caddy_data`/`caddy_config` named volumes (cert persistence across restarts), `depends_on: [nginx]`.
- `grafana`: bind to `127.0.0.1:3000:3000` (not `0.0.0.0`) so it's only reachable via SSH tunnel.
- `prometheus`/`loki`/`alloy`: unchanged, reuse existing `deploy/*.yml`.
- `volumes:` adds `caddy_data`, `caddy_config` alongside the existing named volumes.

**`deploy/Caddyfile`** — automatic HTTPS, no certbot/cron needed:
```
harmony-resolver.duckdns.org {
    reverse_proxy nginx:80
}
```
Caddy obtains and renews the Let's Encrypt cert itself on first request to port 80/443 once DNS resolves.

**`deploy/production.env.example`** — template for the real env file (documents required vars: `POSTGRES_PASSWORD`, `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`, `IDENTITY_HMAC_KEY`, `AUDIT_HMAC_KEY`, each with an `openssl rand -hex 32`-style generation hint), analogous in spirit to the existing `.env.example` but for this Compose-based prod path.

**`deploy/systemd/harmony-resolver.service`** — systemd unit (same oneshot/`RemainAfterExit` pattern as [nix/module.nix](../nix/module.nix), adapted for plain Ubuntu instead of NixOS).

**`deploy/bootstrap-oracle-vps.sh`** — one-shot idempotent setup script the user runs once on the VPS (via `sudo`). Handles: Docker Engine + compose plugin install, opening 80/443 in the host firewall (calling out that OCI Ubuntu images ship `iptables` rules that DROP everything but SSH by default, on top of the cloud-level Security List/NSG), cloning the repo into `/opt/harmony-resolver`, generating `/etc/harmony-resolver/harmony-resolver.env` with random secrets if absent, installing and enabling the systemd unit.

**README.md** — short addition to the existing "Production configuration" section pointing at the bootstrap script, noting Grafana is intentionally bound to localhost.

## Explicit call-outs for the user

- **GHCR visibility**: if `bozmund/Harmony-Resolver` packages are private, `docker compose pull` on the VPS needs `docker login ghcr.io` with a PAT (`read:packages`) first.
- **DNS must resolve before first start**: Caddy's ACME HTTP-01 challenge needs `harmony-resolver.duckdns.org` already pointing at the VPS's public IP.
- **Migration gating**: `api-1`/`api-2` won't start until `migrate` exits 0 (already in base `compose.yaml`).

## Verification

1. `docker compose -f compose.prod.yaml config` — validate compose parses/merges correctly.
2. `systemctl status harmony-resolver` and `docker compose -f compose.prod.yaml ps` — all services healthy/running.
3. `curl -sf https://harmony-resolver.duckdns.org/health/live` and `/health/ready` — confirm HTTPS termination and app health end-to-end.
4. `curl -sf https://harmony-resolver.duckdns.org/internal/diagnostics/snapshot` should return blocked/403.
5. `ssh -L 3000:localhost:3000 <user>@<vps-ip>` then browse `http://localhost:3000` — Grafana reachable privately, not publicly.
