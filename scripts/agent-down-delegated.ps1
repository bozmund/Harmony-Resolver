$ErrorActionPreference = 'Stop'
docker compose -f compose.yaml -f compose.delegated.yaml down
