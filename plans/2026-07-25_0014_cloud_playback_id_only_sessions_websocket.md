# Cloud playback sessions: fast id-only resolution, real queues, live progress

Accepted 2026-07-25. Spans Harmony-Music, Harmony-Cloud and Harmony-Resolver.

## Context

Cloud playback sessions (one account, one audio target, other devices as controllers) work
end-to-end but are unusable in practice for three reasons:

1. **Handoff is slow.** The target resolves a song via `MusicService.getSongWithId`
   (`lib/services/music_service.dart:592`), which issues a `youtubei/v1/player` call,
   **discards the `videoDetails` it just received**, then does a `next` call plus
   continuations to fetch 25 radio tracks — 2-4 sequential round trips to obtain one
   `MediaItem`. The id-keyed `SongsCache` Hive box, which already holds full `MediaItem`s, is
   never consulted on this path. `_sendRequest` also retries infinitely with no backoff.
2. **The queue collapses to one song.** The target does `updateQueue([current])`
   (`lib/services/cloud/cloud_playback_receiver.dart:209`, `:235`) and tracks the rest only as
   bare ids in `_sessionQueueIds`. The controller does `currentQueue..clear()..add(song)`
   (`lib/ui/player/player_controller.dart:144-149`). Consequence: next/prev are computed as
   `isLastSong = currentQueue.last.id == currentSong.id`, so they render **disabled** on both
   devices.
3. **The controller shows no live position.** `portablePlaybackState()` publishes
   `positionMs` with no duration and no timestamp, sampled at 1 Hz, and the controller has no
   interpolator — the bar sits still or jumps. `total` is never set at all.

Underneath all three: **nothing in the Flutter app ever connects to `PlaybackHub`**, so
`IsRealtimeConnected` is permanently `false` in production. Every command goes down the FCM
path, and Windows (no FCM) gets nothing but a 1 s REST poll. Meanwhile the target `POST`s
full session state every second, and that handler does `Sequence++` plus a full `jsonb`
rewrite (`Harmony-Cloud src/Harmony.Cloud.Api/Program.cs:338-353`).

**Outcome:** handoff feels instant, both devices show the complete queue with visible loading
state, and the non-playing device shows a smoothly advancing progress bar — driven by a real
WebSocket instead of polling.

## Locked decisions

| Decision | Choice |
|---|---|
| Metadata source | **Both, raced** — client fast path + a new Resolver metadata API (does not exist today) |
| Transport | **Raw WebSocket**, `PlaybackHub`/SignalR deleted |
| Poll fallback | **None** — WebSocket only |
| Session payload | **Ordered ids only**, lazy resolve |
| Target backfill | Play now, backfill whole queue in background, **loading always visible** |
| Unresolved rows | Skeleton rows, backfilled in place |
| Resolver backfill | Lazy fill on read |
| Compat | None needed — rolling release, no stable clients have this feature |
| Scope | All three repos |

> `signalr_netcore` was rejected: v1.4.4, unverified uploader, and its own page recommends
> downgrading to a 2021 build on problems. `web_socket_channel` is published by
> `tools.dart.dev`, supports Windows + Android, and accepts an `Authorization` header on the
> upgrade request on dart:io platforms — so no token in the query string.

---

## 1. Harmony-Resolver — new metadata capability

The resolver stores **no metadata whatsoever** today: `TrackEntity` is
`VideoId, Status, ObjectKey, ContentLength, ETag, FailureCode, RetryAfter, LastAccessedAt,
ExpiresAt, Priority, IngestionKind`. This is all net-new.

**Schema.** Add nullable columns to `resolver_tracks`: `title varchar(300)`,
`artists jsonb`, `album varchar(300)`, `duration_seconds int`, `thumbnail_url varchar(500)`,
`metadata_updated_at timestamptz`. Migrations here are **hand-written**, not scaffolded —
follow `Migrations/20260720013000_BackupCandidates.cs:15-21` and update
`ResolverDbContextModelSnapshot.cs` by hand. Add properties to
`Infrastructure/Persistence/Entities/TrackEntity.cs` and `HasColumnName` lines near
`ResolverDbContext.cs:33`.

