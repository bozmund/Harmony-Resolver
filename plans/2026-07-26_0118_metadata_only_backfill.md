# Metadata-only backfill for cached Resolver tracks

Accepted 2026-07-26.

## Outcome

Every existing and future ready Resolver track can receive display metadata without downloading,
normalizing, uploading, replacing, or evicting its cached audio object. Work runs only on the
residential downloader fleet, never from the production VPS.

## Decisions

- Keep display metadata in the existing `resolver_track_metadata` table.
- Add a durable, separately leased `resolver_metadata_backfill_jobs` queue. It is deliberately not
  represented by `resolver_tracks.status`, so a ready audio object remains ready throughout
  metadata work.
- The migration seeds one pending metadata job per ready track with no populated metadata. It is
  idempotent and does not touch media objects.
- Metadata jobs share the existing RabbitMQ doorbell, but normal audio jobs always win claim
  priority. A metadata job is claimed only when no audio/backup job is available.
- A downloader performs a yt-dlp metadata probe using `--skip-download` and the existing
  self-identifying metadata print line. It sends metadata, then explicitly completes the metadata
  job. No file path or audio stream is produced.
- Metadata failures retry with a bounded server-side delay; they never change a track's audio
  status or failure code.
- On a public metadata miss, Delegated mode persists a metadata job and rings the downloader.
  Inline mode retains its existing in-process, rate-limited YouTube metadata filler.

## Implementation

1. Add metadata-job and metadata-lease entities, EF mappings, a hand-written migration, and a
   migration seed for ready tracks that lack metadata.
2. Extend the track repository with durable metadata-job enqueue, claim, heartbeat, completion,
   failure, and pending-list operations. Integrate the metadata queue with the existing claim and
   republisher paths without allowing it to starve audio ingestion.
3. Update public metadata reads to enqueue/notify durable jobs in Delegated mode; retain the
   in-memory inline-only filler elsewhere.
4. Add worker endpoints for metadata-job heartbeat, completion, and failure. Validate that a
   metadata report has a live lease from either an audio or metadata job.
5. Add a downloader metadata probe and metadata-job processing branch; preserve the existing
   audio download flow unchanged.
6. Cover migration seeding, durable job lifecycle, endpoint behavior, downloader argument lists,
   and delegated worker processing with automated tests. Run `scripts/agent-check.ps1`.

## Rollout

Deploy the Resolver migration/API and then rebuild/restart residential downloader agents. The
migration queues existing ready tracks; agents drain jobs at idle priority and populate
`resolver_track_metadata` progressively.
