$ErrorActionPreference = 'Stop'

# Starts the normal development stack plus a localhost-only RabbitMQ broker and configures
# both resolver replicas to enqueue delegated ingestion jobs.
docker compose -f compose.yaml -f compose.delegated.yaml up --build -d

Write-Host 'Resolver: http://localhost:8088/swagger'
Write-Host 'RabbitMQ management: http://localhost:15672 (harmony / development-only-rabbitmq)'
Write-Host 'Start the local downloader with the commands in README.md.'
