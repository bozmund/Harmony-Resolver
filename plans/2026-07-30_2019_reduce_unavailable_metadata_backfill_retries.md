# Reduce retries for unavailable metadata backfills

## Summary

Treat explicit yt-dlp “Video unavailable” metadata failures as a distinct, expected condition: retry them after seven days and log them at Information level. Keep the existing 15-minute retry and Warning logging for all other metadata failures.

## Implementation Changes

- In the downloader, classify unambiguous yt-dlp metadata stderr containing “Video unavailable” as `metadata_yt_dlp_unavailable`; retain `metadata_yt_dlp_failed` for all other failures.
- Log that specific metadata-backfill outcome at Information level without upstream error detail; preserve the current structured Warning for transient or unknown failures.
- Reuse the existing `code` field on `POST /v1/worker/metadata-jobs/{videoId}/fail`: apply a seven-day retry for `metadata_yt_dlp_unavailable`, otherwise retain the 15-minute retry.
- No schema or public request-shape change is needed.

## Test Plan

- Unit-test case-insensitive unavailable-message classification and fallback generic classification.
- Add endpoint/repository coverage confirming unavailable metadata jobs persist a seven-day `retry_after`, while generic failures remain 15 minutes.
- Run `dotnet test Harmony.Resolver.slnx` and `scripts/agent-check.ps1`.

## Assumptions

- Only the explicit “Video unavailable” response gets the extended retry; private, region-blocked, bot-check, timeout, and malformed-response failures retain normal retry behavior.
- The desired operational behavior is one audit-level Information entry per unavailable attempt.
