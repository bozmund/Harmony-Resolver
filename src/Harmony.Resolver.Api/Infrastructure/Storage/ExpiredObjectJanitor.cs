using Harmony.Resolver.Api.Abstractions;

namespace Harmony.Resolver.Api.Infrastructure.Storage;

public sealed class ExpiredObjectJanitor(
    ITrackRepository tracks,
    IObjectStore objects,
    TimeProvider clock,
    ILogger<ExpiredObjectJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await SweepAsync(stoppingToken);
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var track in await tracks.ListExpiredAsync(clock.GetUtcNow(), 100, cancellationToken))
        {
            try
            {
                await objects.DeleteAsync(track.ObjectKey!, cancellationToken);
                await tracks.DeleteExpiredAsync(track.VideoId, clock.GetUtcNow(), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Failed to delete expired object for {VideoId}.", track.VideoId);
            }
        }
    }
}
