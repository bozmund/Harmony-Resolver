using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;

namespace Harmony.Resolver.Api.Infrastructure.Storage;

/// <summary>
/// Fails delegated ingestion jobs that have sat <c>ingesting</c> past <see cref="ResolverOptions.JobMaxAge"/>
/// with no worker actively leasing them, so a listener polling a job the fleet can't fill eventually gets a
/// definitive error instead of polling forever. Jobs with a live (heartbeat-renewed) lease are left alone.
/// </summary>
public sealed class StuckJobReaper(
    ITrackRepository tracks,
    ResolverOptions options,
    TimeProvider clock,
    ILogger<StuckJobReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await SweepAsync(stoppingToken);
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = clock.GetUtcNow();
            var failed = await tracks.FailStuckJobsAsync(
                now - options.JobMaxAge, now + TimeSpan.FromMinutes(15), cancellationToken);
            if (failed > 0)
                logger.LogWarning("Reaped {Count} ingestion job(s) stuck longer than {JobMaxAge}.", failed, options.JobMaxAge);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Stuck-job reaper sweep failed.");
        }
    }
}
