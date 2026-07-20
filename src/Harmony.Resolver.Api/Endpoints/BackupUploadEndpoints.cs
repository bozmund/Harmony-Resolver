using System.Security.Cryptography;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Extraction;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using Harmony.Resolver.Api.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Resolver.Api.Endpoints;

public static class BackupUploadEndpoints
{
    public const string BackupPolicy = "tracks:backup";
    private const string TokenHeader = "X-Upload-Token";

    public static void MapBackupUploadEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool requireAuthorization)
    {
        var grant = endpoints.MapPost("/internal/v1/backup-grants", CreateGrantAsync);
        if (requireAuthorization) grant.RequireAuthorization(BackupPolicy);
        endpoints.MapPut("/v1/backup-uploads/{id:guid}", UploadAsync);
    }

    private static async Task<IResult> CreateGrantAsync(
        BackupGrantRequest request,
        ITrackRepository tracks,
        IDbContextFactory<ResolverDbContext> contexts,
        ResolverOptions options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!VideoIds.IsValid(request.VideoId))
            return Results.BadRequest(new { code = "invalid_video_id" });
        var track = await tracks.GetAsync(request.VideoId, cancellationToken);
        if (track?.Status == TrackStatus.Ready)
            return Results.Ok(new BackupGrantResponse("ready", request.VideoId, null, null, null));
        if (await tracks.GetReadyBytesAsync(cancellationToken)
            >= options.BackupStopGiB * 1024L * 1024L * 1024L)
            return Results.Json(new { code = "backup_capacity_reached" },
                statusCode: StatusCodes.Status507InsufficientStorage);

        var token = RandomNumberGenerator.GetBytes(32);
        var now = clock.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({request.VideoId}, 0))",
            cancellationToken);
        if (await db.Tracks.AnyAsync(
                x => x.VideoId == request.VideoId && x.Status == "ready", cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new BackupGrantResponse("ready", request.VideoId, null, null, null));
        }
        var existing = await db.BackupCandidates.SingleOrDefaultAsync(
            x => x.VideoId == request.VideoId, cancellationToken);
        if (existing is not null
            && existing.Status is "uploading" or "verifying"
            && existing.ExpiresAt > now)
        {
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new BackupGrantResponse(
                existing.Status, request.VideoId, existing.Id, null, null));
        }
        if (existing is not null) db.BackupCandidates.Remove(existing);
        var candidate = new BackupCandidateEntity
        {
            Id = Guid.NewGuid(),
            VideoId = request.VideoId,
            TokenHash = SHA256.HashData(token),
            Status = "pending",
            ExpiresAt = now + TimeSpan.FromMinutes(30),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.BackupCandidates.Add(candidate);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new BackupGrantResponse(
            "upload", request.VideoId, candidate.Id,
            $"/resolver/v1/backup-uploads/{candidate.Id}",
            Convert.ToBase64String(token)));
    }

    private static async Task<IResult> UploadAsync(
        Guid id,
        HttpContext context,
        IDbContextFactory<ResolverDbContext> contexts,
        ITrackRepository tracks,
        IObjectStore objects,
        IJobNotifier notifier,
        FfmpegNormalizer normalizer,
        AudioFingerprintService fingerprints,
        ResolverOptions options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!context.Request.Headers.TryGetValue(TokenHeader, out var supplied)
            || !TryDecode(supplied.ToString(), out var token))
            return Results.Unauthorized();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var candidate = await db.BackupCandidates.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (candidate is null || candidate.Status != "pending" || candidate.ExpiresAt <= clock.GetUtcNow()
            || !CryptographicOperations.FixedTimeEquals(candidate.TokenHash, SHA256.HashData(token)))
            return Results.Unauthorized();
        var claimed = await db.BackupCandidates
            .Where(x => x.Id == id && x.Status == "pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "uploading")
                .SetProperty(x => x.UpdatedAt, clock.GetUtcNow()), cancellationToken);
        if (claimed != 1) return Results.Conflict(new { code = "upload_already_started" });
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = null;

        var directory = Directory.CreateTempSubdirectory("harmony-backup-").FullName;
        try
        {
            var input = Path.Combine(directory, "upload");
            var maxBytes = options.MaxObjectMiB * 1024L * 1024L;
            await using (var file = File.Create(input))
            {
                var copied = await CopyBoundedAsync(context.Request.Body, file, maxBytes, cancellationToken);
                if (copied <= 0)
                {
                    candidate.Status = "rejected";
                    candidate.UpdatedAt = clock.GetUtcNow();
                    await db.SaveChangesAsync(cancellationToken);
                    return Results.Json(new { code = copied == 0 ? "empty_upload" : "object_too_large" },
                        statusCode: copied == 0 ? 400 : 413);
                }
            }
            var normalized = await normalizer.NormalizeAsync(input, directory, cancellationToken);
            var fingerprint = await fingerprints.ComputeAsync(input, directory, cancellationToken);
            var key = $"staging/backups/{candidate.Id}.ogg";
            var etag = '"' + Convert.ToHexString(SHA256.HashData(normalized)).ToLowerInvariant() + '"';
            await using (var stream = new MemoryStream(normalized, writable: false))
                await objects.PutAsync(key, stream, normalized.LongLength, cancellationToken);

            candidate.Status = "verifying";
            candidate.StagingObjectKey = key;
            candidate.ContentLength = normalized.LongLength;
            candidate.ETag = etag;
            candidate.DurationSeconds = fingerprint.DurationSeconds;
            candidate.FingerprintA = fingerprint.SegmentA;
            candidate.FingerprintB = fingerprint.SegmentB;
            candidate.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            await tracks.EnqueueAsync(candidate.VideoId, IngestionPriority.Backup, cancellationToken);
            await notifier.NotifyAsync(candidate.VideoId, cancellationToken);
            return Results.Accepted(value: new { status = "verification_pending", candidate.VideoId });
        }
        catch
        {
            await db.BackupCandidates
                .Where(x => x.Id == id && x.Status == "uploading")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "pending")
                    .SetProperty(x => x.UpdatedAt, clock.GetUtcNow()), CancellationToken.None);
            throw;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<long> CopyBoundedAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes) return -1;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return total;
    }

    private static bool TryDecode(string value, out byte[] token)
    {
        try { token = Convert.FromBase64String(value); return token.Length == 32; }
        catch (FormatException) { token = []; return false; }
    }
}

public sealed record BackupGrantRequest(string VideoId);
public sealed record BackupGrantResponse(
    string Status, string VideoId, Guid? UploadId, string? UploadPath, string? UploadToken);
