$ErrorActionPreference = 'Stop'
$discoveryPidFile = Join-Path $env:TEMP 'harmony-resolver-discovery.pid'
if (Test-Path $discoveryPidFile) {
  $discoveryPid = Get-Content $discoveryPidFile -ErrorAction SilentlyContinue
  if ($discoveryPid) { Stop-Process -Id $discoveryPid -Force -ErrorAction SilentlyContinue }
  Remove-Item -LiteralPath $discoveryPidFile -Force
}
docker compose down
