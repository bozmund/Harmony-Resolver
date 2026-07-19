using Harmony.Resolver.Api.Abstractions;

namespace Harmony.Resolver.Api.Infrastructure.Messaging;

/// <summary>
/// Periodically re-rings the doorbell for every job still pending (<c>ingesting</c> with no live lease).
/// This is the server-side safety net that replaces client polling: it covers a broker restart, a lost
/// notification, or all downloaders having been offline when a job was first enqueued. Re-notifying an
/// already-claimed job is harmless — the fleet's next claim simply returns nothing new.
/// </summary>
public sealed class JobRepublisher(
    ITrackRepository tracks,
    IJobNotifier notifier,
    TimeProvider clock,
    ILogger<JobRepublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await SweepAsync(stoppingToken);
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pending = await tracks.ListPendingJobsAsync(clock.GetUtcNow(), 200, cancellationToken);
            foreach (var videoId in pending)
                await notifier.NotifyAsync(videoId, cancellationToken);
            if (pending.Count > 0)
                logger.LogDebug("Re-rang the doorbell for {Count} pending job(s).", pending.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Job republisher sweep failed.");
        }
    }
}
