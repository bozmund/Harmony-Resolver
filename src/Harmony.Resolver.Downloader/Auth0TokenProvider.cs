using System.Net.Http.Json;

namespace Harmony.Resolver.Downloader;

/// <summary>
/// Obtains and caches an Auth0 machine-to-machine access token (client-credentials grant) carrying the
/// <c>tracks:ingest</c> scope. Mirrors the McpBridge token provider. Returns <see langword="null"/> when
/// Auth0 is not configured, so the agent can run unauthenticated against a Development resolver.
/// </summary>
public sealed class Auth0TokenProvider(HttpClient http, DownloaderOptions options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _refreshAt;

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        if (!options.Auth0Enabled) return null;
        if (_token is not null && DateTimeOffset.UtcNow < _refreshAt) return _token;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _refreshAt) return _token;
            var response = await http.PostAsJsonAsync($"https://{options.Auth0Domain!.TrimEnd('/')}/oauth/token", new
            {
                client_id = options.Auth0ClientId ?? throw new InvalidOperationException("AUTH0_CLIENT_ID is required when AUTH0_DOMAIN is set."),
                client_secret = options.Auth0ClientSecret ?? throw new InvalidOperationException("AUTH0_CLIENT_SECRET is required when AUTH0_DOMAIN is set."),
                audience = options.Auth0Audience,
                grant_type = "client_credentials"
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Auth0 returned no token.");
            _token = token.access_token;
            _refreshAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(30, token.expires_in - 60));
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record TokenResponse(string access_token, int expires_in);
}
