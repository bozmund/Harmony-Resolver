using System.Net;
using Harmony.Resolver.Api.Diagnostics;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.Resolver.IntegrationTests;

/// <summary>
/// Tests the fault injection profiles available in Development mode.
/// Each profile is enabled via PUT /internal/faults/{profile},
/// triggers a predictable failure, then is disabled via DELETE /internal/faults.
/// </summary>
public sealed class FaultInjectionTests
{
    private static readonly string[] Profiles =
    [
        "extractor-timeout",
        "malformed-metadata",
        "partial-ffmpeg-output",
        "client-disconnect",
        "minio-failure",
        "postgresql-lease-loss",
        "valkey-outage",
        "replica-crash",
        "slow-downstream"
    ];

    [Fact]
    public async Task All_fault_profiles_are_recognized()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        foreach (var profile in Profiles)
        {
            var response = await client.PutAsync($"/internal/faults/{profile}", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Unknown profile is rejected
        var badResponse = await client.PutAsync("/internal/faults/unknown-fault", null);
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);
    }

    [Fact]
    public async Task Fault_injection_can_be_disabled()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        var enableResponse = await client.PutAsync("/internal/faults/extractor-timeout", null);
        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);

        var disableResponse = await client.DeleteAsync("/internal/faults");
        Assert.Equal(HttpStatusCode.NoContent, disableResponse.StatusCode);
    }

    [Fact]
    public async Task Extractor_timeout_profile_causes_502()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PutAsync("/internal/faults/extractor-timeout", null);

        var response = await client.GetAsync("/v1/tracks/TmoutXXXxxx/audio");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_metadata_profile_causes_502()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PutAsync("/internal/faults/malformed-metadata", null);

        var response = await client.GetAsync("/v1/tracks/MlFrmMetdaA/audio");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Faults_do_not_affect_other_requests_after_disable()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PutAsync("/internal/faults/malformed-metadata", null);
        var failedResponse = await client.GetAsync("/v1/tracks/FaultTestA1/audio");
        Assert.Equal(HttpStatusCode.BadGateway, failedResponse.StatusCode);

        // Disable
        await client.DeleteAsync("/internal/faults");

        // Normal request with different video ID succeeds
        var okResponse = await client.GetAsync("/v1/tracks/NrmlRqstDs1/audio");
        Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);
    }

    [Fact]
    public async Task Normal_request_works_when_no_fault_is_active()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/tracks/NrmlWrkTst1/audio");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Extract_with_fault_disabled_between_requests()
    {
        var factory = CreateFactory();
        using var client = factory.CreateClient();

        // First request succeeds
        var first = await client.GetAsync("/v1/tracks/FirstTst012/audio");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        await client.PutAsync("/internal/faults/malformed-metadata", null);

        var second = await client.GetAsync("/v1/tracks/SecondTst01/audio");
        Assert.Equal(HttpStatusCode.BadGateway, second.StatusCode);

        await client.DeleteAsync("/internal/faults");

        var third = await client.GetAsync("/v1/tracks/ThirdTst012/audio");
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Resolver:UseFakeExtractor", "true");
            builder.UseSetting("Resolver:ExtractionTimeout", "00:00:02");
            builder.UseSetting("ENABLE_FAULT_INJECTION", "true");
            builder.UseSetting("Testing:AllowIncompleteProductionConfiguration", "true");
        });
    }
}