using Harmony.Resolver.Api.Endpoints;
using Xunit;

namespace Harmony.Resolver.UnitTests;

/// <summary>
/// The console markup moved out of a C# string literal into an embedded <c>.html</c> file. That
/// swapped a compile-time constant for a runtime resource lookup, so a mis-wired csproj entry now
/// builds cleanly and only fails when someone opens the page. It also means the markup and the
/// endpoints that serve it can drift apart silently — nothing else in the repo referenced this page.
/// </summary>
public sealed class AdminConsolePageTests
{
    [Fact]
    public void Markup_is_embedded_in_the_assembly_and_loads()
    {
        Assert.False(string.IsNullOrWhiteSpace(AdminConsolePage.Html));
        Assert.StartsWith("<!doctype html>", AdminConsolePage.Html, StringComparison.Ordinal);
        Assert.EndsWith("</html>", AdminConsolePage.Html.TrimEnd(), StringComparison.Ordinal);
    }

    [Theory]
    // The script wires itself to these by id; renaming one in the now-separate file would leave the
    // console rendering fine and doing nothing.
    [InlineData("id=\"login\"")]
    [InlineData("id=\"logout\"")]
    [InlineData("id=\"retry\"")]
    [InlineData("id=\"refresh\"")]
    [InlineData("id=\"more\"")]
    [InlineData("id=\"status\"")]
    public void Interactive_elements_the_script_binds_to_are_present(string marker)
    {
        Assert.Contains(marker, AdminConsolePage.Html, StringComparison.Ordinal);
    }

    [Theory]
    // The coupling between this page and the API. These paths are the routes registered in
    // MapAdminRetryEndpoints; a typo here is a 404 the page reports as a generic failure.
    [InlineData("/admin/config")]
    [InlineData("/admin/api/failed")]
    [InlineData("/admin/api/retries")]
    [InlineData("/admin/retries")]
    public void Api_paths_the_console_calls_are_intact(string route)
    {
        Assert.Contains(route, AdminConsolePage.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Auth0_spa_client_stays_pinned_to_a_release()
    {
        // Floating to "latest" would let a third-party script change under an admin console that
        // holds a resolver:admin token.
        Assert.Contains(
            "https://cdn.auth0.com/js/auth0-spa-js/2.1/auth0-spa-js.production.js",
            AdminConsolePage.Html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void No_credentials_are_baked_into_the_page()
    {
        // The client id and audience arrive at runtime from /admin/config; only the public client id
        // ever reaches the browser, and nothing secret belongs in markup served over plain HTTP
        // through an SSH forward.
        Assert.DoesNotContain("client_secret", AdminConsolePage.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientSecret", AdminConsolePage.Html, StringComparison.OrdinalIgnoreCase);
    }
}
