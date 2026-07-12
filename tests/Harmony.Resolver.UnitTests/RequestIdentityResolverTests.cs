using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class RequestIdentityResolverTests
{
    private static readonly QuotaOptions Options = new()
    {
        IdentityHmacKey = "test-hmac-key-32-bytes-long!"
    };

    private readonly RequestIdentityResolver _resolver = new(Options);

    [Fact]
    public void Authenticated_subject_produces_deterministic_hmac()
    {
        var http = CreateHttpContext(isAuthenticated: true, sub: "auth0|user123");

        var identity = _resolver.Resolve(http);

        Assert.True(identity.IsAuthenticated);
        Assert.NotEmpty(identity.Key);
        // The same subject should always produce the same key
        var identity2 = _resolver.Resolve(http);
        Assert.Equal(identity.Key, identity2.Key);
    }

    [Fact]
    public void Anonymous_ip_produces_different_key_than_subject()
    {
        var authenticated = CreateHttpContext(isAuthenticated: true, sub: "auth0|user123");
        var anonymous = CreateHttpContext(isAuthenticated: false, ip: "192.168.1.1");

        var authIdentity = _resolver.Resolve(authenticated);
        var anonIdentity = _resolver.Resolve(anonymous);

        Assert.True(authIdentity.IsAuthenticated);
        Assert.False(anonIdentity.IsAuthenticated);
        Assert.NotEqual(authIdentity.Key, anonIdentity.Key);
    }

    [Fact]
    public void Different_ips_produce_different_keys()
    {
        var ip1 = CreateHttpContext(isAuthenticated: false, ip: "192.168.1.1");
        var ip2 = CreateHttpContext(isAuthenticated: false, ip: "203.0.113.42");

        var key1 = _resolver.Resolve(ip1);
        var key2 = _resolver.Resolve(ip2);

        Assert.NotEqual(key1.Key, key2.Key);
    }

    [Fact]
    public void Same_ip_produces_same_key()
    {
        var ip1 = CreateHttpContext(isAuthenticated: false, ip: "10.0.0.1");
        var ip2 = CreateHttpContext(isAuthenticated: false, ip: "10.0.0.1");

        Assert.Equal(_resolver.Resolve(ip1).Key, _resolver.Resolve(ip2).Key);
    }

    [Fact]
    public void Missing_ip_uses_unknown_fallback()
    {
        var http = CreateHttpContext(isAuthenticated: false, ip: null);

        var identity = _resolver.Resolve(http);

        Assert.False(identity.IsAuthenticated);
        Assert.NotEmpty(identity.Key);
    }

    [Fact]
    public void Key_is_hex_lowercase()
    {
        var http = CreateHttpContext(isAuthenticated: true, sub: "auth0|user123");

        var identity = _resolver.Resolve(http);

        Assert.Matches("^[0-9a-f]+$", identity.Key);
    }

    [Fact]
    public void Hmac_key_change_produces_different_key()
    {
        var http = CreateHttpContext(isAuthenticated: true, sub: "auth0|user123");

        var resolver1 = new RequestIdentityResolver(new QuotaOptions
        {
            IdentityHmacKey = "first-hmac-key-DONT-USE!"
        });
        var resolver2 = new RequestIdentityResolver(new QuotaOptions
        {
            IdentityHmacKey = "second-hmac-key-DONT-USE!"
        });

        var key1 = resolver1.Resolve(http).Key;
        var key2 = resolver2.Resolve(http).Key;

        Assert.NotEqual(key1, key2);
    }

    private static DefaultHttpContext CreateHttpContext(
        bool isAuthenticated, string? sub = null, string? ip = null)
    {
        var http = new DefaultHttpContext();
        if (ip is not null)
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);

        if (isAuthenticated && sub is not null)
        {
            http.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("sub", sub)],
                    "test"));
        }

        return http;
    }
}