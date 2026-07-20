using System.Security.Cryptography;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Extraction;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;

namespace Harmony.Resolver.Api.Endpoints;

/// <summary>
/// Endpoints the credentialed downloader fleet uses to pull ingestion jobs and upload the audio it
/// fetched from a residential IP. All routes require the <see cref="IngestPolicy"/> Auth0 scope
/// (except in Development, where they may run open for local end-to-end testing). Listener endpoints
/// stay anonymous — see <see cref="DistributedResolverEndpoints"/>.
/// </summary>
public static class WorkerIngestionEndpoints
{
    public const string IngestPolicy = "tracks:ingest";
    private const string LeaseHeader = "X-Lease-Token";

    public static void MapWorkerIngestionEndpoints(this IEndpointRouteBuilder endpoints, bool requireAuthorization)
    {
        var group = endpoints.MapGroup("/v1/worker");
        if (requireAuthorization) group.RequireAuthorization(IngestPolicy);

        group.MapPost("/jobs/claim", ClaimAsync);
        group.MapPost("/jobs/{videoId}/heartbeat", HeartbeatAsync);
        group.MapPut("/tracks/{videoId}/audio", UploadAudioAsync);
        group.MapPost("/tracks/{videoId}/verify-backup", VerifyBackupAsync);
        group.MapPost("/tracks/{videoId}/fail", FailAsync);
    }

    private static async Task<IResult> ClaimAsync(ITrackRepository tracks, ResolverOptions options, CancellationToken cancellationToken)
    {
        var lease = await tracks.ClaimJobAsync(Guid.NewGuid(), options.LeaseDuration, cancellationToken);
        return lease is null
            ? Results.NoContent()
            : Results.Ok(new WorkerJob(lease.VideoId, lease.OwnerId, lease.ExpiresAt, lease.Kind));
    }

