using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;
    public ApiTests(WebApplicationFactory<Program> factory) => client = factory.CreateClient();
    [Fact] public async Task Health_is_live() => Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    [Fact] public async Task Unsafe_id_is_rejected() => Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/tracks/http:%2F%2Fx/audio")).StatusCode);
    [Fact]
    public async Task Miss_becomes_range_enabled_hit()
    {
        var id = "dQw4w9WgXcQ";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1/tracks/{id}/audio")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/tracks/{id}/audio");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);
        Assert.Equal(HttpStatusCode.PartialContent, (await client.SendAsync(request)).StatusCode);
    }
}
