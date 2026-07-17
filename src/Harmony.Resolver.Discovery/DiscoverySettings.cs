namespace Harmony.Resolver.Discovery;

public sealed record DiscoverySettings(
    int Port,
    string Scheme,
    string Environment,
    string InstanceName,
    string ServiceType = "_harmony-resolver._tcp",
    string ApiVersion = "1")
{
    public static DiscoverySettings FromEnvironment()
    {
        var port = int.TryParse(System.Environment.GetEnvironmentVariable("RESOLVER_PORT"), out var configuredPort)
            ? configuredPort
            : 8088;
        return new DiscoverySettings(
            port,
            System.Environment.GetEnvironmentVariable("RESOLVER_SCHEME") ?? "http",
            System.Environment.GetEnvironmentVariable("RESOLVER_ENVIRONMENT") ?? "Development",
            System.Environment.GetEnvironmentVariable("RESOLVER_DISCOVERY_NAME") ?? System.Environment.MachineName);
    }
}