    private static async Task<IResult> HeartbeatAsync(
        string videoId, HttpContext context, ITrackRepository tracks, ResolverOptions options,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!VideoIds.IsValid(videoId)) return InvalidVideoId();
        if (!TryLease(context, videoId, out var lease)) return MissingLease();
        var renewed = await tracks.RenewLeaseAsync(lease, options.LeaseDuration, cancellationToken);
        return renewed
            ? Results.Ok(new WorkerJob(
                videoId, lease.OwnerId, clock.GetUtcNow() + options.LeaseDuration, lease.Kind))
            : LeaseLost();
    }

    private static async Task<IResult> UploadAudioAsync(
        string videoId, HttpContext context, ITrackRepository tracks, IObjectStore objects,
        IDbContextFactory<ResolverDbContext> contexts,
        FfmpegNormalizer normalizer, ResolverOptions options, TimeProvider clock,
        ILogger<Program> logger, CancellationToken cancellationToken)
    {
        if (!VideoIds.IsValid(videoId)) return InvalidVideoId();
        if (!TryLease(context, videoId, out var lease)) return MissingLease();

        // Our bounded copy enforces MaxObjectMiB below, so lift Kestrel's default body-size cap
        // (which is smaller than MaxObjectMiB) to avoid a premature 413 from the server.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = null;

        var maxBytes = options.MaxObjectMiB * 1024L * 1024L;
        var workingDirectory = Directory.CreateTempSubdirectory("harmony-worker-").FullName;
        try
        {
            var inputPath = Path.Combine(workingDirectory, "upload");
            long written;
            await using (var file = File.Create(inputPath))
                written = await CopyBoundedAsync(context.Request.Body, file, maxBytes, cancellationToken);
            if (written < 0) return TooLarge();
            if (written == 0) return EmptyUpload();

            byte[] audio;
            try
            {
                audio = await normalizer.NormalizeAsync(inputPath, workingDirectory, cancellationToken);
            }
            catch (ExtractionException exception)
            {
                // The upload was unusable (not decodable, too long, too large). Fail the job so the
                // listener stops polling; the fleet can retry after the backoff.
                logger.LogWarning(exception, "Worker upload for {VideoId} failed normalization with {FailureCode}.", videoId, exception.Code);
                await tracks.MarkFailedAsync(lease, exception.Code, clock.GetUtcNow() + TimeSpan.FromMinutes(1), cancellationToken);
                return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Upload could not be normalized",
                    extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
            }

            var objectKey = $"tracks/{videoId}.ogg";
            var etag = '"' + Convert.ToHexString(SHA256.HashData(audio)).ToLowerInvariant() + '"';
            var maxMediaBytes = options.MaxMediaGiB * 1024L * 1024L * 1024L;
            await using var capacityLock = await MediaCapacityLock.AcquireAsync(
                contexts, cancellationToken);
            if (await tracks.GetReadyBytesAsync(cancellationToken) + audio.LongLength > maxMediaBytes)
            {
                await tracks.MarkFailedAsync(
                    lease, "media_capacity_reached",
                    clock.GetUtcNow() + TimeSpan.FromHours(1), cancellationToken);
                return Results.Problem(
                    statusCode: StatusCodes.Status507InsufficientStorage,
                    title: "Media capacity reached",
                    extensions: new Dictionary<string, object?> { ["code"] = "media_capacity_reached" });
            }
            await using (var upload = new MemoryStream(audio, writable: false))
                await objects.PutAsync(objectKey, upload, audio.LongLength, cancellationToken);

            var committed = await tracks.MarkReadyAsync(
                lease, objectKey, audio.LongLength, etag, cancellationToken);
            if (!committed)
            {
                await objects.DeleteAsync(objectKey, cancellationToken);
                return LeaseLost();
            }

            logger.LogInformation("Worker ingested {VideoId} contentLength={ContentLength}.", videoId, audio.LongLength);
            return Results.Ok(new WorkerUploadResult(videoId, audio.LongLength, etag));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static async Task<IResult> FailAsync(
        string videoId, HttpContext context, WorkerFailRequest? request, ITrackRepository tracks,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!VideoIds.IsValid(videoId)) return InvalidVideoId();
        if (!TryLease(context, videoId, out var lease)) return MissingLease();
        var code = SanitizeCode(request?.Code);
        var released = await tracks.MarkFailedAsync(lease, code, clock.GetUtcNow() + TimeSpan.FromMinutes(1), cancellationToken);
        return released ? Results.NoContent() : LeaseLost();
    }

    private static async Task<IResult> VerifyBackupAsync(
        string videoId,
        BackupVerificationRequest request,
        HttpContext context,
        ITrackRepository tracks,
        IObjectStore objects,
        IDbContextFactory<ResolverDbContext> contexts,
        ResolverOptions options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!VideoIds.IsValid(videoId)) return InvalidVideoId();
        if (!TryLease(context, videoId, out var lease)) return MissingLease();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var candidate = await db.BackupCandidates.SingleOrDefaultAsync(
            x => x.VideoId == videoId && x.Status == "verifying", cancellationToken);
        if (candidate is null || candidate.StagingObjectKey is null || candidate.ContentLength is null
            || candidate.ETag is null || candidate.DurationSeconds is null
            || candidate.FingerprintA is null || candidate.FingerprintB is null)
            return Results.NotFound();

        var uploaded = new AudioFingerprint(
            candidate.DurationSeconds.Value, candidate.FingerprintA, candidate.FingerprintB);
        var source = new AudioFingerprint(
            request.DurationSeconds, request.FingerprintA, request.FingerprintB);
        if (!AudioFingerprintService.Matches(uploaded, source))
        {
            await objects.DeleteAsync(candidate.StagingObjectKey, cancellationToken);
            candidate.Status = "rejected";
            candidate.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            await tracks.MarkFailedAsync(
                lease, "backup_fingerprint_mismatch", clock.GetUtcNow() + TimeSpan.FromHours(1), cancellationToken);
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Backup audio did not match the source",
                extensions: new Dictionary<string, object?> { ["code"] = "backup_fingerprint_mismatch" });
        }

        await using var capacityLock = await MediaCapacityLock.AcquireAsync(
            contexts, cancellationToken);
        if (await tracks.GetReadyBytesAsync(cancellationToken) + candidate.ContentLength.Value
            > options.MaxMediaGiB * 1024L * 1024L * 1024L)
            return Results.Problem(
                statusCode: StatusCodes.Status507InsufficientStorage,
                title: "Media capacity reached",
                extensions: new Dictionary<string, object?> { ["code"] = "media_capacity_reached" });

        await using var content = new MemoryStream();
        await objects.CopyToAsync(
            candidate.StagingObjectKey, content, 0, candidate.ContentLength.Value, cancellationToken);
        content.Position = 0;
        var objectKey = $"tracks/{videoId}.ogg";
        await objects.PutAsync(objectKey, content, candidate.ContentLength.Value, cancellationToken);
        var committed = await tracks.MarkReadyAsync(
            lease, objectKey, candidate.ContentLength.Value, candidate.ETag, cancellationToken);
        if (!committed)
        {
            await objects.DeleteAsync(objectKey, cancellationToken);
            return LeaseLost();
        }

        await objects.DeleteAsync(candidate.StagingObjectKey, cancellationToken);
        candidate.Status = "ready";
        candidate.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { videoId, status = "ready" });
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
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

    private static bool TryLease(HttpContext context, string videoId, out IngestionLease lease)
    {
        lease = null!;
        if (!context.Request.Headers.TryGetValue(LeaseHeader, out var values)) return false;
        if (!Guid.TryParse(values.ToString(), out var ownerId)) return false;
        lease = new IngestionLease(videoId, ownerId, default);
        return true;
    }

    private static string SanitizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "worker_extraction_failed";
        var trimmed = code.Trim();
        if (trimmed.Length > 64) trimmed = trimmed[..64];
        return trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? trimmed : "worker_extraction_failed";
    }

    private static IResult InvalidVideoId() => Results.BadRequest(new
    {
        type = "https://harmony-resolver/errors/invalid_video_id",
        title = "invalid_video_id",
        status = 400,
        code = "invalid_video_id"
    });

    private static IResult MissingLease() => Results.Problem(statusCode: StatusCodes.Status400BadRequest,
        title: "Missing or malformed lease token",
        extensions: new Dictionary<string, object?> { ["code"] = "missing_lease_token" });

    private static IResult LeaseLost() => Results.Problem(statusCode: StatusCodes.Status409Conflict,
        title: "Lease lost", extensions: new Dictionary<string, object?> { ["code"] = "lease_lost" });

    private static IResult TooLarge() => Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "Uploaded audio exceeds the size limit",
        extensions: new Dictionary<string, object?> { ["code"] = "object_too_large" });

    private static IResult EmptyUpload() => Results.Problem(statusCode: StatusCodes.Status400BadRequest,
        title: "Uploaded audio was empty",
        extensions: new Dictionary<string, object?> { ["code"] = "empty_upload" });
}

public sealed record WorkerJob(string VideoId, Guid LeaseToken, DateTimeOffset ExpiresAt, string Kind);
public sealed record WorkerUploadResult(string VideoId, long ContentLength, string ETag);
public sealed record WorkerFailRequest(string? Code);
public sealed record BackupVerificationRequest(
    double DurationSeconds, string FingerprintA, string FingerprintB);