**Repository.** `PostgresTrackRepository` gains `GetMetadataAsync`,
`GetMetadataBatchAsync` (one `WHERE video_id = ANY(...)` round trip), and `SetMetadataAsync`.
Keep `SetMetadataAsync` separate from `CompleteAsync` — `CompleteAsync:328` deletes the lease,
so a metadata write must not depend on holding one afterwards.

**Endpoints** in `Endpoints/DistributedResolverEndpoints.cs`. Copy the shape of
`GetTrackAsync:71-79`: validate with `VideoIds.IsValid`, return a positional record, take
**no** `IQuotaService` (that handler is the existing precedent for a cheap read that skips
quota), and synthesize a `missing` status rather than 404.

- `GET /v1/tracks/{videoId}/metadata`
- `POST /v1/tracks/metadata:batch` — `{videoIds: [...]}`, cap **100** distinct valid ids

On a miss: return `missing` immediately (the client's YouTube leg covers it) and enqueue a
**metadata-only** background job so the next request is a DB hit. Dedupe by videoId and rate-
limit the enqueue — a 900-id batch miss must not stampede upstream.

**Capture points.**
- *Inline mode:* `YoutubeExplodeExtractorAdapter.cs:18` already calls `youtube.Videos.GetAsync`
  and reads only `Duration` — `Title`, `Author.ChannelTitle` and `Thumbnails` are fetched and
  thrown away. This is the cheapest capture point. Requires widening
  `IExtractorAdapter`/`IMediaExtractor` from `Task<byte[]>` to a record
  `ExtractedAudio(byte[] Audio, TrackMetadata? Metadata)`.
- *yt-dlp:* add `--print` lines per the `SourceFingerprintService.InspectAsync:33-46`
  precedent. **Caution:** `YtDlpDownloader.DownloadAsync:108` parses stdout with
  `.LastOrDefault()` — that must become a line split or it silently breaks.
- *Delegated mode (production):* new `POST /v1/worker/tracks/{videoId}/metadata` modelled on
  `VerifyBackupAsync:142-209`, called from `DownloaderWorker.ProcessAsync` **before**
  `UploadAsync` (line 198-199), because the lease is gone after completion.

**Tests.** Arg-list assertions in the `DownloaderDurationLimitTests` style; new columns in
`PostgresTrackRepositoryTests`; worker metadata report in `DelegatedIngestionTests` (its
Testcontainers fixture runs `MigrateAsync`, so the new migration is exercised automatically);
endpoint coverage in `ApiTests.cs`. Then `scripts\agent-check.ps1` per `AGENTS.md`.

---

## 2. Harmony-Cloud — WebSocket transport, split state

**Delete** `Playback/PlaybackHub.cs`, `AddSignalR()` (`Program.cs:60`) and
`MapHub` (`Program.cs:412`). Add `app.UseWebSockets()`.

**New** `Playback/PlaybackSocket.cs` mapping `GET /cloud/v1/playback/socket` **inside the
`cloud` group** so it inherits `RequireAuthorization()` — note the old `MapHub` was registered
on `app` and did not. Read `deviceId` from the query, resolve the account with
`AccountIdentity`, and register the socket in a singleton `IPlaybackConnectionRegistry`
(`ConcurrentDictionary<accountId, ConcurrentDictionary<deviceId, WebSocket>>`).
Set `IsRealtimeConnected`/`LastSeenAt` on connect and disconnect exactly as the hub did.

**Frame protocol** — JSON with a `type` discriminator:

- server→client: `sessionSnapshot`, `progress`, `command` (payload **inline** — removes today's
  notify-then-fetch round trip), `sessionEnded`
- client→server: `progress` (target only), `ack {commandId, applied}`, `ping`

**Session state v2** (durable, `jsonb`, written **only when it changes**, guarded by
`queueRevision`):

```json
{ "schemaVersion": 2, "queueIds": [], "index": 0, "currentSongId": "",
  "shuffle": false, "repeat": false, "queueLoop": false, "queueRevision": 7 }
```

900 ids is about 12 KB. Ids-only complies naturally with `PlaybackPayload.IsPortable`, whose
entire implementation is a 1 MiB cap plus a **substring** deny-list on key names containing
`url`/`path`/`token`/`credential` (`Domain/PlaybackPayload.cs:13-26`). Do not reintroduce
`thumbnailUrl` — it would 400 the whole payload.

**Ephemeral progress** (`positionMs`, `durationMs`, `publishedAtMs`, `playing`, `speed`) is
broadcast through the registry and persisted **at most every ~10 s**, plus on pause / seek /
song change, so a device joining late still gets a sane snapshot. This replaces the per-second
`Sequence++` + jsonb rewrite at `Program.cs:338-353`.

**Retained REST** (one-shot, not polling): `GET /playback/session` for snapshot-on-connect,
`GET /playback/devices`, session `start`/`target`/`end`, command submit. **Retained FCM**
wakeup for backgrounded Android (`Program.cs:189-190, 290-291, 332-333, 395-396`) — it is now
the signal to *open the socket*.

**Presence fix.** The projection at `Program.cs:146-150` reports `background` whenever a push
token exists. Windows has no FCM, so a closed Windows app must report `unavailable`, not
`background` — make the projection platform-aware.

**Tests.** This repo currently has **zero** endpoint tests (only 4 unit files; playback
coverage is a single 4-case `PlaybackPayloadTests` theory). Add a `WebApplicationFactory`
project covering session start/target/end, command ack and expiry, and the socket handshake
including the unauthenticated-upgrade rejection.

---

## 3. Harmony-Music — resolution, queues, progress, loading

**Fast metadata path.** Add `resolveSongMetadata(String videoId)` to `music_service.dart`: a
single `player` call, `MediaItem` built from `videoDetails`. Keep `getSongWithId` for deep
links (it genuinely wants the radio queue) but have it reuse the new method. Also give
`_sendRequest:110-132` a bounded retry with backoff.

**New** `lib/services/metadata/song_metadata_service.dart` — `resolve(id)` /
`resolveBatch(ids)`:

1. `HiveSongCacheRepository.getCachedSong:20-27` (`SongsCache`), then `SongDownloads` — 0 RTT
2. otherwise **race** the resolver batch endpoint against the YouTube `player` call, first
   non-null wins — reuse the existing race pattern at `audio_handler.dart:2487-2550`
3. write through to `SongsCache`; dedupe in-flight ids so the same id never resolves twice

**New** `lib/services/cloud/playback_socket_client.dart` on `web_socket_channel` — connect with
an `Authorization` header, exponential-backoff reconnect, expose snapshot/progress/command
streams. **Delete** the `Timer.periodic` poll at `cloud_playback_receiver.dart:37-43`.

**`playback_command_service.dart`** — `portablePlaybackState()` emits v2 (adds `durationMs`,
`publishedAtMs`, `queueRevision`); `startSharedSession`/`switchSharedTarget` send `queueIds`
only; drop `_portableSong:207-217` from session payloads.

**`cloud_playback_receiver.dart`**
- *Target:* `_applyHandoff` keeps today's fast start (resolve current → `updateQueue([current])`
  → play), then backfills the whole queue via `resolveBatch` in chunks and calls
  `updateQueue(full)`. Delete the hand-rolled `_sessionQueueIds`/`_advance:238-253` indexing —
  `next`/`previous` go back to the normal handler path.
