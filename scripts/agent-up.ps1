$ErrorActionPreference = 'Stop'
docker compose up --build -d
$discoveryPidFile = Join-Path $env:TEMP 'harmony-resolver-discovery.pid'
if (Test-Path $discoveryPidFile) {
  $oldPid = Get-Content $discoveryPidFile -ErrorAction SilentlyContinue
  if ($oldPid) { Stop-Process -Id $oldPid -Force -ErrorAction SilentlyContinue }
}
$discovery = Start-Process dotnet -ArgumentList @(
  'run', '--project', 'src/Harmony.Resolver.Discovery/Harmony.Resolver.Discovery.csproj',
  '--no-launch-profile'
) -WorkingDirectory $PSScriptRoot\.. -WindowStyle Hidden -PassThru
$discovery.Id | Set-Content $discoveryPidFile
Write-Host 'Resolver: http://localhost:8088  Grafana: http://localhost:3000'
Write-Host 'Phone discovery: _harmony-resolver._tcp (run scripts/lan-firewall.ps1 enable as Administrator once)'
