using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Quotas;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class QuotaTests
{
    [Fact]
    public async Task Anonymous_ingestion_limit_defaults_to_ten_per_hour()
    {
        var quotas = new InMemoryQuotaService(new QuotaOptions(), TimeProvider.System);
        var identity = new RequesterIdentity("anonymous", false);
        for (var index = 0; index < 10; index++)
            Assert.True(await quotas.TryConsumeIngestionAsync(identity, CancellationToken.None));
        Assert.False(await quotas.TryConsumeIngestionAsync(identity, CancellationToken.None));
    }

    [Fact]
    public async Task Anonymous_response_limit_defaults_to_two()
    {
        var quotas = new InMemoryQuotaService(new QuotaOptions(), TimeProvider.System);
        var identity = new RequesterIdentity("anonymous", false);
        await using var first = await quotas.TryAcquireResponseAsync(identity, CancellationToken.None);
        await using var second = await quotas.TryAcquireResponseAsync(identity, CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(await quotas.TryAcquireResponseAsync(identity, CancellationToken.None));
    }
}
