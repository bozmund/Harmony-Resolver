$ErrorActionPreference = 'Stop'
docker compose up --build -d
Write-Host 'Resolver: http://localhost:8088  Grafana: http://localhost:3000'
