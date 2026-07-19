using System.Diagnostics;
using System.IO.Pipelines;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Diagnostics;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Security;

namespace Harmony.Resolver.Api.Endpoints;

public static class DistributedResolverEndpoints
{
    public static void MapDistributedResolverEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/tracks/{videoId}", GetTrackAsync);
        endpoints.MapGet("/v1/tracks/{videoId}/audio", GetAudioAsync);
    }

    private static async Task<IResult> GetTrackAsync(
        string videoId, ITrackRepository tracks, CancellationToken cancellationToken)
    {
        if (!VideoIds.IsValid(videoId)) return InvalidVideoId();
        var track = await tracks.GetAsync(videoId, cancellationToken);
        return Results.Ok(track is null
            ? new TrackInfo(videoId, TrackStatus.Missing)
            : new TrackInfo(track.VideoId, track.Status, track.ContentLength, track.ETag, track.FailureCode, track.ExpiresAt));
    }

    private static async Task GetAudioAsync(
        string videoId,
        HttpContext context,
        ITrackRepository tracks,
        IObjectStore objects,
        IMediaExtractor extractor,
        ResolverOptions options,
        TimeProvider clock,
        IQuotaService quotas,
        RequestIdentityResolver identities,
        ResolverMetrics metrics,
        PlayHistoryWriter playHistory,
        IJobNotifier jobNotifier,
        ILogger<Program> logger)
    {
        if (!VideoIds.IsValid(videoId))
        {
            await InvalidVideoId().ExecuteAsync(context);
            return;
        }

        var identity = identities.Resolve(context);
        var stopwatch = Stopwatch.StartNew();

        var track = await tracks.GetAsync(videoId, context.RequestAborted);
        if (track is { Status: TrackStatus.Ready, ObjectKey: not null, ContentLength: not null, ETag: not null })
        {
            await using var readyPermit = await quotas.TryAcquireResponseAsync(identity, context.RequestAborted);
            if (readyPermit is null) { await ResponseLimited(context); return; }
            await ServeReadyAsync(context, tracks, objects, track, options, clock);
            await RecordServedAsync(metrics, playHistory, logger, videoId, "hit", stopwatch.ElapsedMilliseconds, identity.Key);
            return;
        }

        if (track is { Status: TrackStatus.Failed, RetryAfter: not null } && track.RetryAfter > clock.GetUtcNow())
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((track.RetryAfter.Value - clock.GetUtcNow()).TotalSeconds)).ToString();
            await Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Extraction failed",
                extensions: new Dictionary<string, object?> { ["code"] = track.FailureCode ?? "extraction_failed" }).ExecuteAsync(context);
            return;
        }

        if (track?.Status == TrackStatus.Ingesting)
        {
            await IngestionInProgress(context).ExecuteAsync(context);
            return;
        }

        // Delegated mode: the server never contacts YouTube. Record the cache miss as a pending job
        // for the downloader fleet and tell the listener to poll. The ingestion quota still applies so
        // a single listener can't flood the job queue.
        if (options.ExtractionMode == ExtractionMode.Delegated)
        {
            if (!await quotas.TryConsumeIngestionAsync(identity, CancellationToken.None))
            {
                context.Response.Headers.RetryAfter = "3600";
                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Ingestion quota exceeded",
                    extensions: new Dictionary<string, object?> { ["code"] = "ingestion_rate_limited" }).ExecuteAsync(context);
                return;
            }
            await tracks.EnqueueAsync(videoId, CancellationToken.None);
            await jobNotifier.NotifyAsync(videoId, CancellationToken.None);
            await IngestionInProgress(context).ExecuteAsync(context);
            return;
        }

        var lease = await tracks.TryAcquireLeaseAsync(videoId, Guid.NewGuid(), options.LeaseDuration, CancellationToken.None);
        if (lease is null)
        {
            await IngestionInProgress(context).ExecuteAsync(context);
            return;
        }
        if (!await quotas.TryConsumeIngestionAsync(identity, CancellationToken.None))
        {
            await tracks.AbandonLeaseAsync(lease, CancellationToken.None);
            context.Response.Headers.RetryAfter = "3600";
            await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Ingestion quota exceeded",
                extensions: new Dictionary<string, object?> { ["code"] = "ingestion_rate_limited" }).ExecuteAsync(context);
            return;
        }
        await using var leaderPermit = await quotas.TryAcquireResponseAsync(identity, CancellationToken.None);
        if (leaderPermit is null)
        {
            await tracks.AbandonLeaseAsync(lease, CancellationToken.None);
            await ResponseLimited(context);
            return;
        }

        // Ingestion intentionally ignores RequestAborted. The response can stop while cache completion continues.
        using var timeout = new CancellationTokenSource(options.ExtractionTimeout);
        try
        {
            var audio = await extractor.ExtractAsync(videoId, timeout.Token);
            if (audio.LongLength > options.MaxObjectMiB * 1024L * 1024L)
                throw new InvalidDataException("object_too_large");

            var objectKey = $"tracks/{videoId}.ogg";
            var etag = '"' + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(audio)).ToLowerInvariant() + '"';

            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1024 * 1024, resumeWriterThreshold: 512 * 1024));
            await using var uploadStream = pipe.Reader.AsStream();
            var uploadTask = objects.PutAsync(objectKey, uploadStream, -1, CancellationToken.None);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "audio/ogg";
            context.Response.Headers.ETag = etag;
            var responseConnected = true;
            Exception? teeFailure = null;
            try
            {
                for (var offset = 0; offset < audio.Length; offset += 64 * 1024)
                {
                    var chunk = audio.AsMemory(offset, Math.Min(64 * 1024, audio.Length - offset));
                    await pipe.Writer.WriteAsync(chunk, CancellationToken.None);
                    if (responseConnected)
                    {
                        try { await context.Response.Body.WriteAsync(chunk, context.RequestAborted); }
                        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { responseConnected = false; }
                        catch (IOException) { responseConnected = false; }
                    }
                }
            }
            catch (Exception exception)
            {
                teeFailure = exception;
                throw;
            }
            finally
            {
                await pipe.Writer.CompleteAsync(teeFailure);
            }
            await uploadTask;
            var committed = await tracks.MarkReadyAsync(lease, objectKey, audio.LongLength, etag,
                clock.GetUtcNow() + options.InactivityExpiry, CancellationToken.None);
            if (!committed)
            {
                await objects.DeleteAsync(objectKey, CancellationToken.None);
                throw new InvalidOperationException("lease_lost");
            }

            await RecordServedAsync(metrics, playHistory, logger, videoId, "miss", stopwatch.ElapsedMilliseconds, identity.Key);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            metrics.ExtractionFailures.Add(1, new KeyValuePair<string, object?>("failure_code", "extraction_timeout"));
            logger.LogWarning("Extraction timed out for {VideoId} after {DurationMs}ms, identity={IdentityHash}",
                videoId, stopwatch.ElapsedMilliseconds, identity.Key);
            await tracks.MarkFailedAsync(lease, "extraction_timeout", clock.GetUtcNow() + TimeSpan.FromMinutes(1), CancellationToken.None);
            if (!context.Response.HasStarted)
                await ExtractionFailed("extraction_timeout").ExecuteAsync(context);
        }
        catch (Exception exception)
        {
            var code = exception.Message is "object_too_large" or "lease_lost" ? exception.Message : "extraction_failed";
            metrics.ExtractionFailures.Add(1, new KeyValuePair<string, object?>("failure_code", code));
            logger.LogWarning(exception, "Extraction failed for {VideoId} with {FailureCode} after {DurationMs}ms, identity={IdentityHash}",
                videoId, code, stopwatch.ElapsedMilliseconds, identity.Key);
            await tracks.MarkFailedAsync(lease, code, clock.GetUtcNow() + TimeSpan.FromMinutes(1), CancellationToken.None);
            if (!context.Response.HasStarted)
                await ExtractionFailed(code).ExecuteAsync(context);
        }
    }

    private static async Task RecordServedAsync(
        ResolverMetrics metrics, PlayHistoryWriter playHistory, ILogger logger,
        string videoId, string cache, long durationMs, string identityHash)
    {
        metrics.AudioServeDuration.Record(durationMs / 1000.0, new KeyValuePair<string, object?>("cache", cache));
        logger.LogInformation("Served {VideoId} cache={Cache} durationMs={DurationMs} identity={IdentityHash}",
            videoId, cache, durationMs, identityHash);
        try
        {
            await playHistory.WriteAsync(videoId, identityHash, cache, durationMs, CancellationToken.None);
        }
        catch
        {
            // Best-effort history write; never fail an already-served response over this.
        }
    }

    private static async Task ServeReadyAsync(
        HttpContext context, ITrackRepository tracks, IObjectStore objects, StoredTrack track,
        ResolverOptions options, TimeProvider clock)
    {
        var length = track.ContentLength!.Value;
        var (offset, count, partial) = ParseRange(context.Request.Headers.Range, length);
        context.Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        context.Response.ContentType = "audio/ogg";
        context.Response.ContentLength = count;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ETag = track.ETag;
        if (partial) context.Response.Headers.ContentRange = $"bytes {offset}-{offset + count - 1}/{length}";
        await objects.CopyToAsync(track.ObjectKey!, context.Response.Body, offset, count, context.RequestAborted);
        await tracks.TouchAsync(track.VideoId, clock.GetUtcNow() + options.InactivityExpiry, CancellationToken.None);
    }

    private static (long Offset, long Count, bool Partial) ParseRange(string? value, long length)
    {
        if (string.IsNullOrWhiteSpace(value)) return (0, length, false);
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(','))
            throw new BadHttpRequestException("Only a single byte range is supported.", StatusCodes.Status416RangeNotSatisfiable);
        var parts = value[6..].Split('-', 2);
        if (!long.TryParse(parts[0], out var start) || start < 0 || start >= length)
            throw new BadHttpRequestException("Invalid byte range.", StatusCodes.Status416RangeNotSatisfiable);
        var end = string.IsNullOrEmpty(parts[1]) ? length - 1 : long.Parse(parts[1]);
        end = Math.Min(end, length - 1);
        return end < start ? throw new BadHttpRequestException("Invalid byte range.", StatusCodes.Status416RangeNotSatisfiable) : (start, end - start + 1, true);
    }

    private static IResult InvalidVideoId() => Results.BadRequest(new
    {
        type = "https://harmony-resolver/errors/invalid_video_id",
        title = "invalid_video_id",
        detail = "A YouTube video ID must contain exactly 11 safe characters.",
        status = 400,
        code = "invalid_video_id"
    });

    private static IResult IngestionInProgress(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "2";
        return Results.Json(new
        {
            type = "https://harmony-resolver/errors/ingestion_in_progress",
            title = "ingestion_in_progress",
            detail = "Another replica is ingesting this track.",
            status = 202,
            code = "ingestion_in_progress"
        }, statusCode: 202, contentType: "application/problem+json");
    }

    private static IResult ExtractionFailed(string code) => Results.Problem(
        statusCode: StatusCodes.Status502BadGateway,
        title: "Extraction failed",
        extensions: new Dictionary<string, object?> { ["code"] = code });

    private static async Task ResponseLimited(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "2";
        await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
            title: "Response concurrency limit reached",
            extensions: new Dictionary<string, object?> { ["code"] = "response_concurrency_limited" }).ExecuteAsync(context);
    }
}
