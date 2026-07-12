using System.Security.Cryptography;
using System.Text;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Infrastructure.Security;

public sealed class RequestIdentityResolver(QuotaOptions options)
{
    public RequesterIdentity Resolve(HttpContext context)
    {
        var subject = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst("sub")?.Value
            : null;
        var authenticated = !string.IsNullOrWhiteSpace(subject);
        var raw = authenticated
            ? "subject:" + subject
            : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.IdentityHmacKey));
        return new RequesterIdentity(Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), authenticated);
    }
}
