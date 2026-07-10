using System.ComponentModel;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("resolver", c => c.BaseAddress = new Uri(builder.Configuration["Resolver:BaseUrl"] ?? "http://localhost:8080"));
builder.Services.AddMcpServer().WithHttpTransport().WithTools<DiagnosticTools>();
var app = builder.Build();
app.MapMcp("/mcp");
app.Run();

[McpServerToolType]
public sealed class DiagnosticTools(IHttpClientFactory clients)
{
    [McpServerTool(Name = "get_system_snapshot", ReadOnly = true, Idempotent = true), Description("Returns a sanitized resolver state snapshot.")]
    public async Task<string> GetSystemSnapshot() => await clients.CreateClient("resolver").GetStringAsync("/internal/diagnostics/snapshot");

    [McpServerTool(Name = "get_dependency_health", ReadOnly = true, Idempotent = true), Description("Checks bounded dependency readiness.")]
    public async Task<string> GetDependencyHealth() => await clients.CreateClient("resolver").GetStringAsync("/health/ready");

    [McpServerTool(Name = "run_diagnostic_check", ReadOnly = true, Idempotent = true), Description("Runs the standard read-only diagnostic check.")]
    public async Task<string> RunDiagnosticCheck() => await GetSystemSnapshot();
}