- *Controller:* build placeholder `MediaItem`s from `queueIds` immediately so length and
  next/prev are correct, then backfill the visible window plus current.

**`player_controller.dart`**
- `applyRemoteSessionState`: set `total` from `durationMs`, populate the **full** `currentQueue`,
  set `currentSongIndex` from `index`.
- **Position extrapolator:** a `Ticker` (the controller already implements `TickerProvider`,
  `:40, 312-313`) anchored on `positionMs` + `publishedAtMs`, scaled by `speed`, clamped by
  `_clampProgressPosition:693-697`, running only while `buttonState == playing`. Start/stop in
  apply/clear; cancel in `dispose:1709-1734`.
- **Gate `_listenForChangesInDuration:630` and `_listenForPlaylistChange:760` on
  `_cloudRemoteStateActive`** — both are currently unguarded and will clobber mirrored state on
  the next local handler event.
- `clearRemoteSessionState:162` must resync `currentQueue`/`currentSong` from the local handler
  instead of only flipping a bool.

**Bugs this surfaces (must fix together)**
- `player_control.dart:78` and `mini_player.dart:190` do `currentSong.value!.artist!` →
  **null-check crash** on any item without artists. Make null-safe *and* give placeholders a
  non-null artist and non-empty title, so `isDisplayableSong:121` passes and the mini player
  isn't hidden.
