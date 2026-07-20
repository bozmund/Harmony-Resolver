using System.Diagnostics;
using System.Text.Json.Serialization;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Diagnostics;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Endpoints;
using Harmony.Resolver.Api.Infrastructure.Extraction;
using Harmony.Resolver.Api.Infrastructure.Messaging;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using Harmony.Resolver.Api.Infrastructure.Quotas;
using Harmony.Resolver.Api.Infrastructure.Security;
using Harmony.Resolver.Api.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Minio;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var runMigrations = args.Contains("--migrate", StringComparer.Ordinal)
    || builder.Configuration.GetValue<bool>("RUN_MIGRATIONS");
builder.Services.Configure<ResolverOptions>(builder.Configuration.GetSection("Resolver"));
builder.Services.Configure<ObjectStorageOptions>(builder.Configuration.GetSection("ObjectStorage"));
builder.Services.Configure<QuotaOptions>(builder.Configuration.GetSection("Quotas"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ResolverOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<QuotaOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value);
builder.Services.AddSingleton<RequestIdentityResolver>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITrackCatalog, MemoryTrackCatalog>();
var resolverConfiguration = builder.Configuration.GetSection("Resolver").Get<ResolverOptions>() ?? new ResolverOptions();
if (resolverConfiguration.ExtractionMode == ExtractionMode.Delegated)
{
    // The server never contacts YouTube in Delegated mode: cache misses are queued for the
    // downloader fleet, which uploads raw audio for the server to normalize. FfmpegNormalizer is
    // still needed server-side to canonicalize those uploads to Ogg Opus.
    builder.Services.AddSingleton<FfmpegNormalizer>();
    builder.Services.AddSingleton<AudioFingerprintService>();
    builder.Services.AddSingleton<IMediaExtractor, DelegatedExtractionPlaceholder>();
}
else if (resolverConfiguration.UseFakeExtractor)
{
    builder.Services.AddSingleton<IMediaExtractor, DeterministicExtractor>();
}
else
{
    builder.Services.AddSingleton<FfmpegNormalizer>();
    builder.Services.AddSingleton<AudioFingerprintService>();
    builder.Services.AddSingleton<YoutubeExplode.YoutubeClient>();
    builder.Services.AddSingleton<IExtractorAdapter, YtDlpExtractorAdapter>();
    builder.Services.AddSingleton<IExtractorAdapter, YoutubeExplodeExtractorAdapter>();
    builder.Services.AddSingleton<IMediaExtractor, OrderedMediaExtractor>();
}
var rabbitConfiguration = builder.Configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new RabbitMqOptions();
if (resolverConfiguration.ExtractionMode == ExtractionMode.Delegated && rabbitConfiguration.Enabled)
    builder.Services.AddSingleton<IJobNotifier, RabbitMqJobNotifier>();
else
    builder.Services.AddSingleton<IJobNotifier, NoopJobNotifier>();
builder.Services.AddSingleton<ResolverDiagnostics>();
builder.Services.AddSingleton<ReadinessProbe>();
builder.Services.AddSingleton<FaultInjectionState>();
builder.Services.AddSingleton<ResolverMetrics>();
var postgresConnection = builder.Configuration.GetConnectionString("PostgreSql");
if (!string.IsNullOrWhiteSpace(postgresConnection))
{
    builder.Services.AddPooledDbContextFactory<ResolverDbContext>(options => options.UseNpgsql(postgresConnection));
    builder.Services.AddSingleton<ITrackRepository, PostgresTrackRepository>();
    builder.Services.AddSingleton<DiagnosticAuditWriter>();
    builder.Services.AddSingleton<PlayHistoryWriter>();
}
var storage = builder.Configuration.GetSection("ObjectStorage").Get<ObjectStorageOptions>();
if (storage is not null && !string.IsNullOrWhiteSpace(storage.Endpoint))
{
    var endpoint = new Uri(storage.Endpoint);
    builder.Services.AddSingleton<IMinioClient>(_ => new MinioClient()
        .WithEndpoint(endpoint.Host, endpoint.Port)
        .WithCredentials(storage.AccessKey, storage.SecretKey)
        .WithSSL(endpoint.Scheme == Uri.UriSchemeHttps)
        .WithHttpClient(
            new HttpClient(
                new MinioRangeHeadCompatibilityHandler(new HttpClientHandler())),
            disposeHttpClient: true)
        .Build());
    builder.Services.AddSingleton<IObjectStore, MinioObjectStore>();
    builder.Services.AddHostedService<ObjectStoreInitializer>();
}
if (resolverConfiguration.ExtractionMode == ExtractionMode.Delegated && !string.IsNullOrWhiteSpace(postgresConnection))
    builder.Services.AddHostedService<StuckJobReaper>();
if (resolverConfiguration.ExtractionMode == ExtractionMode.Delegated && rabbitConfiguration.Enabled && !string.IsNullOrWhiteSpace(postgresConnection))
    builder.Services.AddHostedService<JobRepublisher>();
var valkeyConnection = builder.Configuration.GetConnectionString("Valkey");
if (!string.IsNullOrWhiteSpace(valkeyConnection))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(valkeyConnection));
    builder.Services.AddSingleton<IQuotaService, ValkeyQuotaService>();
}
else
{
    builder.Services.AddSingleton<IQuotaService, InMemoryQuotaService>();
}
if (!runMigrations && !builder.Environment.IsDevelopment() && !builder.Configuration.GetValue<bool>("Testing:AllowIncompleteProductionConfiguration"))
{
    var missing = new List<string>();
    if (string.IsNullOrWhiteSpace(postgresConnection)) missing.Add("ConnectionStrings:PostgreSql");
    if (string.IsNullOrWhiteSpace(valkeyConnection)) missing.Add("ConnectionStrings:Valkey");
    if (storage is null || string.IsNullOrWhiteSpace(storage.Endpoint) || string.IsNullOrWhiteSpace(storage.AccessKey) || string.IsNullOrWhiteSpace(storage.SecretKey))
        missing.Add("ObjectStorage credentials");
    var quotaConfiguration = builder.Configuration.GetSection("Quotas").Get<QuotaOptions>();
    if (quotaConfiguration is null || quotaConfiguration.IdentityHmacKey.StartsWith("development-only", StringComparison.Ordinal))
        missing.Add("Quotas:IdentityHmacKey");
    if (missing.Count > 0) throw new InvalidOperationException("Missing production configuration: " + string.Join(", ", missing));
}
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
var authDomain = builder.Configuration["Auth0:Domain"];
var audience = builder.Configuration["Auth0:Audience"];
var authEnabled = !string.IsNullOrWhiteSpace(authDomain) && !string.IsNullOrWhiteSpace(audience);
if (authEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        o.Authority = $"https://{authDomain!.TrimEnd('/')}/";
        o.Audience = audience;
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true };
    });
    // Downloader fleet authorization. Mirrors the MCP service's diagnostics:read policy: a valid
    // Auth0 token whose permissions/scope claim contains tracks:ingest. Applied only to /v1/worker/*.
    builder.Services.AddAuthorization(options => options.AddPolicy(WorkerIngestionEndpoints.IngestPolicy, policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            context.User.FindAll("permissions").Any(claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tracks:ingest", StringComparer.Ordinal)) ||
            context.User.FindAll("scope").Any(claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tracks:ingest", StringComparer.Ordinal)))));
    builder.Services.AddAuthorization(options => options.AddPolicy(BackupUploadEndpoints.BackupPolicy, policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            context.User.FindAll("permissions").Any(claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tracks:backup", StringComparer.Ordinal)) ||
            context.User.FindAll("scope").Any(claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tracks:backup", StringComparer.Ordinal)))));
}
// Delegated extraction requires an authenticated fleet; refuse to expose an unauthenticated ingest
// path in a real deployment. Development may run it open for local end-to-end testing.
if (resolverConfiguration.ExtractionMode == ExtractionMode.Delegated && !authEnabled && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Resolver:ExtractionMode=Delegated requires Auth0:Domain and Auth0:Audience outside Development.");
builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("harmony-resolver-api"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        var otlp = builder.Configuration["OTLP:Endpoint"];
        if (!string.IsNullOrWhiteSpace(otlp))
            t.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
    })
    .WithMetrics(m => m.AddMeter(ResolverMetrics.MeterName).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddPrometheusExporter());

var app = builder.Build();
if (runMigrations)
{
    await using var scope = app.Services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ResolverDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    return;
}
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    await Results.Problem(statusCode: 500, title: "Resolver failure", extensions: new Dictionary<string, object?> { ["traceId"] = traceId }).ExecuteAsync(context);
}));
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedHost
        | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);
if (authEnabled) app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Headers.Authorization.Count > 0)
    {
        if (string.IsNullOrWhiteSpace(authDomain))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var authentication = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!authentication.Succeeded)
        {
            await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid bearer token",
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_token" }).ExecuteAsync(context);
            return;
        }
    }
    await next(context);
});
if (authEnabled) app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapGet("/", () => Results.Redirect("/swagger/", permanent: false));
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "swagger";
        options.SwaggerEndpoint("/openapi/v1.json", "Harmony Resolver API v1");
        options.DocumentTitle = "Harmony Resolver API";
    });
}

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (ReadinessProbe probe, CancellationToken cancellationToken) =>
{
    var result = await probe.CheckAsync(cancellationToken);
    return Results.Json(new { status = result.Ready ? "ready" : "not_ready", dependencies = result.Dependencies },
        statusCode: result.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});
