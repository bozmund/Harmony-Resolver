# RabbitMQ job notifications for the downloader fleet

## Context

The delegated-extraction feature (see `plans/2026-07-19_0138_delegated_extraction_downloader_fleet.md`,
already implemented) has the resolver enqueue a job on each cache miss and home downloader agents pull
work by polling `POST /v1/worker/jobs/claim` on a fixed interval. Polling is wasteful and adds latency
(a job waits up to `DOWNLOADER_POLL_SECONDS` before anyone looks at it).

**Goal:** the resolver publishes a message whenever a song needs downloading, and the downloaders
subscribe to a **RabbitMQ** queue and consume those messages — reacting instantly instead of polling.

**Decisions (from the user):**
- Broker: **RabbitMQ** (chosen over an SSE-over-HTTPS transport, accepting a new public port).
- Downloader stays a **console worker** (`Harmony.Resolver.Downloader`) with a RabbitMQ consumer — not
  an Azure Function (a Function only makes sense self-hosted, since Azure's datacenter IPs are
  bot-blocked just like the VPS; and long yt-dlp subprocesses fit the Functions model poorly).

## Design: the message is a doorbell, not a work item

The message just announces "there is pending work." On receipt a downloader calls the **existing**
`POST /v1/worker/jobs/claim` (claim-any-pending) and drains jobs until it gets `204`, then waits for the
next message. The Postgres lease remains the single arbiter of ownership, so duplicate, reordered, or
lost messages are all harmless. This reuses the entire worker protocol already built and tested
(`claim` → `heartbeat` → `PUT audio` → `fail`) unchanged — RabbitMQ only replaces the downloader's idle
`Task.Delay` poll, and a server-side republisher is the safety net (no client-side timer poll needed).

Queue topology: one **durable** work queue (`harmony.ingest.jobs`); RabbitMQ round-robins each doorbell
to one connected consumer (competing consumers), which drains the backlog via claim-any. Message body is
`{"videoId":"…"}` for observability; the downloader ignores the id and claims whatever is oldest-pending.

Auth stays layered: RabbitMQ credentials authorize *consuming the doorbell*; the existing Auth0
`tracks:ingest` M2M token authorizes *uploading audio* over HTTPS (multi-MB audio must not go through the
broker). A downloader therefore holds two credentials — RabbitMQ user + Auth0 M2M — both revocable.

## Resolver changes — `src/Harmony.Resolver.Api`

1. **Package**: add `RabbitMQ.Client` to `Directory.Packages.props` and reference it in the API csproj.
2. **Config** — new `Configuration/RabbitMqOptions.cs` (bound from `RabbitMq` section): `Uri`
   (e.g. `amqp://user:pass@rabbitmq:5672`), `QueueName` (default `harmony.ingest.jobs`).
3. **Notifier abstraction** — `Abstractions/IJobNotifier.cs` (`Task NotifyAsync(string videoId, …)`),
   with `Infrastructure/Messaging/RabbitMqJobNotifier.cs` (declares the durable queue once, publishes a
   persistent message) and a `NoopJobNotifier` used when RabbitMq is unconfigured (keeps Inline/dev and
   the existing tests working without a broker).
4. **DI** (`Program.cs`): register `RabbitMqJobNotifier` when `ExtractionMode == Delegated` **and**
   `RabbitMq:Uri` is set, else `NoopJobNotifier`. Register the `JobRepublisher` hosted service (below)
   under the same condition.
5. **Publish on enqueue** — in `DistributedResolverEndpoints.GetAudioAsync`, the Delegated cold-miss
   branch (added by the prior plan) already calls `tracks.EnqueueAsync(videoId)`; inject `IJobNotifier`
   and call `notifier.NotifyAsync(videoId)` right after. Publish failures are logged, never fail the
   listener response (the republisher will re-ring).
6. **Server-side safety net** — new `Infrastructure/Storage/JobRepublisher.cs` (`BackgroundService`,
   modeled on `StuckJobReaper`): every ~30s, re-ring the doorbell for every job still `ingesting` with no
   live lease. Needs a new repo method `ListPendingJobsAsync(now, limit)` on `ITrackRepository` /
   `PostgresTrackRepository` (EF query: `status='ingesting'` and no lease with `expires_at > now`). This
   covers a broker restart, a lost message, or all downloaders having been offline when a job was queued.

## Downloader changes — `src/Harmony.Resolver.Downloader`

1. **Package**: `RabbitMQ.Client`.
2. **Config** (`DownloaderOptions` / env): `RABBITMQ_URI` (`amqps://user:pass@host:5671/…` from home),
   `RABBITMQ_QUEUE` (default `harmony.ingest.jobs`). When unset, fall back to the current poll loop so the
   agent still works without a broker.
