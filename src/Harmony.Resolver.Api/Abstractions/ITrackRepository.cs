using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Abstractions;

public interface ITrackRepository
{
    Task<StoredTrack?> GetAsync(string videoId, CancellationToken cancellationToken);
    Task<IngestionLease?> TryAcquireLeaseAsync(string videoId, Guid ownerId, TimeSpan duration, CancellationToken cancellationToken);
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
