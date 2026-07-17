using Harmony.Resolver.Discovery;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class DiscoverySettingsTests
{
    [Fact]
    public void Defaults_match_the_phone_discovery_contract()
    {
        var settings = new DiscoverySettings(8088, "http", "Development", "test-host");

        Assert.Equal("_harmony-resolver._tcp", settings.ServiceType);
        Assert.Equal("1", settings.ApiVersion);
        Assert.Equal(8088, settings.Port);
        Assert.Equal("http", settings.Scheme);
        Assert.Equal("Development", settings.Environment);
    }
}
