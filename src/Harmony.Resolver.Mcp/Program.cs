using Harmony.Resolver.Mcp.Tools;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("resolver", c => c.BaseAddress = new Uri(builder.Configuration["Resolver:BaseUrl"] ?? "http://localhost:8080"));
builder.Services.AddHttpClient("loki", c => c.BaseAddress = new Uri(builder.Configuration["Loki:BaseUrl"] ?? "http://localhost:3100"));
builder.Services.AddMcpServer().WithHttpTransport().WithTools<DiagnosticTools>();
var authDomain = builder.Configuration["Auth0:Domain"];
var audience = builder.Configuration["Auth0:Audience"];
var authEnabled = !string.IsNullOrWhiteSpace(authDomain) && !string.IsNullOrWhiteSpace(audience);
if (authEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = $"https://{authDomain!.TrimEnd('/')}/";
        options.Audience = audience;
    });
    builder.Services.AddAuthorization(options => options.AddPolicy("diagnostics:read", policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            context.User.FindAll("permissions").Any(claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("diagnostics:read", StringComparer.Ordinal)) ||
            context.User.FindAll("scope").Any(claim =>
                claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("diagnostics:read", StringComparer.Ordinal)))));
}
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Method == HttpMethods.Post && context.Request.Path == "/mcp")
    {
        context.Request.EnableBuffering();
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        context.Request.Body.Position = 0;
        if (document.RootElement.TryGetProperty("method", out var method) && method.GetString() == "tools/call" &&
            document.RootElement.TryGetProperty("params", out var parameters) &&
            parameters.TryGetProperty("name", out var name))
        {
            var toolName = name.GetString() ?? "unknown";
            var hasArguments = parameters.TryGetProperty("arguments", out _);
            context.Response.OnCompleted(async () =>
            {
                var subject = context.User.FindFirst("sub")?.Value ?? "development-anonymous";
                var key = builder.Configuration["Audit:HmacKey"] ?? "development-only-change-me";
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
                var subjectHash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(subject))).ToLowerInvariant();
                var summary = new { subjectHash, toolName, summary = new { hasArguments } };
                try { await clients().PostAsJsonAsync("/internal/diagnostics/audit", summary); } catch { }
            });
        }
    }
    await next(context);
});
HttpClient clients() => app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("resolver");
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapMcp("/mcp").RequireAuthorization("diagnostics:read");
}
else if (app.Environment.IsDevelopment())
{
    app.MapMcp("/mcp");
}
else
{
    throw new InvalidOperationException("Auth0:Domain and Auth0:Audience are required outside Development.");
}
app.Run();
