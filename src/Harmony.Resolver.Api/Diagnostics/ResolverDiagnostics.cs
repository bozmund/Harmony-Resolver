using Harmony.Resolver.Api.Abstractions;

namespace Harmony.Resolver.Api.Diagnostics;

public sealed class ResolverDiagnostics(
    IServiceProvider services,
    TimeProvider clock,
    IConfiguration configuration)
{
    public async Task<object> SnapshotAsync(CancellationToken cancellationToken)
    {
        var repository = services.GetService<ITrackRepository>();
        if (repository is not null)
        {
            var statistics = await repository.GetStatisticsAsync(clock.GetUtcNow(), cancellationToken);
            var failures = await repository.ListFailuresAsync(clock.GetUtcNow() - TimeSpan.FromHours(24), 20, cancellationToken);
            return new
            {
                generatedAt = clock.GetUtcNow(),
                mode = "distributed",
                statistics.StatusCounts,
                statistics.ActiveLeases,
                recentFailures = failures.Select(x => new { x.VideoId, x.FailureCode, x.RetryAfter }),
                deployment = new { environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production" }
            };
        }

        var catalog = services.GetRequiredService<ITrackCatalog>();
        var snapshot = catalog.Snapshot();
        return new
        {
            generatedAt = clock.GetUtcNow(),
            mode = "in-memory",
            statusCounts = snapshot.GroupBy(x => x.Status).ToDictionary(x => x.Key.ToString().ToLowerInvariant(), x => x.Count()),
            activeLeases = snapshot.Count(x => x.Status == Domain.TrackStatus.Ingesting),
            recentFailures = snapshot.Where(x => x.Status == Domain.TrackStatus.Failed).Take(20)
        };
    }
}
