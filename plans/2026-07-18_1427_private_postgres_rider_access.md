# Private PostgreSQL access for Rider

## Context

Rider needs direct PostgreSQL access from a developer computer, while the production database must remain unavailable from the public internet and Oracle Cloud firewall changes must not be required.

## Changes

1. Add a `postgres-relay` service to `compose.prod.yaml`.
   - It uses `alpine/socat` on the existing Docker network.
   - It forwards Docker-internal `postgres:5432` to port `15432`.
   - Its host binding is explicitly `127.0.0.1:15432`, so it is only reachable from the VPS itself or through SSH port forwarding.
   - It waits for PostgreSQL readiness and restarts with the production stack.
2. Document the standard SSH tunnel and Rider PostgreSQL connection settings in the README.

## Verification

1. Validate `compose.prod.yaml` with dummy required environment values.
2. Run the repository agent check before handoff.
3. After deployment, establish `ssh -L 15432:localhost:15432 ...`, connect Rider to `localhost:15432`, and verify that no Oracle/host firewall rule exposes port `5432` or `15432`.
