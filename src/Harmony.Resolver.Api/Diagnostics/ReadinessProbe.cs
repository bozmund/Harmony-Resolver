using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Harmony.Resolver.Api.Diagnostics;

public sealed class ReadinessProbe(IServiceProvider services)
{
    public async Task<(bool Ready, IReadOnlyDictionary<string, string> Dependencies)> CheckAsync(
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, string>();
        var contexts = services.GetService<IDbContextFactory<ResolverDbContext>>();
        if (contexts is not null)
        {
            try
            {
                await using var db = await contexts.CreateDbContextAsync(cancellationToken);
                results["postgresql"] = await db.Database.CanConnectAsync(cancellationToken) ? "healthy" : "unhealthy";
            }
            catch { results["postgresql"] = "unhealthy"; }
        }

        var objects = services.GetService<IObjectStore>();
        if (objects is not null)
        {
            try { await objects.EnsureReadyAsync(cancellationToken); results["minio"] = "healthy"; }
            catch { results["minio"] = "unhealthy"; }
        }

        var valkey = services.GetService<IConnectionMultiplexer>();
        if (valkey is not null)
        {
            try { await valkey.GetDatabase().PingAsync(); results["valkey"] = "healthy"; }
            catch { results["valkey"] = "unhealthy"; }
        }

        return (results.Values.All(value => value == "healthy"), results);
    }
}
