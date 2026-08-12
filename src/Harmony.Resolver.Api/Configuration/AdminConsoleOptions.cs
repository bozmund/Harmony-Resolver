namespace Harmony.Resolver.Api.Configuration;

/// <summary>
/// Public configuration for the SSH-tunneled administrator browser client.
/// The client id is intentionally public; its browser redirect URI is protected
/// by the host-loopback Docker binding and the API permission policy.
/// </summary>
public sealed class AdminConsoleOptions
{
    public string Auth0ClientId { get; init; } = string.Empty;
    public string Auth0Audience { get; init; } = string.Empty;
}
