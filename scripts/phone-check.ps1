param([int]$Port = 8088)
$ErrorActionPreference = 'Stop'
$address = Get-NetIPAddress -AddressFamily IPv4 |
  Where-Object { $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and $_.IPAddress -notlike '169.254*' } |
  Sort-Object { if ($_.InterfaceAlias -match 'Wi-?Fi') { 0 } else { 1 } } |
  Select-Object -First 1 -ExpandProperty IPAddress
if (-not $address) { throw 'No LAN IPv4 address was found.' }
$url = "http://${address}:$Port/health/ready"
$health = Invoke-RestMethod $url
[ordered]@{ url=$url; status=$health.status; dependencies=$health.dependencies } | ConvertTo-Json -Depth 5
