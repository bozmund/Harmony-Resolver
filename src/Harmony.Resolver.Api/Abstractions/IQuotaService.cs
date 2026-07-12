using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Abstractions;

public interface IQuotaService
{
    Task<bool> TryConsumeIngestionAsync(RequesterIdentity identity, CancellationToken cancellationToken);
    Task<IAsyncDisposable?> TryAcquireResponseAsync(RequesterIdentity identity, CancellationToken cancellationToken);
}
