$ErrorActionPreference = 'Stop'
docker compose up --build -d
Write-Host 'Resolver: http://localhost:8080  Grafana: http://localhost:3000'
