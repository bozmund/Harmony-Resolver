using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Xunit;

namespace Harmony.Resolver.IntegrationTests;

/// <summary>
/// Tests JWT authentication when Auth0 is configured.
/// Uses a self-signed signing key and a mock OIDC configuration manager
/// so the tests don't require a real Auth0 tenant.
/// </summary>
public sealed class JwtValidationTests
{
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048));
    private static readonly string Issuer = "https://harmony-resolver-test.auth0.com/";
    private static readonly string Audience = "https://harmony-resolver";

    [Fact]
    public async Task Request_without_token_succeeds_for_public_endpoints()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Valid_token_with_diagnostics_read_scope_succeeds()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = CreateToken(["diagnostics:read"]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Valid_token_without_permissions_is_accepted_by_api()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = CreateToken([]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = CreateToken(["diagnostics:read"], expires: DateTimeOffset.UtcNow.AddHours(-1));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_token_is_rejected()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not.a.valid.jwt");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_from_wrong_key_is_rejected()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        var wrongKey = new RsaSecurityKey(RSA.Create(2048));
        var token = CreateToken(["diagnostics:read"], signingKey: wrongKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string CreateToken(
        string[] scopes,
        DateTimeOffset? expires = null,
        RsaSecurityKey? signingKey = null,
        string? issuer = null,
        string? audience = null)
    {
        var key = signingKey ?? SigningKey;
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var claims = new List<Claim>
        {
            new("sub", "test|auth0|user123"),
            new("scope", string.Join(" ", scopes))
        };

        var token = new JwtSecurityToken(
            issuer: issuer ?? Issuer,
            audience: audience ?? Audience,
            claims: claims,
            expires: (expires ?? DateTimeOffset.UtcNow.AddHours(1)).UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Auth0:Domain", "harmony-resolver-test.auth0.com");
            builder.UseSetting("Auth0:Audience", Audience);
            builder.UseSetting("Testing:AllowIncompleteProductionConfiguration", "true");

            builder.ConfigureServices(services =>
            {
                // Post-configure the JWT bearer options to use a static
                // configuration manager with our self-signed key instead of
                // reaching out to the real Auth0 JWKS endpoint.
                services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(
                    new PostConfigureJwtBearerOptions(SigningKey, Issuer));
            });
        });
    }

    private sealed class PostConfigureJwtBearerOptions(
        RsaSecurityKey signingKey, string issuer)
        : IPostConfigureOptions<JwtBearerOptions>
    {
        public void PostConfigure(string? name, JwtBearerOptions options)
        {
            options.ConfigurationManager = new StaticConfigurationManager(
                new OpenIdConnectConfiguration { Issuer = issuer }
                    .WithSigningKey(signingKey));

            // Also set the token validation parameters to match our test values
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey
            };
        }
    }
}

public sealed class StaticConfigurationManager(OpenIdConnectConfiguration configuration)
    : IConfigurationManager<OpenIdConnectConfiguration>
{
    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        => Task.FromResult(configuration);

    public void RequestRefresh() { }
}

public static class OpenIdConnectConfigurationExtensions
{
    public static OpenIdConnectConfiguration WithSigningKey(
        this OpenIdConnectConfiguration config, SecurityKey key)
    {
        config.SigningKeys.Add(key);
        return config;
    }
}