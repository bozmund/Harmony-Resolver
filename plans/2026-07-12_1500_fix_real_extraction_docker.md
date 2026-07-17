# Fix real YouTube extraction in Harmony Resolver Docker stack

## Context

Harmony Music's resolver playback only ever produced a 500 ms "boop" because the dev
resolver runs `DeterministicExtractor` (a stub returning a hardcoded Ogg Opus tone) —
intentional per the accepted 2026-07-12 LAN plan. The user chose to enable real
extraction. We already flipped the switch: a git-ignored
`compose.real-extractor.local.yml` sets `Resolver__UseFakeExtractor=false` for
`api-1`/`api-2`, and the containers were rebuilt. Real extraction now *runs* but
**fails**:

- **yt-dlp adapter** (`YtDlpExtractorAdapter.cs`): yt-dlp 2026.07.04 warns
  `No supported JavaScript runtime could be found. Only deno is enabled by default`
  then errors `Video unavailable`. YouTube requires solving JS challenges (EJS);
  yt-dlp needs **deno**, which the image (`Harmony-Resolver/Dockerfile`) does not
  install.
- **YoutubeExplode adapter** (fallback): fails `youtube_explode_failed`; pinned at
  **6.5.7** in `Directory.Packages.props` — stale against July-2026 YouTube.
- **Observability gap**: `OrderedMediaExtractor` logs only the failure code, never
  the sanitized inner error (yt-dlp stderr is captured into `ExtractionException`
  but dropped) — diagnosing required manual `docker exec`.

Already done this session (uncommitted, in Harmony-Resolver working tree, per the
user's test-before-commit rule):
- `OrderedMediaExtractor.cs`: added catch-all so non-`ExtractionException` failures
  (e.g. missing binary → `Win32Exception`) fall through to the next adapter.
- `.gitignore`: added `compose.*.local.yml`.
- `compose.real-extractor.local.yml`: the opt-in override (dev default stays fake,
  matching the accepted plan's "switch when desired").

## Changes (all in C:\MyRepositories\Harmony-Resolver)

### 1. Dockerfile — install deno for yt-dlp
In the runtime stage (line 6 area), extend the existing `apt-get` RUN to also
install deno (latest at build time, same policy as yt-dlp):

- Add `curl`, `ca-certificates`, `unzip` to the apt install list.
- Install deno via the official installer into a system path, e.g.:
  `curl -fsSL https://deno.land/install.sh | DENO_INSTALL=/usr/local sh`
  (installs `/usr/local/bin/deno`; must be on PATH for the `app` user, which
  `/usr/local/bin` is).
- Keep the single-RUN layer style already used.

### 2. Directory.Packages.props — bump YoutubeExplode
Update `YoutubeExplode` from `6.5.7` to the latest available version (check
nuget.org during implementation). One line; no code changes expected since the
adapter uses stable APIs (`Videos.GetAsync`, `GetManifestAsync`,
`GetAudioOnlyStreams`, `DownloadAsync`).

### 3. OrderedMediaExtractor.cs — log the sanitized inner error
In the existing `catch (ExtractionException)` block, include
`exception.InnerException?.Message` (already sanitized to ≤500 chars by the
adapters) in the LogWarning so future extraction failures are diagnosable from
`docker logs` without exec'ing into containers.

### 4. Rebuild and verify (no compose.yaml changes)
Dev default remains `UseFakeExtractor=true`; the local override keeps real
extraction opt-in.

## Verification

1. `docker compose -f compose.yaml -f compose.real-extractor.local.yml up -d --build api-1 api-2`
2. In-container sanity: `docker exec harmony-resolver-api-1-1 deno --version` and a
   direct yt-dlp extraction of a short video (quote `%(ext)s` patterns; avoid Git
   Bash path mangling by using container-side paths only).
3. End-to-end through nginx:
   `curl -D - -o test.ogg http://127.0.0.1:8088/v1/tracks/jNQXAC9IZiE/audio`
   Expect `200`, `Content-Type: audio/ogg`, file starts with `OggS`, size ≫ 5 KB
   (the boop was ~5 KB; real audio will be hundreds of KB+). First request may
   return `202 + Retry-After` (ingestion); poll until ready.
4. Repeat request → expect cached `200` with ETag (served from MinIO).
5. If yt-dlp still says "Video unavailable" with deno present, try another video ID
   to distinguish per-video restriction from systemic failure, and check whether
   the *fallback* (bumped YoutubeExplode) now succeeds — logs now show inner errors.
6. User then plays a track in the Harmony Music Windows/Android debug app pointed at
   `http://<LAN-IP>:8088` and confirms real audio instead of the boop.
7. Run resolver unit/integration tests (`dotnet test`) to confirm the
   OrderedMediaExtractor changes don't break existing suites (integration tests
   force `UseFakeExtractor=true`, so they stay hermetic).

## Out of scope / notes

- No commits: all Harmony-Resolver changes stay uncommitted until the user tests
  end-to-end (their standing rule); they commit together with their LAN work.
- `compose.yaml` dev default stays fake — accepted-plan behavior unchanged.
- If YouTube bot-detection still blocks the home IP even with deno (possible but
  unlikely on residential), cookie/PO-token setup would be a separate follow-up.
- Harmony Music (Flutter) side needs no changes; it already streams whatever the
  resolver returns.