app.MapPrometheusScrapingEndpoint("/metrics");
if (!string.IsNullOrWhiteSpace(postgresConnection) && storage is not null && !string.IsNullOrWhiteSpace(storage.Endpoint))
{
    app.MapDistributedResolverEndpoints();
    if (resolverConfiguration.ExtractionMode == ExtractionMode.Delegated)
    {
        app.MapWorkerIngestionEndpoints(authEnabled);
        app.MapBackupUploadEndpoints(authEnabled);
    }
}
else
{
    app.MapGet("/v1/tracks/{videoId}", (string videoId, ITrackCatalog catalog) =>
        VideoIds.IsValid(videoId) ? Results.Ok(catalog.Get(videoId)) : Results.BadRequest(Problem("invalid_video_id", "A YouTube video ID must contain exactly 11 safe characters.")));

    app.MapGet("/v1/tracks/{videoId}/audio", async (string videoId, HttpContext http, ITrackCatalog catalog, IMediaExtractor extractor, ResolverOptions options) =>
    {
        if (!VideoIds.IsValid(videoId)) return Results.BadRequest(Problem("invalid_video_id", "A YouTube video ID must contain exactly 11 safe characters."));
        var track = catalog.Get(videoId);
        if (track.Status == TrackStatus.Ready && catalog.Read(videoId) is { } cached)
            return Results.File(cached, "audio/ogg", enableRangeProcessing: true, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(track.ETag!));
        if (track.Status == TrackStatus.Ingesting)
        {
            http.Response.Headers.RetryAfter = "2";
            return Results.Json(Problem("ingestion_in_progress", "Another replica is ingesting this track."), statusCode: 202, contentType: "application/problem+json");
        }
        if (!catalog.TryBegin(videoId))
        {
            http.Response.Headers.RetryAfter = "2";
            return Results.StatusCode(202);
        }

        using var timeout = new CancellationTokenSource(options.ExtractionTimeout);
        try
        {
            var audio = await extractor.ExtractAsync(videoId, timeout.Token);
            if (audio.LongLength > options.MaxObjectMiB * 1024L * 1024L) throw new InvalidDataException("object limit");
            catalog.Ready(videoId, audio);
            return Results.File(audio, "audio/ogg");
        }
        catch (OperationCanceledException)
        {
            catalog.Failed(videoId, "extraction_timeout");
            return Results.Problem(statusCode: 502, title: "Extraction failed", extensions: new Dictionary<string, object?> { ["code"] = "extraction_timeout" });
        }
        catch
        {
            catalog.Failed(videoId, "extraction_failed");
            return Results.Problem(statusCode: 502, title: "Extraction failed", extensions: new Dictionary<string, object?> { ["code"] = "extraction_failed" });
        }
    });
}

