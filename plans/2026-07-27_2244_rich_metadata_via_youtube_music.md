# Rich metadata in the Resolver: fetch it the way the app does

## Context

The app has two metadata pipelines and they produce very different things.

**The rich one** — `getWatchPlaylist` (YT Music InnerTube `next`) → `parseWatchTrack` →
`parseSongRuns` ([nav_parser.dart:251](lib/services/nav_parser.dart:251)). Walks
`longBylineText.runs` and pulls out `album: {name, id}` (browseId starting `MPRE`), `artists:
[{name, id}]`, plus the square YT Music cover from `thumbnail.thumbnails`. This is what runs when
you browse an album, and what "Go to album" needs — `MediaItemBuilder.fromJson` keeps an album
**only if it has an `id`**, and the menu entry renders only when `extras['album']['id']` exists.

**The fast one** — `SongMetadataService`, added so cloud sessions (which carry bare ids) do not pay
2–4 round trips per song. Its two online legs are the Resolver and YouTube's `player` endpoint, and
*neither* can produce an album id:

- Resolver: metadata comes from a yt-dlp `--print` line on the fleet
  ([YtDlpMetadataLine.cs](C:/MyRepositories/Harmony-Resolver/src/Harmony.Resolver.Api/Infrastructure/Extraction/YtDlpMetadataLine.cs))
  or `YoutubeExplodeMetadataSource` inline. Both give a video-shaped view: channel as artist, the
  letterboxed video thumbnail, an album *name* at best.
- `resolveSongMetadata` reads `player.videoDetails` — no album at all.

Results are written to `SongsCache`, and `_fromCaches` is consulted first on every later lookup, so
once a thin answer is cached it wins forever. That is why artwork and album links got worse and
stayed worse.

This makes the Resolver fetch metadata the way the app's rich path does, so the backend holds
album ids, artist ids and real cover art for every track.

## A bug this also fixes

`TrackMetadataResponse.Album` is a `string`, so the app receives `"album": "Some Album"` and
`MediaItemBuilder.fromJson` evaluates `json['album']['id']` on a String. That throws, and
`_resolverBatch`'s per-track `catch` silently drops the song. **Every Resolver track that has an
album name is currently being discarded by the app**, falling through to the YouTube leg that has
no album either.

## Decisions

- The InnerTube call runs **on the downloader fleet**, extending the `metadata` job kind it already
  handles. Upstream access stays on residential IPs, which is the reason Delegated mode exists —
  the API makes no YouTube calls in production today.
- **Backfill re-fetches rows whose stored metadata has no album id**, so the existing catalogue
  converges.
- App-side, **thin `SongsCache` entries are purged once** so they re-resolve against the now-rich
  Resolver.

## Storage already supports this

`SetMetadataAsync` persists `metadata.ToJson()` straight into `resolver_tracks.metadata` (jsonb),
and `TrackMetadata.Json` is a passthrough that `ToJson()` returns verbatim
([TrackMetadata.cs:21](C:/MyRepositories/Harmony-Resolver/src/Harmony.Resolver.Api/Domain/TrackMetadata.cs)).
So the rich document needs no schema change — only the *source* and the *serving contract* flatten
it today.

## Harmony-Resolver — downloader

New `YouTubeMusicMetadataClient` in `src/Harmony.Resolver.Downloader/`, a direct port of the app's
rich path:

- POST `https://music.youtube.com/youtubei/v1/next` with the `WEB_REMIX` context, client version and
  API key the app already uses (`_context` in [music_service.dart:36](lib/services/music_service.dart:36),
  `fixedParms` in [constant.dart](lib/services/constant.dart)), plus `videoId`,
  `playlistId: "RDAMVM<videoId>"`, `enablePersistentPlaylistPanel: true`, `isAudioOnly: true` and
  the `watchEndpointMusicSupportedConfigs` block.
- Navigate `contents.singleColumnMusicWatchNextResultsRenderer.tabbedRenderer.
  watchNextTabbedResultsRenderer` → tab content → `musicQueueRenderer.content.playlistPanelRenderer.
  contents`, and take the `playlistPanelVideoRenderer` whose `videoId` matches the request.
