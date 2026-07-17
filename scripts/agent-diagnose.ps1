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
$lanAddress = Get-NetIPAddress -AddressFamily IPv4 |
  Where-Object { $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and $_.IPAddress -notlike '169.254*' } |
  Sort-Object { if ($_.InterfaceAlias -match 'Wi-?Fi') { 0 } else { 1 } } |
  Select-Object -First 1 -ExpandProperty IPAddress
$firewall = & (Join-Path $PSScriptRoot 'lan-firewall.ps1') status | ConvertFrom-Json
$discoveryPidFile = Join-Path $env:TEMP 'harmony-resolver-discovery.pid'
$discoveryRunning = $false
if (Test-Path $discoveryPidFile) {
  $discoveryPid = Get-Content $discoveryPidFile -ErrorAction SilentlyContinue
  $discoveryRunning = [bool](Get-Process -Id $discoveryPid -ErrorAction SilentlyContinue)
}
[ordered]@{
  generatedAt=(Get-Date).ToUniversalTime().ToString('o')
  health=$health
  snapshot=$snapshot
  phone=[ordered]@{
    url=if ($lanAddress) { "http://${lanAddress}:8088" } else { $null }
    discoveryService='_harmony-resolver._tcp'
    discoveryRunning=$discoveryRunning
    firewall=$firewall
  }
  containers=$containers
} | ConvertTo-Json -Depth 12
