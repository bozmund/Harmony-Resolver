$ErrorActionPreference = 'Stop'
$first = .\scripts\lan-firewall.ps1 status | ConvertFrom-Json
$second = .\scripts\lan-firewall.ps1 status | ConvertFrom-Json
if ($first.tcp8088 -ne $second.tcp8088 -or $first.udp5353 -ne $second.udp5353) {
  throw 'Firewall status is not idempotent.'
}
$phone = .\scripts\phone-check.ps1 | ConvertFrom-Json
if ($phone.status -ne 'ready' -or $phone.url -notmatch '^http://[^/]+:8088/health/ready$') {
  throw 'Phone connectivity check failed.'
}
[ordered]@{ firewall=$first; phone=$phone } | ConvertTo-Json -Depth 6
