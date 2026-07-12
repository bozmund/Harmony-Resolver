#!/usr/bin/env bash
set -euo pipefail
docker info >/dev/null 2>&1 || { echo 'Docker is unavailable. Start Docker and wait for its engine.' >&2; exit 1; }
docker compose ps --services --status running | grep -qx nginx || {
  echo 'Harmony Resolver is not running. Start it with scripts/agent-up.sh first.' >&2
  exit 1
}

# Get the diagnostic snapshot from the internal API
snapshot_json=$(docker compose exec -T nginx wget -qO- http://api-1:8080/internal/diagnostics/snapshot)

# Get the health check through the public Nginx port
health_json=$(curl --silent --fail http://localhost:8088/health/ready 2>/dev/null || echo '{"status":"not_ready","dependencies":{}}')

# Get container status as JSON
containers_json=$(docker compose ps --format json | jq '[.[] | {service: .Service, state: .State, health: .Health, image: .Image}]')

# Combine into a single structured JSON output (matching the PowerShell version)
jq --null-input \
  --arg generated_at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --argjson snapshot "$snapshot_json" \
  --argjson health "$health_json" \
  --argjson containers "$containers_json" \
  '{ generatedAt: $generated_at, health: $health, snapshot: $snapshot, containers: $containers }'
