using Makaretu.Dns;
using Harmony.Resolver.Discovery;

var settings = DiscoverySettings.FromEnvironment();

using var multicast = new MulticastService();
using var discovery = new ServiceDiscovery(multicast);
var profile = new ServiceProfile($"Harmony Resolver ({settings.InstanceName})", settings.ServiceType, (ushort)settings.Port);
profile.AddProperty("scheme", settings.Scheme);
profile.AddProperty("apiVersion", settings.ApiVersion);
profile.AddProperty("environment", settings.Environment);

discovery.Advertise(profile);
multicast.Start();
Console.WriteLine($"Advertising {settings.ServiceType} on {settings.Scheme}://{settings.InstanceName}:{settings.Port}");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
try { await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token); }
catch (OperationCanceledException) { }
finally
{
    discovery.Unadvertise(profile);
    multicast.Stop();
}
