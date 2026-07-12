$ErrorActionPreference = 'Stop'
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is unavailable. Start Docker Desktop and wait for its Linux engine.' }
$running = docker compose ps --services --status running
if ($LASTEXITCODE -ne 0 -or $running -notcontains 'nginx') {
  throw 'Harmony Resolver is not running. Start it with scripts/agent-up.ps1 first.'
}
$snapshotJson = docker compose exec -T nginx wget -qO- http://api-1:8080/internal/diagnostics/snapshot
$snapshot = $snapshotJson | ConvertFrom-Json
$health = Invoke-RestMethod http://localhost:8088/health/ready
$containers = docker compose ps --format json | ConvertFrom-Json | ForEach-Object {
  [ordered]@{ service=$_.Service; state=$_.State; health=$_.Health; image=$_.Image }
}
[ordered]@{ generatedAt=(Get-Date).ToUniversalTime().ToString('o'); health=$health; snapshot=$snapshot; containers=$containers } | ConvertTo-Json -Depth 12
