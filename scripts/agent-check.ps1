$ErrorActionPreference = 'Stop'
dotnet restore Harmony.Resolver.slnx
dotnet build Harmony.Resolver.slnx --no-restore --configuration Release
dotnet test Harmony.Resolver.slnx --no-build --configuration Release
docker compose config --quiet
$composeProject = "harmony-resolver-check-$PID"
$resolverPort = 18000 + ($PID % 1000)
$grafanaPort = 19000 + ($PID % 1000)
$env:RESOLVER_PORT = $resolverPort
$env:GRAFANA_PORT = $grafanaPort
$compose = @('compose', '--project-name', $composeProject)
try {
  & docker @compose up --build -d
  $ready = $false
  foreach ($attempt in 1..60) {
    try { Invoke-RestMethod "http://localhost:$resolverPort/health/ready" | Out-Null; $ready = $true; break } catch { Start-Sleep -Seconds 2 }
  }
  if (-not $ready) { throw 'Resolver did not become ready' }
  $videoId = 'a' + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
  $statuses = 1..5 | ForEach-Object -Parallel {
    try {
      (Invoke-WebRequest "http://localhost:$using:resolverPort/v1/tracks/$using:videoId/audio" `
        -Headers @{ 'X-Forwarded-For' = "192.0.2.$_" } -UseBasicParsing).StatusCode
    }
    catch { [int]$_.Exception.Response.StatusCode }
  } -ThrottleLimit 5
  if (($statuses | Where-Object { $_ -eq 200 }).Count -lt 1) { throw "Expected a completed ingestion, got: $($statuses -join ',')" }
  if (($statuses | Where-Object { $_ -eq 202 }).Count -lt 1) { throw "Expected concurrent followers, got: $($statuses -join ',')" }
  if (($statuses | Where-Object { $_ -notin 200, 202, 429 }).Count -ne 0) { throw "Unexpected response status: $($statuses -join ',')" }
  $range = $null
  foreach ($attempt in 1..10) {
    try {
      $range = Invoke-WebRequest "http://localhost:$resolverPort/v1/tracks/$videoId/audio" `
        -Headers @{ Range = 'bytes=0-9'; 'X-Forwarded-For' = '192.0.2.250' } -UseBasicParsing
      break
    } catch {
      if ([int]$_.Exception.Response.StatusCode -ne 429 -or $attempt -eq 10) { throw }
      Start-Sleep -Milliseconds 500
    }
  }
  if ($range.StatusCode -ne 206 -or $range.RawContentLength -ne 10) { throw 'Range smoke test failed' }
} finally {
  & docker @compose down
  Remove-Item Env:RESOLVER_PORT, Env:GRAFANA_PORT -ErrorAction SilentlyContinue
}
