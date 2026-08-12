using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Abstractions;

public interface ITrackRepository
{
    Task<StoredTrack?> GetAsync(string videoId, CancellationToken cancellationToken);
    Task<IngestionLease?> TryAcquireLeaseAsync(string videoId, Guid ownerId, TimeSpan duration, CancellationToken cancellationToken);
    /// <summary>
    /// Records a cache-miss as a pending ingestion job (a track row in <c>ingesting</c> status with no
    /// lease) for the downloader fleet to claim. Idempotent: a no-op when the track is already
    /// <c>ready</c> or <c>ingesting</c>, and resets a <c>failed</c> row back to <c>ingesting</c> only
    /// once its <c>retry_after</c> backoff has elapsed.
    /// </summary>
    Task EnqueueAsync(string videoId, IngestionPriority priority, CancellationToken cancellationToken);
    /// <summary>
    /// Atomically claims the oldest pending job (using <c>FOR UPDATE ... SKIP LOCKED</c> so concurrent
    /// workers never claim the same track) and leases it to <paramref name="workerId"/>. Returns the
    /// lease, or <see langword="null"/> when the queue is empty.
    /// </summary>
    Task<IngestionLease?> ClaimJobAsync(Guid workerId, TimeSpan duration, CancellationToken cancellationToken);
    Task EnqueueMetadataBackfillAsync(string videoId, CancellationToken cancellationToken);
    Task<bool> RenewMetadataBackfillLeaseAsync(IngestionLease lease, TimeSpan duration, CancellationToken cancellationToken);
    Task<bool> CompleteMetadataBackfillAsync(IngestionLease lease, CancellationToken cancellationToken);
    Task<bool> FailMetadataBackfillAsync(IngestionLease lease, DateTimeOffset retryAfter, CancellationToken cancellationToken);
    Task<bool> HasActiveWorkerLeaseAsync(IngestionLease lease, CancellationToken cancellationToken);
    /// <summary>
    /// Fails jobs stuck <c>ingesting</c> since before <paramref name="olderThan"/> that no worker is
    /// actively leasing, so listeners polling an unfillable job eventually get a definitive error.
    /// Returns the number of jobs failed.
    /// </summary>
    Task<int> FailStuckJobsAsync(DateTimeOffset olderThan, DateTimeOffset retryAfter, CancellationToken cancellationToken);
    /// <summary>
    /// Returns the video ids of jobs still pending — <c>ingesting</c> with no live lease — oldest first.
    /// Used by the republisher to re-notify the downloader fleet about work no one is currently doing.
    /// </summary>
    Task<IReadOnlyList<string>> ListPendingJobsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
    Task<bool> RenewLeaseAsync(IngestionLease lease, TimeSpan duration, CancellationToken cancellationToken);
    Task AbandonLeaseAsync(IngestionLease lease, CancellationToken cancellationToken);
    Task<bool> MarkReadyAsync(IngestionLease lease, string objectKey, long contentLength, string etag, CancellationToken cancellationToken);
    Task<bool> MarkFailedAsync(IngestionLease lease, string failureCode, DateTimeOffset retryAfter, CancellationToken cancellationToken);
    Task TouchAsync(string videoId, CancellationToken cancellationToken);
    // Kept as inert compatibility hooks while permanent-media callers roll forward.
    Task<IReadOnlyList<StoredTrack>> ListExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
    Task<bool> DeleteExpiredAsync(string videoId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredTrack>> ListFailuresAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken);
    /// <summary>Returns a bounded, safe-to-display page of failed media jobs for the SSH-only admin console.</summary>
    Task<AdminFailedTrackPage> ListFailedForAdminAsync(int offset, int limit, CancellationToken cancellationToken);
    /// <summary>
    /// Bypasses a failed job's retry cooldown and atomically queues a fresh urgent download.
    /// Existing ready work and actively leased work are never replaced.
    /// </summary>
    Task<AdminRetryResult> ForceRetryAsync(string videoId, CancellationToken cancellationToken);
    Task<RepositoryStatistics> GetStatisticsAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<long> GetReadyBytesAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Returns display metadata for <paramref name="videoId"/>, or <see langword="null"/> when the
    /// track is unknown or its metadata was never captured.
    /// </summary>
    Task<TrackMetadata?> GetMetadataAsync(string videoId, CancellationToken cancellationToken);
    /// <summary>
    /// Batch form of <see cref="GetMetadataAsync"/> in a single round trip. Ids with no metadata are
    /// simply absent from the result — callers must not assume index alignment with the input.
    /// </summary>
    Task<IReadOnlyList<TrackMetadata>> GetMetadataBatchAsync(
        IReadOnlyCollection<string> videoIds, CancellationToken cancellationToken);
    /// <summary>
    /// Upserts display metadata, creating a placeholder track row if the video has never been seen.
    /// Deliberately does NOT require holding an ingestion lease: the lease is deleted when ingestion
    /// completes, and metadata is also written by the lazy-fill path where no lease ever exists.
    /// </summary>
    Task SetMetadataAsync(TrackMetadata metadata, CancellationToken cancellationToken);
}
