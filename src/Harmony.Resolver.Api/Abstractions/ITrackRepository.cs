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
    Task EnqueueAsync(string videoId, CancellationToken cancellationToken);
    /// <summary>
    /// Atomically claims the oldest pending job (using <c>FOR UPDATE ... SKIP LOCKED</c> so concurrent
    /// workers never claim the same track) and leases it to <paramref name="workerId"/>. Returns the
    /// lease, or <see langword="null"/> when the queue is empty.
    /// </summary>
    Task<IngestionLease?> ClaimJobAsync(Guid workerId, TimeSpan duration, CancellationToken cancellationToken);
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
    Task<bool> MarkReadyAsync(IngestionLease lease, string objectKey, long contentLength, string etag, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<bool> MarkFailedAsync(IngestionLease lease, string failureCode, DateTimeOffset retryAfter, CancellationToken cancellationToken);
    Task TouchAsync(string videoId, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredTrack>> ListExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
    Task<bool> DeleteExpiredAsync(string videoId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredTrack>> ListFailuresAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken);
    Task<RepositoryStatistics> GetStatisticsAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
