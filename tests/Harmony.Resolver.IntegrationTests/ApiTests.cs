using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Harmony.Resolver.IntegrationTests;

public sealed class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();
    [Fact] public async Task Health_is_live() => Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live")).StatusCode);
    [Fact] public async Task Unsafe_id_is_rejected() => Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/v1/tracks/http:%2F%2Fx/audio")).StatusCode);

    [Fact]
    public async Task Development_exposes_swagger_and_openapi()
    {
        var root = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("Harmony Resolver API", await root.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Production_hides_swagger_and_openapi()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("Testing:AllowIncompleteProductionConfiguration", "true");
            });
        using var productionClient = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await productionClient.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await productionClient.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Supplied_token_is_rejected_when_auth0_is_not_configured()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Miss_becomes_range_enabled_hit()
    {
        const string id = "dQw4w9WgXcQ";
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/v1/tracks/{id}/audio")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/tracks/{id}/audio");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);
        Assert.Equal(HttpStatusCode.PartialContent, (await _client.SendAsync(request)).StatusCode);
    }
}
