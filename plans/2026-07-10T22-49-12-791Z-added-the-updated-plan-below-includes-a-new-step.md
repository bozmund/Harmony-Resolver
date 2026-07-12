# Accepted Plan

Saved: 2026-07-10T22:49:12.791Z

Added. The updated plan below includes a new step 10 to fix the `read`-on-directory mistake, and the tool-use pattern now cascades into the pre-handoff validation. Here is the complete revised plan:

---

## Plan

1. **Add Nix flake OCI image outputs** — Define `packages.api-image` and `packages.mcp-image` using `pkgs.dockerTools.buildImage` (or `streamLayeredImage`) that produce equivalent images to Dockerfile and Dockerfile.mcp. Update the CI workflow to optionally build/push Nix images alongside Docker ones. Add a NixOS integration test or check that uses the module with the built images.

2. **Fill Valkey-backed test coverage** — Add an integration test project that uses `Testcontainers.Redis` to start a Valkey container and exercises `ValkeyQuotaService` for concurrent ingestion and response limits. Also add a multi-replica concurrent lease test that exercises the Valkey-coordinated path alongside PostgreSQL leases.

3. **Add disconnect-continuation test** — In the integration test suite, add a test that initiates an extraction, aborts the HTTP client mid-stream, waits, then verifies the track transitioned to `Ready` (not `Failed`) and is retrievable via range request. This validates the architecture's key invariant.

4. **Add ExpiredObjectJanitor test** — Write a unit test that creates tracks with past `ExpiresAt`, verifies `ListExpiredAsync` returns them, simulates object deletion, and confirms `DeleteExpiredAsync` cleans up. Optionally use `Testcontainers.PostgreSql` for a real DB round-trip.

5. **Add JWT validation tests** — Add integration tests with a self-signed JWK token (or mock Auth0 configuration) that verify:
   - Valid token with `diagnostics:read` scope → MCP tool call succeeds
   - Valid token without permissions → 403
   - Valid token → authenticated quota limit applied
   - Expired/malformed token → 401

6. **Add MCP audit trail test** — After calling an MCP tool, verify a `DiagnosticAuditEntity` was written with the correct HMAC subject hash and tool name. Test that the subject hash is not reversible to the original sub/ip claim.

7. **Add `RequestIdentityResolver` tests** — Unit-test the HMAC identity resolution with known inputs: authenticated subject, anonymous IP, missing IP, edge-case HMAC keys. Assert output format and deterministic behavior.

8. **Add fault injection tests** — For each of the 9 fault profiles (`extractor-timeout`, `malformed-metadata`, `partial-ffmpeg-output`, `client-disconnect`, `minio-failure`, `postgresql-lease-loss`, `valkey-outage`, `replica-crash`, `slow-downstream`):
   - Enable via `/internal/faults/{profile}`
   - Call the relevant endpoint
   - Assert the expected error code or behavior
   - Disable and verify normal operation resumes

9. **Enrich Grafana dashboard** — Add panels for:
   - Track status distribution (pie chart or stat)
   - Active lease count over time
   - Ingestion quota exhaustion rate (derived from 429 responses)
   - Object store operation latency (MinIO client metrics from OpenTelemetry)
   - Extraction failure rate by failure code

10. **Add OTLP endpoint to Alloy** — Configure Alloy to accept OTLP traces and forward them to Loki (or Tempo if added). Wire the API's OpenTelemetry trace exporter to the Alloy OTLP endpoint. This closes the observability loop between distributed traces and logs.

11. **Fix agent-diagnose.sh completeness** — Read, validate, and repair the Bash version of `agent-diagnose.sh`. Ensure the JSON output shape matches `agent-diagnose.ps1` and all tool calls are bounded and redacted. Test that neither script attempts to `read` a directory as if it were a file (use `ls` for directories, `read` for regular files).

12. **Final pre-handoff validation** — Run `scripts/agent-check.ps1`, fix any failures. Validate that:
   - All new tests are integrated into `ci.yml` and run in CI
   - Test documentation is updated
   - Every tool call in scripts uses the right verb (`ls` for directories, `read` for files)
   - No tool in the project accidentally calls `read` on a directory path
