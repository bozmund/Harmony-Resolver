#!/usr/bin/env bash
set -euo pipefail
dotnet restore Harmony.Resolver.slnx
dotnet build Harmony.Resolver.slnx --no-restore --configuration Release
dotnet test Harmony.Resolver.slnx --no-build --configuration Release
docker compose config --quiet
compose_project="harmony-resolver-check-$$"
export RESOLVER_PORT=$((18000 + ($$ % 1000)))
export GRAFANA_PORT=$((19000 + ($$ % 1000)))
compose=(docker compose --project-name "$compose_project")
trap '"${compose[@]}" down' EXIT
"${compose[@]}" up --build -d
for attempt in {1..60}; do
  if curl --fail --silent "http://localhost:$RESOLVER_PORT/health/ready" >/dev/null; then break; fi
  test "$attempt" -lt 60 || { "${compose[@]}" logs; exit 1; }
  sleep 2
done
video_id="a$(date +%s)"
export video_id
export RESOLVER_PORT
statuses="$(seq 5 | xargs -P5 -I{} sh -c 'curl --silent --output /dev/null --write-out "%{http_code}\n" -H "X-Forwarded-For: 192.0.2.{}" "http://localhost:$RESOLVER_PORT/v1/tracks/$video_id/audio"')"
test "$(printf '%s\n' "$statuses" | grep -c '^200$')" -ge 1
test "$(printf '%s\n' "$statuses" | grep -c '^202$')" -ge 1
test "$(printf '%s\n' "$statuses" | grep -Ev '^(200|202|429)$' | wc -l)" = 0
range_result=""
for attempt in {1..10}; do
  range_result="$(curl --silent --output /dev/null --write-out '%{http_code}:%{size_download}' -H 'X-Forwarded-For: 192.0.2.250' -H 'Range: bytes=0-9' "http://localhost:$RESOLVER_PORT/v1/tracks/$video_id/audio")"
  test "$range_result" = "429:0" || break
  sleep 0.5
done
test "$range_result" = "206:10"
