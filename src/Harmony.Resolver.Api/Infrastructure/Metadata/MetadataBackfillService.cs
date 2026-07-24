using Harmony.Resolver.Api.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Harmony.Resolver.Api.Infrastructure.Metadata;

/// <summary>
/// Drains <see cref="MetadataBackfillQueue"/> one id at a time. Serial by design: backfill is
/// strictly background work behind a client that already has its own resolution path, so it must
/// never compete with audio ingestion for upstream budget.
/// </summary>
public sealed class MetadataBackfillService(
    MetadataBackfillQueue queue,
    ITrackMetadataSource source,
    ITrackRepository tracks,
    ILogger<MetadataBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var videoId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var metadata = await source.FetchAsync(videoId, stoppingToken);
                if (metadata is null || metadata.IsEmpty) continue;
                await tracks.SetMetadataAsync(metadata, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Metadata backfill failed for {VideoId}.", videoId);
            }
            queue.Prune();
        }
    }
}
