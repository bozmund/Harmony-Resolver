# Rider One-Click Development Configurations

## Summary

Add two shared Rider launch options:

- `Harmony Resolver API` for fast local debugging with deterministic fixtures.
- `Harmony Resolver Full Stack` for Docker Compose, two replicas, dependencies, Nginx, and observability.

Both configurations will open an interactive Swagger UI automatically.

## Implementation Changes

- Add an ASP.NET launch profile for `Harmony.Resolver.Api` using Development mode, deterministic extraction, a fixed localhost port, browser launch, and `/swagger`.
- Add Swagger UI backed by the existing generated `/openapi/v1.json` document. Expose Swagger and OpenAPI only in Development.
- Add shared Rider `.run` configurations:
  - API profile running under Rider’s debugger.
  - Docker Compose profile starting the complete stack and opening `http://localhost:8080/swagger`.
- Keep raw OpenAPI available at `/openapi/v1.json`.
- Add the launch profiles and Rider configurations to the solution’s `Solution Items`.
- Document Rider startup choices, Swagger URLs, Grafana URL, stopping the stack, and expected deterministic test behavior.

## Test Plan

- Confirm Rider recognizes both shared configurations.
- Run the API profile and verify Swagger opens, health requests succeed, and breakpoint debugging works.
- Run the full-stack profile and verify Swagger through Nginx, both replicas, PostgreSQL, MinIO, Valkey, Prometheus, Loki, and Grafana start successfully.
- Verify Swagger is unavailable outside Development.
- Run formatting, release build, all tests, and `docker compose config --quiet`.

## Assumptions

- Fast API debugging continues to use the current in-memory catalog and deterministic extractor.
- Full-stack Swagger is exposed through Nginx at port `8080`; local API debugging uses a separate fixed port.
- HTTPS remains the production responsibility of Nginx; local Rider profiles use HTTP to avoid development-certificate friction.
