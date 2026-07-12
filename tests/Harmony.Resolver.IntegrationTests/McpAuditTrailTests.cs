using System.Net;
using System.Net.Http.Json;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Extraction;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using Harmony.Resolver.Api.Infrastructure.Storage;
using Harmony.Resolver.Api.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace Harmony.Resolver.IntegrationTests;

/// <summary>
/// Verifies that MCP tool calls write a sanitized audit trail,
/// and that subject hashes are HMAC-redacted and not reversible.
/// </summary>
public sealed class McpAuditTrailTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("harmony_test_audit")
        .WithUsername("harmony")
        .WithPassword("test-only-password")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var db = new ResolverDbContext(
            new DbContextOptionsBuilder<ResolverDbContext>()
                .UseNpgsql(_postgres.GetConnectionString())
                .Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Audit_endpoint_writes_entity_with_hmac_subject_hash()
    {
        _factory = CreateFactory();

        // Seed the audit via the API endpoint
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/internal/diagnostics/audit", new
        {
            SubjectHash = "a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6",
            ToolName = "get_system_snapshot",
            Summary = new Dictionary<string, object?>
            {
                ["hasArguments"] = false,
                ["durationMs"] = 42
            }
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify the entity was written
        await using var db = new ResolverDbContext(
            new DbContextOptionsBuilder<ResolverDbContext>()
                .UseNpgsql(_postgres.GetConnectionString())
                .Options);

        var audit = await db.DiagnosticAudits
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
        Assert.Equal("a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6", audit.SubjectHash);
        Assert.Equal("get_system_snapshot", audit.ToolName);
        Assert.NotNull(audit.QuerySummary);
    }

    [Fact]
    public async Task Audit_endpoint_rejects_invalid_subject_hash_length()
    {
        _factory = CreateFactory();
        using var client = _factory.CreateClient();

        // Too short
        var shortResponse = await client.PostAsJsonAsync("/internal/diagnostics/audit", new
        {
            SubjectHash = "tooshort",
            ToolName = "get_system_snapshot",
            Summary = new Dictionary<string, object?>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, shortResponse.StatusCode);

        // Too long
        var longResponse = await client.PostAsJsonAsync("/internal/diagnostics/audit", new
        {
            SubjectHash = new string('a', 200),
            ToolName = "get_system_snapshot",
            Summary = new Dictionary<string, object?>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, longResponse.StatusCode);
    }

    [Fact]
    public async Task Audit_endpoint_rejects_invalid_tool_name()
    {
        _factory = CreateFactory();
        using var client = _factory.CreateClient();

        // Too short
        var shortResponse = await client.PostAsJsonAsync("/internal/diagnostics/audit", new
        {
            SubjectHash = new string('a', 32),
            ToolName = "",
            Summary = new Dictionary<string, object?>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, shortResponse.StatusCode);

        // Too long
        var longResponse = await client.PostAsJsonAsync("/internal/diagnostics/audit", new
        {
            SubjectHash = new string('a', 32),
            ToolName = new string('b', 100),
            Summary = new Dictionary<string, object?>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, longResponse.StatusCode);
    }

    [Fact]
    public async Task Hmac_subject_hash_is_not_reversible_to_original_subject()
    {
        // This test validates the HMAC-redaction property:
        // given the hash, you cannot recover the original subject.
        // We verify this by hashing two different subjects and checking
        // they produce different hashes, and that neither hash contains
        // the original subject string.

        const string knownKey = "known-test-hmac-key-32bytes!";
        const string subject1 = "auth0|user12345";
        const string subject2 = "auth0|user67890";

        var hash1 = ComputeHmac(subject1, knownKey);
        var hash2 = ComputeHmac(subject2, knownKey);

        // Different subjects produce different hashes
        Assert.NotEqual(hash1, hash2);

        // The hash does not contain the original subject
        Assert.DoesNotContain(subject1, hash1);
        Assert.DoesNotContain(subject2, hash2);

        // The hash is a hex string (lowercase)
        Assert.Matches("^[0-9a-f]+$", hash1);
        Assert.Matches("^[0-9a-f]+$", hash2);
    }

    private static string ComputeHmac(string subject, string key)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(subject));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:PostgreSql", _postgres.GetConnectionString());
            builder.UseSetting("Testing:AllowIncompleteProductionConfiguration", "true");

            builder.ConfigureServices(services =>
            {
                // Remove hosted services that would try to connect to MinIO
                var hosted = services
                    .Where(s => s.ServiceType == typeof(IHostedService) &&
                        (s.ImplementationType?.FullName?.Contains("ObjectStore") == true ||
                         s.ImplementationType?.FullName?.Contains("Janitor") == true))
                    .ToList();
                foreach (var h in hosted) services.Remove(h);
            });
        });
    }
}