using System.Collections.Concurrent;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Infrastructure.Quotas;

public sealed class InMemoryQuotaService(QuotaOptions options, TimeProvider clock) : IQuotaService
{
    private readonly ConcurrentDictionary<string, int> _ingestions = new();
    private readonly ConcurrentDictionary<string, int> _responses = new();

    public Task<bool> TryConsumeIngestionAsync(RequesterIdentity identity, CancellationToken cancellationToken)
    {
        var hour = clock.GetUtcNow().ToUnixTimeSeconds() / 3600;
        var count = _ingestions.AddOrUpdate($"{identity.Key}:{hour}", 1, (_, value) => value + 1);
        var limit = identity.IsAuthenticated ? options.AuthenticatedIngestionsPerHour : options.AnonymousIngestionsPerHour;
        return Task.FromResult(count <= limit);
    }

    public Task<IAsyncDisposable?> TryAcquireResponseAsync(RequesterIdentity identity, CancellationToken cancellationToken)
    {
        var limit = identity.IsAuthenticated ? options.AuthenticatedConcurrentResponses : options.AnonymousConcurrentResponses;
        var count = _responses.AddOrUpdate(identity.Key, 1, (_, value) => value + 1);
        if (count > limit)
        {
            _responses.AddOrUpdate(identity.Key, 0, (_, value) => Math.Max(0, value - 1));
            return Task.FromResult<IAsyncDisposable?>(null);
        }
        return Task.FromResult<IAsyncDisposable?>(new Permit(_responses, identity.Key));
    }

    private sealed class Permit(ConcurrentDictionary<string, int> counts, string key) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            counts.AddOrUpdate(key, 0, (_, value) => Math.Max(0, value - 1));
            return ValueTask.CompletedTask;
        }
    }
}
