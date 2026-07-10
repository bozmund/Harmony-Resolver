#!/usr/bin/env bash
set -euo pipefail
read -r -p 'Delete all Harmony Resolver development data? Type RESET: ' answer
test "$answer" = RESET || { echo 'Reset cancelled'; exit 1; }
docker compose down --volumes
