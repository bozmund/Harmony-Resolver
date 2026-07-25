using System.Diagnostics;
using System.IO.Pipelines;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Diagnostics;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Metadata;
using Harmony.Resolver.Api.Infrastructure.Security;

namespace Harmony.Resolver.Api.Endpoints;

public static class DistributedResolverEndpoints
{
    /// Matches the client's queue-backfill chunk size. Kept well under the 900-song queues the app
    /// can hold so one render never issues an unbounded query.
    public const int MetadataBatchLimit = 100;

    public static void MapDistributedResolverEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/tracks/{videoId}", GetTrackAsync);
        endpoints.MapGet("/v1/tracks/{videoId}/metadata", GetTrackMetadataAsync);
        endpoints.MapPost("/v1/tracks/metadata:batch", GetTrackMetadataBatchAsync);
        endpoints.MapGet("/v1/tracks/{videoId}/audio", GetAudioAsync);
        endpoints.MapPost("/v1/prefetch", PrefetchAsync);
    }

    /// Cheap metadata read. Like <see cref="GetTrackAsync"/> it takes no quota: it is a single
    /// primary-key lookup and clients call it on every queue render.
    private static async Task<IResult> GetTrackMetadataAsync(
        string videoId, ITrackRepository tracks, MetadataBackfillQueue backfill,
        ResolverOptions options, IJobNotifier notifier,
        CancellationToken cancellationToken)
    {
        if (!VideoIds.IsValid(videoId)) return InvalidVideoId();
        var metadata = await tracks.GetMetadataAsync(videoId, cancellationToken);
        if (metadata is not null) return Results.Ok(TrackMetadataResponse.Ready(metadata));
        await EnqueueMetadataBackfillAsync(videoId, tracks, backfill, options, notifier, cancellationToken);
        return Results.Ok(TrackMetadataResponse.Missing(videoId));
    }

    private static async Task<IResult> GetTrackMetadataBatchAsync(
        TrackMetadataBatchRequest request, ITrackRepository tracks, MetadataBackfillQueue backfill,
        ResolverOptions options, IJobNotifier notifier,
        CancellationToken cancellationToken)
    {
        if (request.VideoIds is null || request.VideoIds.Count is < 1 or > MetadataBatchLimit
            || request.VideoIds.Distinct(StringComparer.Ordinal).Count() != request.VideoIds.Count
            || request.VideoIds.Any(id => !VideoIds.IsValid(id)))
            return Results.BadRequest(new { code = "invalid_metadata_request" });

        var found = (await tracks.GetMetadataBatchAsync(request.VideoIds, cancellationToken))
            .ToDictionary(x => x.VideoId, StringComparer.Ordinal);
        // Preserve request order and always answer for every id, so the client can zip the response
        // against its queue without a second lookup.
        var results = new List<TrackMetadataResponse>(request.VideoIds.Count);
        foreach (var videoId in request.VideoIds)
        {
            if (found.TryGetValue(videoId, out var metadata))
            {
                results.Add(TrackMetadataResponse.Ready(metadata));
                continue;
            }
            await EnqueueMetadataBackfillAsync(videoId, tracks, backfill, options, notifier, cancellationToken);
            results.Add(TrackMetadataResponse.Missing(videoId));
        }
        return Results.Ok(new TrackMetadataBatchResponse(results));
    }

    private static async Task EnqueueMetadataBackfillAsync(
        string videoId, ITrackRepository tracks, MetadataBackfillQueue backfill,
        ResolverOptions options, IJobNotifier notifier, CancellationToken cancellationToken)
    {
        if (options.ExtractionMode != ExtractionMode.Delegated)
        {
            backfill.TryEnqueue(videoId);
            return;
        }
        await tracks.EnqueueMetadataBackfillAsync(videoId, cancellationToken);
        await notifier.NotifyAsync(videoId, cancellationToken);
    }

    private static async Task<IResult> PrefetchAsync(
        PrefetchRequest request,
        HttpContext context,
        ITrackRepository tracks,
        IQuotaService quotas,
        RequestIdentityResolver identities,
        IJobNotifier jobNotifier,
        ResolverOptions options,
        CancellationToken cancellationToken)
    {
        if (request.VideoIds is null || request.VideoIds.Count is < 1 or > 3
            || request.VideoIds.Distinct(StringComparer.Ordinal).Count() != request.VideoIds.Count
            || request.VideoIds.Any(id => !VideoIds.IsValid(id)))
            return Results.BadRequest(new { code = "invalid_prefetch_request" });

        var identity = identities.Resolve(context);
        var results = new List<PrefetchResult>(request.VideoIds.Count);
        var readyBytes = await tracks.GetReadyBytesAsync(cancellationToken);
        var prefetchLimit = options.PrefetchStopGiB * 1024L * 1024L * 1024L;
        foreach (var videoId in request.VideoIds)
        {
            var current = await tracks.GetAsync(videoId, cancellationToken);
            if (current?.Status == TrackStatus.Ready)
            {
                results.Add(new(videoId, "ready"));
                continue;
            }
            if (current?.Status == TrackStatus.Ingesting)
            {
                await tracks.EnqueueAsync(videoId, IngestionPriority.Prefetch, cancellationToken);
                results.Add(new(videoId, "ingesting"));
                continue;
            }
            if (readyBytes >= prefetchLimit)
            {
                results.Add(new(videoId, "blocked_capacity"));
                continue;
            }
            if (!await quotas.TryConsumeIngestionAsync(identity, cancellationToken))
            {
                results.Add(new(videoId, "rate_limited"));
                continue;
            }
            await tracks.EnqueueAsync(videoId, IngestionPriority.Prefetch, cancellationToken);
            if (options.ExtractionMode == ExtractionMode.Delegated)
                await jobNotifier.NotifyAsync(videoId, cancellationToken);
            results.Add(new(videoId, "queued"));
        }
        return Results.Accepted(value: new PrefetchResponse(results));
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
        var stopwatch = Stopwatch.StartNew();
        if (!VideoIds.IsValid(videoId))
        {
            await InvalidVideoId().ExecuteAsync(context);
            return;
        }

        var identity = identities.Resolve(context);

        var track = await tracks.GetAsync(videoId, context.RequestAborted);
        if (track is { Status: TrackStatus.Ready, ObjectKey: not null, ContentLength: not null, ETag: not null })
        {
            await using var readyPermit = await quotas.TryAcquireResponseAsync(identity, context.RequestAborted);
            if (readyPermit is null) { await ResponseLimited(context); return; }
            var fullTransferDurationMs = await ServeReadyAsync(
                context,
                tracks,
                objects,
                track,
                metrics,
                stopwatch);
            await RecordServedAsync(
                metrics,
                playHistory,
                logger,
                videoId,
                "hit",
                fullTransferDurationMs,
                identity.Key);
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
            await tracks.EnqueueAsync(videoId, IngestionPriority.Urgent, CancellationToken.None);
            await IngestionInProgress(context).ExecuteAsync(context);
            return;
        }

        // Delegated mode: the server never contacts YouTube. Record the cache miss as a pending job
        // for the downloader fleet and tell the listener to poll. The ingestion quota still applies so
        // a single listener can't flood the job queue.
        if (options.ExtractionMode == ExtractionMode.Delegated)
        {
            if (await tracks.GetReadyBytesAsync(CancellationToken.None)
                >= options.MaxMediaGiB * 1024L * 1024L * 1024L)
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status507InsufficientStorage,
                    title: "Media capacity reached",
                    extensions: new Dictionary<string, object?> { ["code"] = "media_capacity_reached" })
                    .ExecuteAsync(context);
                return;
            }
            if (!await quotas.TryConsumeIngestionAsync(identity, CancellationToken.None))
            {
                context.Response.Headers.RetryAfter = "3600";
                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Ingestion quota exceeded",
                    extensions: new Dictionary<string, object?> { ["code"] = "ingestion_rate_limited" }).ExecuteAsync(context);
                return;
            }
            await tracks.EnqueueAsync(videoId, IngestionPriority.Urgent, CancellationToken.None);
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
            var extracted = await extractor.ExtractAsync(videoId, timeout.Token);
            var audio = extracted.Audio;
            if (audio.LongLength > options.MaxObjectMiB * 1024L * 1024L)
                throw new InvalidDataException("object_too_large");
            // Persist before streaming: the response can be abandoned mid-transfer, and metadata we
            // already paid for should survive that.
            if (extracted.Metadata is { IsEmpty: false } metadata)
                await tracks.SetMetadataAsync(metadata, CancellationToken.None);

            var objectKey = $"tracks/{videoId}.ogg";
            var etag = '"' + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(audio)).ToLowerInvariant() + '"';

            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1024 * 1024, resumeWriterThreshold: 512 * 1024));
            await using var uploadStream = pipe.Reader.AsStream();
            var uploadTask = objects.PutAsync(objectKey, uploadStream, -1, CancellationToken.None);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "audio/ogg";
            context.Response.Headers.ETag = etag;
            var responseConnected = true;
            var responseBody = metrics.TrackFirstResponseWrite(
                context.Response.Body,
                stopwatch,
                AudioCacheStatus.Miss,
                AudioRangeKind.Initial);
            long? fullTransferDurationMs = null;
            Exception? teeFailure = null;
            try
            {
                for (var offset = 0; offset < audio.Length; offset += 64 * 1024)
                {
                    var chunk = audio.AsMemory(offset, Math.Min(64 * 1024, audio.Length - offset));
                    await pipe.Writer.WriteAsync(chunk, CancellationToken.None);
                    if (responseConnected)
                    {
                        try { await responseBody.WriteAsync(chunk, context.RequestAborted); }
                        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { responseConnected = false; }
                        catch (IOException) { responseConnected = false; }
                    }
                }
                if (responseConnected)
                    fullTransferDurationMs = stopwatch.ElapsedMilliseconds;
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
            var committed = await tracks.MarkReadyAsync(
                lease, objectKey, audio.LongLength, etag, CancellationToken.None);
            if (!committed)
            {
                await objects.DeleteAsync(objectKey, CancellationToken.None);
                throw new InvalidOperationException("lease_lost");
            }

            if (fullTransferDurationMs is { } durationMs)
            {
                await RecordServedAsync(
                    metrics,
                    playHistory,
                    logger,
                    videoId,
                    "miss",
                    durationMs,
                    identity.Key);
            }
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

    private static async Task<long> ServeReadyAsync(
        HttpContext context, ITrackRepository tracks, IObjectStore objects, StoredTrack track,
        ResolverMetrics metrics, Stopwatch requestStopwatch)
    {
        var length = track.ContentLength!.Value;
        var (offset, count, partial) = ParseRange(context.Request.Headers.Range, length);
        context.Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        context.Response.ContentType = "audio/ogg";
        context.Response.ContentLength = count;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ETag = track.ETag;
        if (partial) context.Response.Headers.ContentRange = $"bytes {offset}-{offset + count - 1}/{length}";
        var responseBody = metrics.TrackFirstResponseWrite(
            context.Response.Body,
            requestStopwatch,
            AudioCacheStatus.Hit,
            offset == 0 ? AudioRangeKind.Initial : AudioRangeKind.Nonzero);
        await objects.CopyToAsync(track.ObjectKey!, responseBody, offset, count, context.RequestAborted);
        var fullTransferDurationMs = requestStopwatch.ElapsedMilliseconds;
        await tracks.TouchAsync(track.VideoId, CancellationToken.None);
        return fullTransferDurationMs;
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

public sealed record TrackMetadataBatchRequest(IReadOnlyList<string> VideoIds);

public sealed record TrackMetadataBatchResponse(IReadOnlyList<TrackMetadataResponse> Tracks);

/// <param name="Status"><c>ready</c> when metadata is known, <c>missing</c> when a lazy fill was
/// scheduled. Mirrors <see cref="TrackInfo"/>'s convention of synthesizing a status rather than
/// returning 404, so a batch response can answer for every requested id.</param>
public sealed record TrackMetadataResponse(
    string VideoId,
    string Status,
    string? Title = null,
    IReadOnlyList<string>? Artists = null,
    string? Album = null,
    int? DurationSeconds = null,
    string? ThumbnailUrl = null)
{
    public static TrackMetadataResponse Ready(TrackMetadata metadata) => new(
        metadata.VideoId, "ready", metadata.Title, metadata.Artists, metadata.Album,
        metadata.DurationSeconds, metadata.ThumbnailUrl);

    public static TrackMetadataResponse Missing(string videoId) => new(videoId, "missing");
}

public sealed record PrefetchRequest(IReadOnlyList<string> VideoIds);
public sealed record PrefetchResult(string VideoId, string Status);
public sealed record PrefetchResponse(IReadOnlyList<PrefetchResult> Tracks);