app.MapGet("/internal/diagnostics/snapshot", async (ResolverDiagnostics diagnostics, CancellationToken cancellationToken) =>
    Results.Ok(await diagnostics.SnapshotAsync(cancellationToken)));
if (!string.IsNullOrWhiteSpace(postgresConnection))
{
    app.MapGet("/internal/diagnostics/tracks/{videoId}", async (string videoId, ITrackRepository tracks, CancellationToken cancellationToken) =>
        VideoIds.IsValid(videoId) ? Results.Ok(await tracks.GetAsync(videoId, cancellationToken)) : Results.BadRequest());
    app.MapGet("/internal/diagnostics/failures", async (int? limit, ITrackRepository tracks, TimeProvider clock, CancellationToken cancellationToken) =>
        Results.Ok(await tracks.ListFailuresAsync(clock.GetUtcNow() - TimeSpan.FromHours(24), Math.Clamp(limit ?? 50, 1, 200), cancellationToken)));
    app.MapGet("/internal/diagnostics/recent-plays", async (int? limit, PlayHistoryWriter playHistory, CancellationToken cancellationToken) =>
        Results.Ok(await playHistory.GetRecentAsync(Math.Clamp(limit ?? 5, 1, 50), cancellationToken)));
    app.MapPost("/internal/diagnostics/audit", async (DiagnosticAuditRequest request, DiagnosticAuditWriter audit, CancellationToken cancellationToken) =>
    {
        if (request.SubjectHash.Length is < 16 or > 128 || request.ToolName.Length is < 1 or > 64)
            return Results.BadRequest();
        using var summary = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(request.Summary));
        await audit.WriteAsync(request.SubjectHash, request.ToolName, summary, cancellationToken);
        return Results.NoContent();
    });
}

if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("ENABLE_FAULT_INJECTION"))
{
    var allowedProfiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "extractor-timeout", "malformed-metadata", "partial-ffmpeg-output", "client-disconnect",
        "minio-failure", "postgresql-lease-loss", "valkey-outage", "replica-crash", "slow-downstream"
    };
    app.MapPut("/internal/faults/{profile}", (string profile, FaultInjectionState faults) =>
    {
        if (!allowedProfiles.Contains(profile)) return Results.BadRequest(new { code = "unknown_fault_profile" });
        faults.Set(profile);
        return Results.Ok(new { profile, enabled = true });
    });
    app.MapDelete("/internal/faults", (FaultInjectionState faults) => { faults.Set(null); return Results.NoContent(); });
}

app.Run();
return;

static object Problem(string code, string detail) => new { type = $"https://harmony-resolver/errors/{code}", title = code, detail, status = 400, code };

public sealed record DiagnosticAuditRequest(string SubjectHash, string ToolName, Dictionary<string, object?> Summary);
