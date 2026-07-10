using System.Diagnostics;
using System.Text.Json.Serialization;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Diagnostics;
using Harmony.Resolver.Api.Domain;
using Harmony.Resolver.Api.Infrastructure.Extraction;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ResolverOptions>(builder.Configuration.GetSection("Resolver"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ResolverOptions>>().Value);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITrackCatalog, MemoryTrackCatalog>();
builder.Services.AddSingleton<IMediaExtractor, DeterministicExtractor>();
builder.Services.AddSingleton<ResolverDiagnostics>();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
var authDomain = builder.Configuration["Auth0:Domain"];
var audience = builder.Configuration["Auth0:Audience"];
if (!string.IsNullOrWhiteSpace(authDomain) && !string.IsNullOrWhiteSpace(audience))
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        o.Authority = $"https://{authDomain.TrimEnd('/')}/";
        o.Audience = audience;
        o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true };
    });
}
builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("harmony-resolver-api"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddPrometheusExporter());

var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    await Results.Problem(statusCode: 500, title: "Resolver failure", extensions: new Dictionary<string, object?> { ["traceId"] = traceId }).ExecuteAsync(context);
}));
if (!string.IsNullOrWhiteSpace(authDomain)) app.UseAuthentication();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "swagger";
        options.SwaggerEndpoint("/openapi/v1.json", "Harmony Resolver API v1");
        options.DocumentTitle = "Harmony Resolver API";
    });
}

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapPrometheusScrapingEndpoint("/metrics");
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

    // Deliberately independent of RequestAborted: a disconnected leader must not cancel cache completion.
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

app.MapGet("/internal/diagnostics/snapshot", (ResolverDiagnostics diagnostics) => Results.Ok(diagnostics.Snapshot()));

if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("ENABLE_FAULT_INJECTION"))
    app.MapPost("/internal/faults/{profile}", (string profile) => Results.Ok(new { profile, enabled = true }));

app.Run();
return;

static object Problem(string code, string detail) => new { type = $"https://harmony-resolver/errors/{code}", title = code, detail, status = 400, code };