- `up_next_queue.dart:154` renders a literal `null`; `:187` shows a blank duration; `:77,79`
  risk duplicate keys if an id is ever empty.
- Disable `Dismissible`/reorder on unresolved rows (`up_next_queue.dart:57, 83-85`) — these
  destructive gestures act on the mirrored list today.
- Next/prev re-enable on their own once the queue is complete
  (`mini_player.dart:323-340`, `player_control.dart:177-182`).

**Loading must always be visible** (explicit requirement). Reuse what exists:
- queue rows → `BasicShimmerContainer` (`lib/ui/widgets/shimmer_widgets/basic_container.dart`)
- now-playing while the target resolves → `PlayButtonState.loading`, which already renders a
  spinner at `animated_play_button.dart:97`
- devices sheet → per-row spinner until the target acks the handoff
- backfill progress → count label in `player_queue_footer_controls.dart:21,50` ("42 of 900")
- socket down → visible "reconnecting" state; with no poll fallback, a dead socket must never
  fail silently

**`pubspec.yaml`** — add `web_socket_channel: ^3.0.3`.

**Tests.** `test/player_controller_queue_order_test.dart` asserts *source-text statement order*
inside `pushSongToQueue`/`playPlayListSong` and **will break** — update it. Add real unit tests
for the extrapolator, `SongMetadataService` (cache hit / race / dedupe, via `http_mock_adapter`),
and v2 state serialization.

---

## Verification

**Resolver** — `dotnet test` (integration fixtures run the new migration), then
`scripts\agent-check.ps1`.
**Cloud** — `dotnet test` including the new endpoint + socket tests.
**Music** — via the `harmony-flutter-dart` MCP server with `timeout_ms: 600000`:
`flutter analyze --no-pub` and `flutter test`.

**Manual, two devices on one account** (the user runs the app; the agent never touches devices):
1. Handoff a cached song → audio starts on the target in well under a second.
2. Non-playing device shows a **smoothly advancing** bar with the correct total duration.
3. Both devices show the **full** queue; skeleton rows fill in progressively and the count
   label reports backfill progress.
4. Next/prev/seek/shuffle from the controller take effect on the target and reflect back.
5. Kill the network on the controller → visible "reconnecting", then automatic recovery.
6. Background the Android target → FCM wakes it and the socket reopens.

Post-deploy, the `harmony-resolver-diagnostics` MCP tools can confirm ingestion and track state.

## Risks

- **WS-only + no FCM on Windows** means a fully closed Windows app can never be a target. This
  is inherent to the choice; presence must report `unavailable` so the UI is honest.
- **Resolver metadata coverage starts at zero** and fills lazily, so the YouTube leg of the
  race carries nearly all traffic at first. The client fast path is what makes this acceptable.
- **`IsPortable` fails closed and silently** — any future payload key containing `url`/`path`
  yields a flat 400. Worth a comment at the serialization site.
- **Deploy order matters:** Resolver migration → Cloud → app.