- Port `parseWatchTrack` + `parseSongRuns`: even-indexed runs only (odd ones are separators); a run
  with a `navigationEndpoint` whose browseId starts with `MPRE` or contains `release_detail` is the
  **album**, any other endpoint run is an **artist**, and bare text is matched against the
  duration / year / views patterns before falling back to an id-less artist. Emit the same
  Harmony-shaped song JSON `MediaItemBuilder.fromJson` consumes: `videoId`, `title`, `artists`,
  `album`, `thumbnails`, `length`, `duration`, `year`.
- **Fail soft.** A non-music video has no playlist panel, and YouTube changes these shapes without
  warning. Any miss or parse failure falls back to the existing yt-dlp metadata rather than failing
  the job — coverage must never regress to nothing.

`DownloaderWorker` ([DownloaderWorker.cs:198](C:/MyRepositories/Harmony-Resolver/src/Harmony.Resolver.Downloader/DownloaderWorker.cs))
calls the new client for `job.Kind == "metadata"`, and for the metadata reported alongside an audio
download, keeping `YtDlpDownloader.FetchMetadataAsync` as the fallback.

## Harmony-Resolver — API

- `WorkerMetadataRequest` gains the Harmony song JSON alongside the existing flat fields.
  `ReportMetadataAsync` ([WorkerIngestionEndpoints.cs:43](C:/MyRepositories/Harmony-Resolver/src/Harmony.Resolver.Api/Endpoints/WorkerIngestionEndpoints.cs))
  passes it as `TrackMetadata.Json`, which flows unchanged into the jsonb column. Keep populating
  the flat fields — `IsEmpty` gates on `Title`, and diagnostics read them.
- `PostgresTrackRepository.FromJson` must carry the raw document into `TrackMetadata.Json` on read,
  or rich metadata is lost the moment it is loaded back.
- **Serving**: `TrackMetadataResponse.Ready` currently flattens to `Title/Artists/Album/…`. Return
  the stored Harmony document's fields instead, alongside `videoId` and `status`. The app already
  does `MediaItemBuilder.fromJson({...track, 'videoId': ...})`, so it needs no change to start
  receiving `album: {name, id}` and square artwork — and the `album`-as-String crash disappears.
- `YoutubeExplodeMetadataSource` stays as the inline-mode source; only Delegated mode gets the rich
  path. Note this in its doc comment so the difference is not mistaken for a bug.

## Harmony-Resolver — backfill

A migration seeding `resolver_metadata_backfill_jobs` for every track whose `metadata` document has
no `album.id`, following the seeding pattern already used by
`20260726011800_MetadataBackfillJobs`. Jobs are claimed only when no audio job is available, so this
never competes with ingestion.

## Harmony-Music — stop the cache pinning thin data

- Purge `SongsCache` entries with no `album.id`, next to the download purge already in
  `startHouseKeeping` ([house_keeping.dart](lib/utils/house_keeping.dart)), so they re-resolve.
  Reuse `thumbnailUrlFromJson`-style tolerant reading rather than indexing blindly.
- `_resolverBatch`'s per-track `catch` swallowed the album crash for weeks. Log the failure under
  `LogTags.cloudSync` (or a metadata tag) so the next shape mismatch is visible.

## Verification

**Resolver:**

```bash
dotnet test Harmony.Resolver.slnx
```

Add unit tests for the parser port against a captured `next` response fixture: album id extracted
from an `MPRE` run, artists keeping their ids, the leading result-type token not becoming an artist,
a non-music response falling back rather than throwing. Then `scripts/agent-check.ps1` per the
repo's AGENTS.md.

**App:**

```bash
flutter analyze --no-pub
```

then `flutter test`. Add a case asserting `MediaItemBuilder.fromJson` keeps `extras['album']['id']`
from a Resolver-shaped response, which is what "Go to album" gates on.

**End to end (user-run):** deploy the Resolver and the fleet, let the backfill run, then query
`resolver_tracks.metadata` for a known album track and confirm `album.id` starts with `MPRE` and the
thumbnail is an `lh3.googleusercontent.com` URL rather than `i.ytimg.com`. In the app, play a song
from an album and confirm the cover is square and "Go to album" appears and navigates.

## Rollout order

1. Resolver API first — it accepts the extended worker request while older workers keep sending the
   flat one.
2. Downloader fleet, which starts producing rich metadata.
3. Run the backfill migration.
4. App build with the cache purge, so devices stop preferring their stored thin copies.
