param(
  [Parameter(Mandatory)]
  [ValidateSet('enable', 'status', 'disable')]
  [string]$Action,
  [int]$Port = 8088
)

$ErrorActionPreference = 'Stop'
$tcpRule = 'Harmony Resolver LAN TCP'
$mdnsRule = 'Harmony Resolver mDNS UDP'

function Get-RuleState {
  $rules = Get-NetFirewallRule -DisplayName $tcpRule, $mdnsRule -ErrorAction SilentlyContinue
  [ordered]@{
    tcp8088 = [bool]($rules | Where-Object DisplayName -eq $tcpRule)
    udp5353 = [bool]($rules | Where-Object DisplayName -eq $mdnsRule)
  }
}

switch ($Action) {
  'enable' {
    if (-not (Get-NetFirewallRule -DisplayName $tcpRule -ErrorAction SilentlyContinue)) {
      New-NetFirewallRule -DisplayName $tcpRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Private | Out-Null
    }
    if (-not (Get-NetFirewallRule -DisplayName $mdnsRule -ErrorAction SilentlyContinue)) {
      New-NetFirewallRule -DisplayName $mdnsRule -Direction Inbound -Action Allow -Protocol UDP -LocalPort 5353 -Profile Private | Out-Null
    }
  }
  'disable' {
    Remove-NetFirewallRule -DisplayName $tcpRule, $mdnsRule -ErrorAction SilentlyContinue
  }
}

Get-RuleState | ConvertTo-Json
