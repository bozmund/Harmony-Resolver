#!/usr/bin/env bash
set -euo pipefail
curl --fail --silent http://localhost:8080/internal/diagnostics/snapshot
