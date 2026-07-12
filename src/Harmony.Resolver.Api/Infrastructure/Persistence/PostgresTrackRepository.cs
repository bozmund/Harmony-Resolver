using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Resolver.Api.Infrastructure.Persistence;

public sealed class PostgresTrackRepository(
    IDbContextFactory<ResolverDbContext> contexts,
    TimeProvider clock) : ITrackRepository
{
    public async Task<StoredTrack?> GetAsync(string videoId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entity = await db.Tracks.AsNoTracking().SingleOrDefaultAsync(x => x.VideoId == videoId, cancellationToken);
        return entity is null
            ? null
            : new StoredTrack(entity.VideoId, ParseStatus(entity.Status), entity.ObjectKey, entity.ContentLength,
                entity.ETag, entity.FailureCode, entity.RetryAfter, entity.ExpiresAt);
    }

    public async Task<IngestionLease?> TryAcquireLeaseAsync(
        string videoId, Guid ownerId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expiresAt = now + duration;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO resolver_tracks
                (video_id, status, last_accessed_at, created_at, updated_at)
            VALUES ({videoId}, 'ingesting', {now}, {now}, {now})
            ON CONFLICT (video_id) DO NOTHING
            """, cancellationToken);

        var acquired = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO resolver_ingestion_leases
                (video_id, owner_id, acquired_at, expires_at)
            VALUES ({videoId}, {ownerId}, {now}, {expiresAt})
            ON CONFLICT (video_id) DO UPDATE
            SET owner_id = EXCLUDED.owner_id,
                acquired_at = EXCLUDED.acquired_at,
                expires_at = EXCLUDED.expires_at
            WHERE resolver_ingestion_leases.expires_at <= {now}
            """, cancellationToken);

        if (acquired == 1)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE resolver_tracks
                SET status = 'ingesting', failure_code = NULL, retry_after = NULL, updated_at = {now}
                WHERE video_id = {videoId}
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IngestionLease(videoId, ownerId, expiresAt);
        }

        await transaction.RollbackAsync(cancellationToken);
        return null;
    }

    public async Task<bool> RenewLeaseAsync(
        IngestionLease lease, TimeSpan duration, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expiresAt = now + duration;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE resolver_ingestion_leases
            SET expires_at = {expiresAt}
            WHERE video_id = {lease.VideoId} AND owner_id = {lease.OwnerId} AND expires_at > {now}
            """, cancellationToken);
        return updated == 1;
    }

    public async Task AbandonLeaseAsync(IngestionLease lease, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM resolver_ingestion_leases
            WHERE video_id = {lease.VideoId} AND owner_id = {lease.OwnerId}
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM resolver_tracks AS tracks
            WHERE tracks.video_id = {lease.VideoId} AND tracks.status = 'ingesting'
              AND NOT EXISTS (SELECT 1 FROM resolver_ingestion_leases AS leases WHERE leases.video_id = tracks.video_id)
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<bool> MarkReadyAsync(
        IngestionLease lease, string objectKey, long contentLength, string etag,
        DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        CompleteAsync(lease, "ready", objectKey, contentLength, etag, null, null, expiresAt, cancellationToken);

    public Task<bool> MarkFailedAsync(
        IngestionLease lease, string failureCode, DateTimeOffset retryAfter, CancellationToken cancellationToken) =>
        CompleteAsync(lease, "failed", null, null, null, failureCode, retryAfter, null, cancellationToken);

    public async Task TouchAsync(string videoId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE resolver_tracks
            SET last_accessed_at = {now}, expires_at = {expiresAt}, updated_at = {now}
            WHERE video_id = {videoId} AND status = 'ready'
            """, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredTrack>> ListExpiredAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entities = await db.Tracks.AsNoTracking()
            .Where(x => x.Status == "ready" && x.ExpiresAt <= now && x.ObjectKey != null)
            .OrderBy(x => x.ExpiresAt).Take(limit).ToListAsync(cancellationToken);
        return entities.Select(ToStoredTrack).ToArray();
    }

    public async Task<bool> DeleteExpiredAsync(
        string videoId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Tracks.Where(x => x.VideoId == videoId && x.Status == "ready" && x.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<StoredTrack>> ListFailuresAsync(
        DateTimeOffset since, int limit, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entities = await db.Tracks.AsNoTracking().Where(x => x.Status == "failed" && x.UpdatedAt >= since)
            .OrderByDescending(x => x.UpdatedAt).Take(Math.Min(limit, 200)).ToListAsync(cancellationToken);
        return entities.Select(ToStoredTrack).ToArray();
    }

    public async Task<RepositoryStatistics> GetStatisticsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var counts = await db.Tracks.AsNoTracking().GroupBy(x => x.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var activeLeases = await db.IngestionLeases.AsNoTracking().CountAsync(x => x.ExpiresAt > now, cancellationToken);
        return new RepositoryStatistics(counts, activeLeases);
    }

    private async Task<bool> CompleteAsync(
        IngestionLease lease, string status, string? objectKey, long? contentLength, string? etag,
        string? failureCode, DateTimeOffset? retryAfter, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE resolver_tracks AS tracks
            SET status = {status}, object_key = {objectKey}, content_length = {contentLength}, etag = {etag},
                failure_code = {failureCode}, retry_after = {retryAfter}, expires_at = {expiresAt}, updated_at = {now}
            FROM resolver_ingestion_leases AS leases
            WHERE tracks.video_id = {lease.VideoId}
              AND leases.video_id = tracks.video_id
              AND leases.owner_id = {lease.OwnerId}
            """, cancellationToken);
        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM resolver_ingestion_leases
            WHERE video_id = {lease.VideoId} AND owner_id = {lease.OwnerId}
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static TrackStatus ParseStatus(string status) => status switch
    {
        "ingesting" => TrackStatus.Ingesting,
        "ready" => TrackStatus.Ready,
        "failed" => TrackStatus.Failed,
        _ => throw new InvalidDataException($"Unknown persisted track status '{status}'.")
    };

    private static StoredTrack ToStoredTrack(Entities.TrackEntity entity) =>
        new(entity.VideoId, ParseStatus(entity.Status), entity.ObjectKey, entity.ContentLength,
            entity.ETag, entity.FailureCode, entity.RetryAfter, entity.ExpiresAt);
}
