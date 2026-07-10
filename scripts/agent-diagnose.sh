#!/usr/bin/env bash
set -euo pipefail
curl --fail --silent http://localhost:8088/internal/diagnostics/snapshot