3. **Consumer** — replace the idle `Task.Delay(pollSeconds)` in `DownloaderWorker.RunLoopAsync` with a
   RabbitMQ `AsyncEventingBasicConsumer` on the durable queue (QoS `prefetch = MaxParallel`,
   `AutomaticRecoveryEnabled = true`). On each message: drain via the existing
   `ResolverWorkerClient.ClaimAsync` + `ProcessAsync` loop until `ClaimAsync` returns null, then ack. On
   consumer start **and on every reconnect**, do one claim-any drain to catch anything enqueued while
   disconnected. Everything downstream of the claim (`ProcessAsync`, `YtDlpDownloader`, heartbeat, upload,
   fail) is reused verbatim.

## Deployment — `compose.prod.yaml` + `deploy/`

1. **`rabbitmq` service** in `compose.prod.yaml` (`rabbitmq:4-management-alpine`), durable volume,
   healthcheck (`rabbitmq-diagnostics -q ping`). It listens on `5672` (plaintext AMQP, **internal Docker
   network only**, used by `api-1`/`api-2`) and `5671` (AMQPS, **published to the host** for home
   downloaders). Add `RabbitMq__Uri: amqp://harmony:${RABBITMQ_PASSWORD}@rabbitmq:5672` to the shared
   `api-environment`, and `RABBITMQ_DEFAULT_USER/PASS` to the rabbitmq service from the env file.
2. **TLS for 5671** — reuse Caddy's existing Let's Encrypt cert for `harmony-resolver.duckdns.org`:
   mount the `caddy_data` volume read-only into rabbitmq and point `rabbitmq.conf`
   (`ssl_options.certfile`/`keyfile`) at the cert/key Caddy writes under
   `/data/caddy/certificates/.../harmony-resolver.duckdns.org/`. Document that RabbitMQ needs a restart to
   pick up a renewed cert (acceptable for a personal fleet; Let's Encrypt renews ~every 60 days).
3. **Firewall** — open TCP `5671` on the host firewall in `deploy/bootstrap-oracle-vps.sh` (mirroring the
   existing 80/443 block) and note the matching Oracle Security List rule in its reminder + the README.
   Port `5672` is never exposed to the host.
4. **Secrets/env** — add `RABBITMQ_PASSWORD` to `deploy/production.env.example`, the bootstrap secret
   generator, and `.env.example`. Add `RABBITMQ_URI` to `src/Harmony.Resolver.Downloader/.env.example`
   and `deploy/downloader/.env.example`.
5. **README** — document that Delegated mode now needs the rabbitmq service, port 5671, a per-downloader
   RabbitMQ user (`rabbitmqctl add_user` / `set_permissions`; revoke with `delete_user`), and that the
   agent needs both RabbitMQ and Auth0 credentials.

## Out of scope / notes

- Uploading audio through the broker (kept as HTTPS `PUT`; brokers are for control messages, not payloads).
- "Message IS the claim" (consumer-group ownership instead of the DB lease) — considered and rejected: it
  would re-plumb the tested lease machinery into stream acks for no real gain given the doorbell design.
- The old `jobs/claim` pull endpoint stays (used by the doorbell drain and as a manual/fallback path).

## Verification

1. **Unit/e2e (no broker)** — extend `DelegatedIngestionTests` with a fake `IJobNotifier` registered via
   `ConfigureServices`; assert a cache miss both enqueues the DB job and calls `NotifyAsync(videoId)`. Add
   a `PostgresTrackRepositoryTests` case for `ListPendingJobsAsync` (returns unleased `ingesting` jobs,
   excludes ready/leased). `NoopJobNotifier` keeps all existing tests broker-free.
2. **Broker round-trip** — add `Testcontainers.RabbitMq` and an integration test: publish via
   `RabbitMqJobNotifier`, consume with a `RabbitMQ.Client` consumer, assert the videoId arrives on the
   durable queue.
3. **Local end-to-end** — `docker compose -f compose.prod.yaml up` with `Resolver__ExtractionMode=Delegated`
   and rabbitmq; run the downloader with `RABBITMQ_URI` pointing at it. `GET /v1/tracks/{id}/audio` → `202`;
   confirm the downloader wakes **immediately** (not after the poll interval), claims, and uploads; a second
   `GET` → `200` `OggS`. Kill the downloader mid-queue, confirm `JobRepublisher` re-rings and a restarted
   downloader drains the backlog.
4. **Diagnostics (MCP)** — `get_system_snapshot` shows the job move `ingesting → ready`; `query_logs` shows
   the publish + consume lines.
