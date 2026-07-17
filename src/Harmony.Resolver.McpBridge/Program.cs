using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

LoadDotEnvFile();
LoadUserSecretsFallback();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<Auth0TokenProvider>();
builder.Services.AddSingleton<RemoteMcpClient>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<BridgeTools>();
await builder.Build().RunAsync();

static void LoadDotEnvFile([CallerFilePath] string sourceFilePath = "")
{
    var envPath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, ".env");
    if (!File.Exists(envPath)) return;
    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var separatorIndex = line.IndexOf('=');
        if (separatorIndex < 0) continue;
        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim().Trim('"');
        if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
    }
}

// Fallback for values not already supplied by a real env var or .env, e.g. `dotnet user-secrets set AUTH0_CLIENT_ID ...`.
static void LoadUserSecretsFallback()
{
    var configuration = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).Build();
    foreach (var key in new[] { "HARMONY_MCP_URL", "AUTH0_DOMAIN", "AUTH0_CLIENT_ID", "AUTH0_CLIENT_SECRET", "AUTH0_AUDIENCE" })
    {
        if (Environment.GetEnvironmentVariable(key) is null && configuration[key] is { } value)
            Environment.SetEnvironmentVariable(key, value);
    }
}

public sealed class Auth0TokenProvider(HttpClient http)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _refreshAt;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _refreshAt) return _token;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _refreshAt) return _token;
            var domain = Required("AUTH0_DOMAIN");
            var response = await http.PostAsJsonAsync($"https://{domain.TrimEnd('/')}/oauth/token", new
            {
                client_id = Required("AUTH0_CLIENT_ID"),
                client_secret = Required("AUTH0_CLIENT_SECRET"),
                audience = Environment.GetEnvironmentVariable("AUTH0_AUDIENCE") ?? "https://harmony-resolver-diagnostics",
                grant_type = "client_credentials"
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Auth0 returned no token.");
            _token = token.access_token;
            _refreshAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(30, token.expires_in - 60));
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is required.");
    private sealed record TokenResponse(string access_token, int expires_in);
}

public sealed class RemoteMcpClient(Auth0TokenProvider tokens)
{
    public async Task<string> CallAsync(
        string toolName, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("HARMONY_MCP_URL")
            ?? throw new InvalidOperationException("HARMONY_MCP_URL is required."));
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(cancellationToken));
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = "harmony-resolver-remote",
            TransportMode = HttpTransportMode.StreamableHttp
        }, http, ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
        return JsonSerializer.Serialize(result);
    }
}

[McpServerToolType]
public sealed class BridgeTools(RemoteMcpClient remote)
{
    [McpServerTool(Name = "get_system_snapshot", ReadOnly = true, Idempotent = true), Description("Returns a sanitized resolver snapshot.")]
    public Task<string> Snapshot(CancellationToken cancellationToken) => Call("get_system_snapshot", null, cancellationToken);
    [McpServerTool(Name = "get_dependency_health", ReadOnly = true, Idempotent = true), Description("Returns dependency health.")]
    public Task<string> Health(CancellationToken cancellationToken) => Call("get_dependency_health", null, cancellationToken);
    [McpServerTool(Name = "list_failed_ingestions", ReadOnly = true, Idempotent = true), Description("Lists bounded recent failures.")]
    public Task<string> Failures(int limit, CancellationToken cancellationToken) => Call("list_failed_ingestions", new Dictionary<string, object?> { ["limit"] = limit }, cancellationToken);
    [McpServerTool(Name = "inspect_ingestion", ReadOnly = true, Idempotent = true), Description("Inspects one ingestion.")]
    public Task<string> Ingestion(string videoId, CancellationToken cancellationToken) => Call("inspect_ingestion", new Dictionary<string, object?> { ["videoId"] = videoId }, cancellationToken);
    [McpServerTool(Name = "inspect_track", ReadOnly = true, Idempotent = true), Description("Inspects one track.")]
    public Task<string> Track(string videoId, CancellationToken cancellationToken) => Call("inspect_track", new Dictionary<string, object?> { ["videoId"] = videoId }, cancellationToken);
    [McpServerTool(Name = "query_logs", ReadOnly = true, Idempotent = true), Description("Queries bounded logs.")]
    public Task<string> Logs(int hours, int limit, CancellationToken cancellationToken) => Call("query_logs", new Dictionary<string, object?> { ["hours"] = hours, ["limit"] = limit }, cancellationToken);
    [McpServerTool(Name = "get_trace", ReadOnly = true, Idempotent = true), Description("Gets trace correlation details.")]
    public Task<string> Trace(string traceId, CancellationToken cancellationToken) => Call("get_trace", new Dictionary<string, object?> { ["traceId"] = traceId }, cancellationToken);
    [McpServerTool(Name = "query_metrics", ReadOnly = true, Idempotent = true), Description("Queries resolver metrics.")]
    public Task<string> Metrics(CancellationToken cancellationToken) => Call("query_metrics", null, cancellationToken);
    [McpServerTool(Name = "get_deployment_info", ReadOnly = true, Idempotent = true), Description("Gets deployment information.")]
    public Task<string> Deployment(CancellationToken cancellationToken) => Call("get_deployment_info", null, cancellationToken);
    [McpServerTool(Name = "run_diagnostic_check", ReadOnly = true, Idempotent = true), Description("Runs standard diagnostics.")]
    public Task<string> Check(CancellationToken cancellationToken) => Call("run_diagnostic_check", null, cancellationToken);

    private Task<string> Call(string name, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken) =>
        remote.CallAsync(name, arguments, cancellationToken);
}
