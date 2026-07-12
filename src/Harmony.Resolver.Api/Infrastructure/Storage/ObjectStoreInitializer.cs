using Harmony.Resolver.Api.Abstractions;

namespace Harmony.Resolver.Api.Infrastructure.Storage;

public sealed class ObjectStoreInitializer(IObjectStore objects) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => objects.EnsureReadyAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
