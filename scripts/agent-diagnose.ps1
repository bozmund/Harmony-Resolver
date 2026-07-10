$ErrorActionPreference = 'Stop'
$snapshot = Invoke-RestMethod http://localhost:8088/internal/diagnostics/snapshot
$health = Invoke-RestMethod http://localhost:8088/health/ready
[ordered]@{ generatedAt=(Get-Date).ToUniversalTime().ToString('o'); health=$health; snapshot=$snapshot; containers=(docker compose ps --format json | ConvertFrom-Json) } | ConvertTo-Json -Depth 12
