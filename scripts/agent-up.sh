#!/usr/bin/env bash
set -euo pipefail
docker compose up --build -d
printf 'Resolver: http://localhost:8080  Grafana: http://localhost:3000\n'
