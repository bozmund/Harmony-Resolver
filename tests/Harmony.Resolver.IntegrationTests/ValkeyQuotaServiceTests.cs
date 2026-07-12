using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Quotas;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Harmony.Resolver.IntegrationTests;

public sealed class ValkeyQuotaServiceTests : IAsyncLifetime
{
    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:8-alpine")
        .Build();

    private ConnectionMultiplexer _connection = null!;
    private static readonly TimeProvider Clock = TimeProvider.System;

    public async Task InitializeAsync()
    {
        await _valkey.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_valkey.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    [Fact]
    public async Task Anonymous_ingestion_limit_defaults_to_ten_per_hour()
    {
        var quotas = new ValkeyQuotaService(_connection, new QuotaOptions(), Clock);
        var identity = new RequesterIdentity("anonymous-key", false);

        for (var i = 0; i < 10; i++)
            Assert.True(await quotas.TryConsumeIngestionAsync(identity, CancellationToken.None));

        Assert.False(await quotas.TryConsumeIngestionAsync(identity, CancellationToken.None));
    }

    [Fact]
    public async Task Authenticated_ingestion_limit_defaults_to_one_hundred_per_hour()
    {
        var quotas = new ValkeyQuotaService(_connection, new QuotaOptions(), Clock);
        var identity = new RequesterIdentity("auth-key", true);

        for (var i = 0; i < 100; i++)
            Assert.True(await quotas.TryConsumeIngestionAsync(identity, CancellationToken.None));

        Assert.False(await quotas.TryConsumeIngestionAsync(identity, CancellationToken.None));
    }

    [Fact]
    public async Task Anonymous_response_concurrency_defaults_to_two()
    {
        var quotas = new ValkeyQuotaService(_connection, new QuotaOptions(), Clock);
        var identity = new RequesterIdentity("anon-concurrent", false);

        await using var first = await quotas.TryAcquireResponseAsync(identity, CancellationToken.None);
        await using var second = await quotas.TryAcquireResponseAsync(identity, CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);

        await using var third = await quotas.TryAcquireResponseAsync(identity, CancellationToken.None);
        Assert.Null(third);
    }

    [Fact]
    public async Task Authenticated_response_concurrency_defaults_to_five()
    {
        var quotas = new ValkeyQuotaService(_connection, new QuotaOptions(), Clock);
        var identity = new RequesterIdentity("auth-concurrent", true);

        var permits = new List<IAsyncDisposable?>();
        for (var i = 0; i < 5; i++)
        {
            var permit = await quotas.TryAcquireResponseAsync(identity, CancellationToken.None);
            permits.Add(permit);
            Assert.NotNull(permit);
        }

        await using var extra = await quotas.TryAcquireResponseAsync(identity, CancellationToken.None);
        Assert.Null(extra);

        foreach (var p in permits) p?.DisposeAsync();
    }

    [Fact]
    public async Task Releasing_a_response_permit_allows_a_new_one()
    {
        var quotas = new ValkeyQuotaService(_connection, new QuotaOptions(), Clock);
        var identity = new RequesterIdentity("release-test", false);

        await using (var first = await quotas.TryAcquireResponseAsync(identity, CancellationToken.None))
        {
            Assert.NotNull(first);
        }

        // After disposal the slot is free
        await using var second = await quotas.TryAcquireResponseAsync(identity, CancellationToken.None);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Ingestion_counters_are_independent_per_identity()
    {
        var quotas = new ValkeyQuotaService(_connection, new QuotaOptions { AnonymousIngestionsPerHour = 5 }, Clock);

        var anonA = new RequesterIdentity("independent-a", false);
        var anonB = new RequesterIdentity("independent-b", false);

        for (var i = 0; i < 5; i++)
            Assert.True(await quotas.TryConsumeIngestionAsync(anonA, CancellationToken.None));

        Assert.False(await quotas.TryConsumeIngestionAsync(anonA, CancellationToken.None));

        // anonB should still have its full budget
        for (var i = 0; i < 5; i++)
            Assert.True(await quotas.TryConsumeIngestionAsync(anonB, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_response_permits_are_independent_per_identity()
    {
        var quotas = new ValkeyQuotaService(_connection, new QuotaOptions { AnonymousConcurrentResponses = 2 }, Clock);
        var anonA = new RequesterIdentity("conc-independent-a", false);
        var anonB = new RequesterIdentity("conc-independent-b", false);

        // Fill A to its limit
        await using var a1 = await quotas.TryAcquireResponseAsync(anonA, CancellationToken.None);
        await using var a2 = await quotas.TryAcquireResponseAsync(anonA, CancellationToken.None);
        Assert.NotNull(a1);
        Assert.NotNull(a2);
        Assert.Null(await quotas.TryAcquireResponseAsync(anonA, CancellationToken.None));

        // B should be unaffected
        await using var b1 = await quotas.TryAcquireResponseAsync(anonB, CancellationToken.None);
        await using var b2 = await quotas.TryAcquireResponseAsync(anonB, CancellationToken.None);
        Assert.NotNull(b1);
        Assert.NotNull(b2);
    }
}