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

    // Stays raw SQL: needs a native atomic INSERT ... ON CONFLICT DO UPDATE ... WHERE upsert, which
    // ExecuteUpdateAsync can't express (it only updates rows that already match a Where predicate,
    // it can't insert-or-update in one statement). This is what keeps concurrent lease acquisition
    // race-free — see PostgresTrackRepositoryTests.Twenty_concurrent_callers_produce_exactly_one_lease_winner.
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

    public async Task EnqueueAsync(string videoId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        // Insert a fresh pending job, or revive a failed row whose retry backoff has elapsed. When the
        // row is already ready/ingesting (or failed but still within backoff) the WHERE makes the
        // DO UPDATE a no-op, leaving the existing row untouched.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO resolver_tracks
                (video_id, status, last_accessed_at, created_at, updated_at)
            VALUES ({videoId}, 'ingesting', {now}, {now}, {now})
            ON CONFLICT (video_id) DO UPDATE
            SET status = 'ingesting', failure_code = NULL, retry_after = NULL, updated_at = {now}
            WHERE resolver_tracks.status = 'failed'
              AND (resolver_tracks.retry_after IS NULL OR resolver_tracks.retry_after <= {now})
            """, cancellationToken);
    }

    // Raw ADO with a data-modifying CTE: the single statement selects one pending job under
    // FOR UPDATE ... SKIP LOCKED, upserts its lease, and RETURNs the video_id — so two workers can
    // never claim the same track. ExecuteSqlInterpolatedAsync only returns rows-affected, and EF's
    // SqlQuery would wrap this (illegally pushing the data-modifying CTE into a subquery), so we read
    // the RETURNING value directly.
    public async Task<IngestionLease?> ClaimJobAsync(Guid workerId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expiresAt = now + duration;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            opened = true;
        }
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH candidate AS (
                    SELECT t.video_id
                    FROM resolver_tracks t
                    LEFT JOIN resolver_ingestion_leases l ON l.video_id = t.video_id
                    WHERE t.status = 'ingesting' AND (l.video_id IS NULL OR l.expires_at <= @now)
                    ORDER BY t.created_at
                    LIMIT 1
                    FOR UPDATE OF t SKIP LOCKED
                ),
                claimed AS (
                    INSERT INTO resolver_ingestion_leases (video_id, owner_id, acquired_at, expires_at)
                    SELECT video_id, @owner, @now, @expires FROM candidate
                    ON CONFLICT (video_id) DO UPDATE
                        SET owner_id = EXCLUDED.owner_id, acquired_at = EXCLUDED.acquired_at, expires_at = EXCLUDED.expires_at
                        WHERE resolver_ingestion_leases.expires_at <= @now
                    RETURNING video_id
                )
                SELECT video_id FROM claimed
                """;
            AddParameter(command, "now", now);
            AddParameter(command, "owner", workerId);
            AddParameter(command, "expires", expiresAt);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is string videoId ? new IngestionLease(videoId, workerId, expiresAt) : null;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    public async Task<int> FailStuckJobsAsync(DateTimeOffset olderThan, DateTimeOffset retryAfter, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        // Only fail jobs no worker is actively leasing; a long download keeps its lease alive via
        // heartbeats and is spared even when older than the age bound.
        return await db.Tracks
            .Where(t => t.Status == "ingesting" && t.CreatedAt <= olderThan
                && !db.IngestionLeases.Any(l => l.VideoId == t.VideoId && l.ExpiresAt > now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, "failed")
                .SetProperty(x => x.FailureCode, "no_worker_available")
                .SetProperty(x => x.RetryAfter, retryAfter)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListPendingJobsAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Tracks.AsNoTracking()
            .Where(t => t.Status == "ingesting"
                && !db.IngestionLeases.Any(l => l.VideoId == t.VideoId && l.ExpiresAt > now))
            .OrderBy(t => t.CreatedAt)
            .Take(Math.Min(limit, 200))
            .Select(t => t.VideoId)
            .ToListAsync(cancellationToken);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    public async Task<bool> RenewLeaseAsync(
        IngestionLease lease, TimeSpan duration, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expiresAt = now + duration;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var updated = await db.IngestionLeases
            .Where(x => x.VideoId == lease.VideoId && x.OwnerId == lease.OwnerId && x.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, expiresAt), cancellationToken);
        return updated == 1;
    }

    public async Task AbandonLeaseAsync(IngestionLease lease, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.IngestionLeases.Where(x => x.VideoId == lease.VideoId && x.OwnerId == lease.OwnerId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.Tracks.Where(x => x.VideoId == lease.VideoId && x.Status == "ingesting"
                && !db.IngestionLeases.Any(l => l.VideoId == x.VideoId))
            .ExecuteDeleteAsync(cancellationToken);
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
        await db.Tracks.Where(x => x.VideoId == videoId && x.Status == "ready")
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastAccessedAt, now)
                .SetProperty(x => x.ExpiresAt, expiresAt)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
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
        var updated = await db.Tracks
            .Where(t => t.VideoId == lease.VideoId
                && db.IngestionLeases.Any(l => l.VideoId == t.VideoId && l.OwnerId == lease.OwnerId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.ObjectKey, objectKey)
                .SetProperty(x => x.ContentLength, contentLength)
                .SetProperty(x => x.ETag, etag)
                .SetProperty(x => x.FailureCode, failureCode)
                .SetProperty(x => x.RetryAfter, retryAfter)
                .SetProperty(x => x.ExpiresAt, expiresAt)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await db.IngestionLeases.Where(x => x.VideoId == lease.VideoId && x.OwnerId == lease.OwnerId)
            .ExecuteDeleteAsync(cancellationToken);
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
