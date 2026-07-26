using Harmony.Resolver.Api.Abstractions;

namespace Harmony.Resolver.Api.Infrastructure.Storage;

/// <summary>Compatibility shell: Resolver media is permanent and no expiry sweep is performed.</summary>
public sealed class ExpiredObjectJanitor : BackgroundService
{
    public ExpiredObjectJanitor(
        ITrackRepository tracks,
        IObjectStore objects,
        TimeProvider clock,
        ILogger<ExpiredObjectJanitor> logger)
    {
    }
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
