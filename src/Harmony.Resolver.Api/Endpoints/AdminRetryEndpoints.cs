using System.Text.Json;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Diagnostics;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Security;

namespace Harmony.Resolver.Api.Endpoints;

/// <summary>
/// The console is deliberately served by the Resolver itself so its browser
/// calls stay same-origin. Production ingress blocks this path; it is available
/// only from the loopback-bound replica reached through SSH port forwarding.
/// </summary>
public static class AdminRetryEndpoints
{
    public const string Policy = "resolver:admin";
    private const int MaxPageSize = 100;

    public static void MapAdminRetryEndpoints(
        this IEndpointRouteBuilder endpoints,
        string auth0Domain,
        AdminConsoleOptions options)
    {
        endpoints.MapGet("/admin/retries", () =>
            Results.Content(AdminConsolePage.Html, "text/html; charset=utf-8"));
        endpoints.MapGet("/admin/config", () =>
        {
            if (string.IsNullOrWhiteSpace(auth0Domain)
                || string.IsNullOrWhiteSpace(options.Auth0ClientId)
                || string.IsNullOrWhiteSpace(options.Auth0Audience))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Admin console is not configured",
                    extensions: new Dictionary<string, object?> { ["code"] = "admin_console_not_configured" });
            }
            return Results.Ok(new
            {
                domain = auth0Domain,
                clientId = options.Auth0ClientId,
                audience = options.Auth0Audience,
            });
        });

        var api = endpoints.MapGroup("/admin/api").RequireAuthorization(Policy);
        api.MapGet("/failed", ListFailedAsync);
        api.MapPost("/retries", RetryAsync);
    }

    private static async Task<IResult> ListFailedAsync(
        int? offset,
        int? limit,
        ITrackRepository tracks,
        CancellationToken cancellationToken)
    {
        var pageOffset = Math.Max(0, offset ?? 0);
        var pageLimit = Math.Clamp(limit ?? 50, 1, MaxPageSize);
        return Results.Ok(await tracks.ListFailedForAdminAsync(
            pageOffset, pageLimit, cancellationToken));
    }

    private static async Task<IResult> RetryAsync(
        AdminRetryRequest? request,
        HttpContext context,
        ITrackRepository tracks,
        IJobNotifier notifier,
        RequestIdentityResolver identities,
        DiagnosticAuditWriter audit,
        CancellationToken cancellationToken)
    {
        var ids = request?.VideoIds;
        if (ids is null || ids.Count is < 1 or > MaxPageSize
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
            || ids.Any(id => !VideoIds.IsValid(id)))
        {
            return Results.BadRequest(new { code = "invalid_retry_request" });
        }

        var results = new List<AdminRetryResult>(ids.Count);
        foreach (var videoId in ids)
        {
            var result = await tracks.ForceRetryAsync(videoId, cancellationToken);
            results.Add(result);
            if (result.Outcome == AdminRetryOutcome.Queued)
                await notifier.NotifyAsync(videoId, cancellationToken);
        }

        using var summary = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            requested = ids.Count,
            queued = results.Count(x => x.Outcome == AdminRetryOutcome.Queued),
            videoIds = ids,
        }));
        await audit.WriteAsync(
            identities.Resolve(context).Key,
            "admin_retry_tracks",
            summary,
            cancellationToken);
        return Results.Ok(new { results });
    }

    private sealed record AdminRetryRequest(IReadOnlyList<string>? VideoIds);
}

/// <summary>
/// The admin console markup, read once from the embedded <c>AdminConsolePage.html</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Auth0 SPA client is pinned to a major/minor release. No credentials are embedded here; the
/// browser receives only the public client id.
/// </para>
/// <para>
/// An embedded resource rather than a file on disk: it travels inside the assembly, so it cannot go
/// missing from a publish or be unreadable to the non-root container user, and there is no
/// static-file middleware to add. Keeping the page in a real <c>.html</c> file is what gives its
/// markup, CSS and ~55 lines of script syntax highlighting, formatting and validation — none of
/// which a C# string literal gets.
/// </para>
/// </remarks>
internal static class AdminConsolePage
{
    private const string ResourceName = "AdminConsolePage.html";

    internal static string Html { get; } = Load();

    private static string Load()
    {
        using var stream = typeof(AdminConsolePage).Assembly.GetManifestResourceStream(ResourceName)
            // A mis-wired csproj entry would otherwise surface as a blank console page. Name the
            // resource so the cause is obvious rather than having to guess at an empty response.
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' is missing. It must be declared as an " +
                "<EmbeddedResource> with a matching LogicalName in Harmony.Resolver.Api.csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
