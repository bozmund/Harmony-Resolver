using System.Net;
using System.Net.Http.Json;
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
    public async Task Metadata_miss_answers_with_a_status_rather_than_404()
    {
        var response = await _client.GetAsync("/v1/tracks/dQw4w9WgXcQ/metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"missing\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metadata_rejects_an_invalid_id()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/v1/tracks/http:%2F%2Fx/metadata")).StatusCode);
    }

    [Fact]
    public async Task Metadata_batch_answers_for_every_requested_id_in_order()
    {
        var response = await _client.PostAsJsonAsync("/v1/tracks/metadata:batch",
            new { videoIds = new[] { "dQw4w9WgXcQ", "9bZkp7q19f0" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(body.IndexOf("dQw4w9WgXcQ", StringComparison.Ordinal)
            < body.IndexOf("9bZkp7q19f0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Metadata_batch_enforces_its_size_limits(int count)
    {
        var ids = Enumerable.Range(0, count).Select(i => i.ToString("D11")).ToArray();

        var response = await _client.PostAsJsonAsync("/v1/tracks/metadata:batch", new { videoIds = ids });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_batch_rejects_duplicate_ids()
    {
        var response = await _client.PostAsJsonAsync("/v1/tracks/metadata:batch",
            new { videoIds = new[] { "dQw4w9WgXcQ", "dQw4w9WgXcQ" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
