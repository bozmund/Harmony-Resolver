# Delegated extraction: credentialed downloader fleet

## Context

Harmony Resolver runs on an Oracle VPS whose **datacenter IP is flagged by YouTube's bot
detection**. On every cache miss the server extracts audio *inline* inside the HTTP
request (`DistributedResolverEndpoints.cs:98-169`), and both adapters fail there:

- **yt-dlp** → `Sign in to confirm you're not a bot` (IP-range bot gate; residential/mobile IPs mostly pass, datacenter IPs don't — confirmed by upstream [YoutubeExplode#794](https://github.com/Tyrrrz/YoutubeExplode/issues/794)).
- **youtube-explode (C#)** → `Failed to extract the cipher manifest` (an *open, unresolved* upstream bug, [YoutubeExplode#902](https://github.com/Tyrrrz/YoutubeExplode/issues/902)).

Production currently has **0 successful extractions, 2 failures**. Neither an Auth0 token
nor YouTube `visitorData` fixes this — the block is IP reputation, not credentials.

**Goal:** move extraction off the datacenter IP onto a small fleet of downloaders the
operator personally runs on residential IPs, without exposing the fleet to the public or
enlisting strangers' devices. The server becomes coordinator + normalizer + cache; the
fleet does the actual YouTube fetching.

## Decisions (from the user)

| Question | Decision |
|---|---|
| Who may be a downloader | A vetted fleet the operator runs/manages; **not** anonymous or auto-enrolled end-user phones |
| Downloader form factor | **Headless home agent** (always-on, residential IP) |
| Work trigger | **Hybrid**: on-demand job queue (primary) + opportunistic (future) |
| Worker auth | **Auth0 M2M** with a `tracks:ingest` scope; revoke by disabling the client |
| Content integrity | Operator trusts own fleet → no fingerprint/quorum; server still force-normalizes |
| Cold-miss listener UX | **Queue + poll (async)**: enqueue job, return `202`, app polls until filled |

## Architecture

```
Listener ──GET /v1/tracks/{id}/audio──> Resolver (VPS)
   │  cache hit  → 200/206 stream (unchanged)
   │  cache miss → EnqueueAsync(id); 202 ingestion_in_progress ; listener polls
   │
Home agent (residential IP, authenticated tracks:ingest)
   ├─ POST /v1/worker/jobs/claim         → { videoId, leaseToken, expiresAt } | 204
   ├─ (yt-dlp -f bestaudio → raw bytes)
   ├─ POST /v1/worker/jobs/{id}/heartbeat (RenewLeaseAsync for long downloads)
   └─ PUT  /v1/worker/tracks/{id}/audio  (raw body)
                                          → server FfmpegNormalizer → Ogg Opus
                                          → objects.PutAsync + tracks.MarkReadyAsync
   next listener poll → cache hit → served
```

**Key reuse:** the existing lease table (`resolver_ingestion_leases`: `video_id` PK,
`owner_id`, expiry, steal-if-expired) is already a race-safe distributed claim protocol,
and `RenewLeaseAsync` already exists but is **currently unused** — it becomes the worker
heartbeat. A **pending job = a `resolver_tracks` row in `ingesting` status with no active
lease**; **claimed = same row with an active lease**. This reuses the existing status enum
(`ingesting`/`ready`/`failed`) — **no status-enum migration needed**.

## Server changes — `src/Harmony.Resolver.Api`

1. **Config** — `Configuration/ResolverOptions.cs`: add
   `ExtractionMode { Inline, Delegated }` (default `Inline`, so dev/LAN is unchanged).
   Production/VPS sets `Resolver__ExtractionMode=Delegated`.

2. **Register `FfmpegNormalizer` when Delegated** — `Program.cs:37-49`. Today it is only
   registered when `!UseFakeExtractor`; the worker-ingest path needs it server-side even
   though the server itself won't run yt-dlp/youtube-explode. In Delegated mode, register
   `FfmpegNormalizer` (and skip registering the YouTube adapters — the server never
   extracts).

3. **New repository methods** — `Abstractions/ITrackRepository.cs` +
   `Infrastructure/Persistence/PostgresTrackRepository.cs` (mirror the existing raw-SQL
   style of `TryAcquireLeaseAsync`):
   - `EnqueueAsync(videoId, ct)` — `INSERT ... status='ingesting' ON CONFLICT DO NOTHING`;
     also resets a `failed` row to `ingesting` when its `retry_after` has passed.
     Idempotent; no-op if already `ready`/`ingesting`.
   - `ClaimJobAsync(workerId, leaseDuration, ct) → IngestionLease?` — picks one pending job
     with `SELECT ... WHERE status='ingesting' AND (no lease OR lease expired) ORDER BY
     created_at LIMIT 1 FOR UPDATE SKIP LOCKED`, then upserts a lease for the worker.
     `SKIP LOCKED` gives clean multi-worker concurrency.
   - Reuse as-is: `RenewLeaseAsync` (heartbeat), `MarkReadyAsync` (commit — its existing
     `owner_id` guard already rejects a lost/stolen lease), `MarkFailedAsync` (fail).

4. **New worker endpoints** — new `Endpoints/WorkerIngestionEndpoints.cs`, all
   `.RequireAuthorization("tracks:ingest")`:
   - `POST /v1/worker/jobs/claim` → `ClaimJobAsync`; returns `{videoId, leaseToken=ownerId,
     expiresAt}` or `204` when the queue is empty.
   - `POST /v1/worker/jobs/{videoId}/heartbeat` (leaseToken) → `RenewLeaseAsync`.
   - `PUT /v1/worker/tracks/{videoId}/audio` (leaseToken; raw audio body) → stream body to
     a temp file bounded by `MaxObjectMiB` (`413` if exceeded), run
     `FfmpegNormalizer.NormalizeAsync` → canonical Ogg Opus, then the **existing** commit
     path: `objectKey = tracks/{videoId}.ogg`, SHA-256 ETag, `objects.PutAsync`,
     `tracks.MarkReadyAsync(lease, …)`.
   - `POST /v1/worker/tracks/{videoId}/fail` (leaseToken, code) → `MarkFailedAsync`.

5. **Auth policy** — `Program.cs` (resolver API already wires optional Auth0 JWT at
   99-163): add a `tracks:ingest` policy that asserts the `permissions`/`scope` claim
   contains `tracks:ingest`, mirroring the MCP service's `diagnostics:read` policy
   (`Mcp/Program.cs:22-27`). Apply `RequireAuthorization` **only** to the worker
   endpoints; listener endpoints stay anonymous.

6. **Cold-miss handler** — `DistributedResolverEndpoints.GetAudioAsync`: when
   `ExtractionMode == Delegated`, replace the inline-extraction block (lines 76-169) with
   `await tracks.EnqueueAsync(videoId)` (guarded by the existing
   `TryConsumeIngestionAsync` quota so a listener can't flood the queue) followed by the
   existing `202 ingestion_in_progress` response. The `ready`/`failed`/`ingesting` branches
   (lines 52-74) are unchanged. `Inline` mode keeps today's behavior verbatim.

7. **Job reaper** — new `IHostedService` alongside `ExpiredObjectJanitor`
   (`Infrastructure/Storage/`): periodically mark a job `failed` (with a `retry_after`)
   when it has sat `ingesting` past a max age / repeatedly claimed-and-expired, so
   listeners polling a track no worker can fetch eventually get a definitive `502` instead
   of polling forever.

8. **(Optional) migration** — a partial index `ix_resolver_tracks_pending` on
   `(created_at) WHERE status='ingesting'` to keep `ClaimJobAsync` cheap. No column/enum
   changes.

## Downloader agent — new `src/Harmony.Resolver.Downloader`

A .NET `BackgroundService` console app the operator runs at home:

- Obtains an Auth0 **M2M** token (`tracks:ingest` scope), reusing the `Auth0TokenProvider`
  pattern from `src/Harmony.Resolver.McpBridge`.
- Loop: `POST /v1/worker/jobs/claim`. On a job → shell out to **yt-dlp**
  (`-f bestaudio/best -o -`, residential IP → bot gate passes), heartbeat during long
  downloads, then `PUT` the **raw** bytes to `/v1/worker/tracks/{id}/audio`. On failure →
  `POST …/fail`. On empty queue (`204`) → short sleep, repeat.
- **v1 relies on yt-dlp alone** (the C# youtube-explode fallback is upstream-broken; on a
  residential IP yt-dlp is sufficient). Config: resolver base URL, Auth0 M2M creds, poll
  interval, max parallel jobs, yt-dlp path. Ship a compose/systemd unit for home use.

**Format:** worker uploads raw yt-dlp output (opus-in-webm / m4a); the **server** normalizes
to Ogg Opus — no worker-side ffmpeg, and the existing object-key/ETag/commit path is reused
unchanged.

## App change — `Harmony-Music` (minimal)

`ResolverPlaybackClient._openAt()` already handles `202`/`Retry-After`, but only **4
retries** — a cold fetch routed through a worker can take longer. Raise the retry budget /
honor a longer `Retry-After` for the cold-miss case so the listener waits long enough for
the fleet to fill the cache. (Phone-as-worker is out of scope for this iteration per the
headless-agent decision.)

## Trust / security

- Downloaders authenticate with `tracks:ingest`; the operator trusts the fleet, so no
  fingerprint/quorum. Revoke a node by disabling its Auth0 M2M client.
- Server still **force-normalizes** every upload (guarantees decodable Opus, enforces
  `MaxDurationMinutes`/`MaxObjectMiB`) — the stored artifact and its ETag stay
  server-controlled, never caller-supplied.
- Worker endpoints are gated; listener endpoints remain anonymous. Nginx already blocks
  `/internal/*` publicly — confirm `/v1/worker/*` is reachable by the fleet but consider
  restricting it the same way if the fleet can reach an internal path.

## Out of scope (future)

- Opportunistic phone uploads (the "both" trigger's second half) — would reuse the same
  `PUT` endpoint from the app after a local play.
- Sharing the C# extractor adapters as a library so the agent can run youtube-explode too.
- Multi-region fleet / load-based job routing.

## Verification

1. **Unit** — `EnqueueAsync` idempotency; `ClaimJobAsync` single-winner under N concurrent
   workers (`FOR UPDATE SKIP LOCKED`); heartbeat renew; commit via `MarkReadyAsync` owner
   guard. Extend `PostgresTrackRepositoryTests`.
2. **Local e2e** — bring up the stack with `Resolver__ExtractionMode=Delegated`; run the
   agent locally with a dev `tracks:ingest` token. `GET /v1/tracks/{id}/audio` → `202`;
   watch the agent claim + upload; poll → `200`, body starts with `OggS`, size ≫ 5 KB.
3. **Auth** — worker endpoints reject tokens lacking `tracks:ingest` (`401`/`403`);
   listener endpoints still serve anonymously.
4. **Diagnostics (MCP)** — `get_system_snapshot` shows the job move `ingesting → ready`;
   the reaper surfaces abandoned jobs in `list_failed_ingestions`.
5. **VPS** — deploy; confirm the server no longer spawns yt-dlp (no datacenter bot-check
   failures); the home agent fills the cache; `get_recent_plays` begins showing served
   tracks.
