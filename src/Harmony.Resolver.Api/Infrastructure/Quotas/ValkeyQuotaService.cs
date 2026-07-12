using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Domain;
using StackExchange.Redis;

namespace Harmony.Resolver.Api.Infrastructure.Quotas;

public sealed class ValkeyQuotaService(IConnectionMultiplexer connection, QuotaOptions options, TimeProvider clock)
    : IQuotaService
{
    private const string AcquireScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) then return 0 end
        redis.call('ZADD', KEYS[1], ARGV[3], ARGV[4])
        redis.call('EXPIRE', KEYS[1], 600)
        return 1
        """;

    public async Task<bool> TryConsumeIngestionAsync(RequesterIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = connection.GetDatabase();
        var hour = clock.GetUtcNow().ToUnixTimeSeconds() / 3600;
        var key = (RedisKey)$"quota:ingestion:{identity.Key}:{hour}";
        var value = await database.StringIncrementAsync(key);
        if (value == 1) await database.KeyExpireAsync(key, TimeSpan.FromHours(2));
        var limit = identity.IsAuthenticated ? options.AuthenticatedIngestionsPerHour : options.AnonymousIngestionsPerHour;
        return value <= limit;
    }

    public async Task<IAsyncDisposable?> TryAcquireResponseAsync(
        RequesterIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = connection.GetDatabase();
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var expires = now + TimeSpan.FromMinutes(5).TotalMilliseconds;
        var limit = identity.IsAuthenticated ? options.AuthenticatedConcurrentResponses : options.AnonymousConcurrentResponses;
        var token = Guid.NewGuid().ToString("N");
        var key = (RedisKey)$"quota:responses:{identity.Key}";
        var acquired = (long)await database.ScriptEvaluateAsync(AcquireScript, [key], [now, limit, expires, token]) == 1;
        return acquired ? new Permit(database, key, token) : null;
    }

    private sealed class Permit(IDatabase database, RedisKey key, RedisValue token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await database.SortedSetRemoveAsync(key, token);
    }
}
