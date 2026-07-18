# Rework the Grafana dashboard into something actually useful

## Context

The current dashboard is mostly dead weight for a personal music server: three panels ("Track status distribution", "Active leases", "Object store latency (MinIO)") query Prometheus metrics that have never existed in the codebase and will show "No data" forever; "Active requests" shows Prometheus's own scrape connections; the headline request-rate panels are dominated by monitoring self-traffic (`/metrics` scrapes every 15s, health checks, MCP diagnostic calls) so real music traffic is invisible; the 15-minute default window is usually empty for sporadic personal listening; and there is zero host-level monitoring even though disk fill-up (MinIO audio cache on a free-tier volume) is the most likely future outage.

User's confirmed direction: wire Grafana directly to Postgres and replace dead panels with real tables; put music traffic front and center with infra noise in a collapsed row; surface all four priorities (cached songs, recent plays feed, failures with reasons, VPS disk/CPU/RAM); default window 6 hours.

**No API code changes needed** — everything comes from existing Postgres tables (`resolver_tracks`, `resolver_play_events`), existing Prometheus metrics, and two new standard exporter containers.

## Changes

### 1. `compose.prod.yaml`
- `grafana`: add `POSTGRES_PASSWORD` env (datasource provisioning interpolation; also forces recreation on deploy so datasources reload).
- New `node-exporter` service (`prom/node-exporter`, `--path.rootfs=/host`, `/:/host:ro,rslave`, internal only).
- New `cadvisor` service (`gcr.io/cadvisor/cadvisor`, standard read-only binds, internal only).
- `prometheus`: append `--web.enable-lifecycle` — compose only recreates containers whose service definition changed, so editing the bind-mounted `prometheus.yml` alone would never reload scrape config on deploy; the command change forces recreation now and enables `/-/reload` later.
- Dev `compose.yaml`: grafana gets `POSTGRES_PASSWORD: development-only` for datasource parity. node-exporter/cadvisor deliberately prod-only (on Docker Desktop they measure the VM — misleading).

### 2. `deploy/prometheus.yml`
Scrape jobs `node` → `node-exporter:9100` and `cadvisor` → `cadvisor:8080`.

### 3. `deploy/grafana/provisioning/datasources/datasources.yml`
PostgreSQL datasource (uid `Postgres`, db `harmony`, user `harmony`, password via `$POSTGRES_PASSWORD` env interpolation, sslmode disable). Reuses app credentials — Grafana is localhost-bound on a personal box; a dedicated read-only role would need manual SQL on the existing volume. Loki datasource gets explicit `uid: Loki` (previously matched by luck).

### 4. `deploy/grafana/dashboards/resolver.json` — full rework
Default `now-6h`. Layout: at-a-glance stat tiles (cached tracks, cache size, plays 24h, failed tracks, disk used %); music traffic row (`/v1/tracks.*`-filtered rates + cache latency/hit-rate panels); Postgres tables (recent plays, cached tracks); failures row (failure-rate metric, failed-tracks table, warning/error logs via `| json | LogLevel=~"Warning|Error|Critical"`); VPS health row (CPU/RAM/disk from node-exporter, per-container from cadvisor); collapsed infra row (unfiltered HTTP panels, 429 panel, all-services logs). Deleted: Track status distribution, Active leases, MinIO latency, Active requests.

## Verification
1. JSON/YAML parse checks + `docker compose config` with dummy env.
2. Push to `main`, watch `publish images` → `deploy`, confirm `/health/ready`.
3. MCP bridge sanity (`get_recent_plays`, `query_logs`).
4. User refreshes Grafana: stat tiles + VPS health live (proves exporters + scrape reload worked); play a song → recent-plays/cache panels populate.
