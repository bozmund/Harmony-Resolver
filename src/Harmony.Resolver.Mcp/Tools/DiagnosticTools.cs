using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Harmony.Resolver.Mcp.Tools;

[McpServerToolType]
public sealed class DiagnosticTools(IHttpClientFactory clients)
{
    [McpServerTool(Name = "get_system_snapshot", ReadOnly = true, Idempotent = true), Description("Returns a sanitized resolver state snapshot.")]
    public async Task<string> GetSystemSnapshot() => await clients.CreateClient("resolver").GetStringAsync("/internal/diagnostics/snapshot");

    [McpServerTool(Name = "get_dependency_health", ReadOnly = true, Idempotent = true), Description("Checks bounded dependency readiness.")]
    public async Task<string> GetDependencyHealth() => await clients.CreateClient("resolver").GetStringAsync("/health/ready");

    [McpServerTool(Name = "run_diagnostic_check", ReadOnly = true, Idempotent = true), Description("Runs the standard read-only diagnostic check.")]
    public async Task<string> RunDiagnosticCheck() => await GetSystemSnapshot();

    [McpServerTool(Name = "list_failed_ingestions", ReadOnly = true, Idempotent = true), Description("Lists up to 200 sanitized failures from the last 24 hours.")]
    public async Task<string> ListFailedIngestions(int limit = 50) =>
        await clients.CreateClient("resolver").GetStringAsync($"/internal/diagnostics/failures?limit={Math.Clamp(limit, 1, 200)}");

    [McpServerTool(Name = "inspect_ingestion", ReadOnly = true, Idempotent = true), Description("Inspects sanitized ingestion state for one YouTube video ID.")]
    public async Task<string> InspectIngestion(string videoId) => await InspectTrack(videoId);

    [McpServerTool(Name = "inspect_track", ReadOnly = true, Idempotent = true), Description("Inspects sanitized track state for one YouTube video ID.")]
    public async Task<string> InspectTrack(string videoId) =>
        await clients.CreateClient("resolver").GetStringAsync($"/internal/diagnostics/tracks/{Uri.EscapeDataString(videoId)}");

    [McpServerTool(Name = "query_logs", ReadOnly = true, Idempotent = true), Description("Returns a bounded log-query capability response; windows are capped at 24 hours and 200 events.")]
    public async Task<string> QueryLogs(int hours = 1, int limit = 50)
    {
        hours = Math.Clamp(hours, 1, 24);
        limit = Math.Clamp(limit, 1, 200);
        var end = DateTimeOffset.UtcNow;
        var start = end - TimeSpan.FromHours(hours);
        return await QueryLokiAsync("{service_name=~\"api-1|api-2|nginx|mcp\"}", start, end, limit);
    }

    [McpServerTool(Name = "get_trace", ReadOnly = true, Idempotent = true), Description("Returns sanitized trace correlation information.")]
    public Task<string> GetTrace(string traceId)
    {
        if (traceId.Length is < 16 or > 64 || !traceId.All(Uri.IsHexDigit))
            throw new ArgumentException("traceId must be 16-64 hexadecimal characters.", nameof(traceId));
        var end = DateTimeOffset.UtcNow;
        return QueryLokiAsync($"{{service_name=~\"api-1|api-2|nginx|mcp\"}} |= \"{traceId}\"", end - TimeSpan.FromHours(24), end, 200);
    }

    [McpServerTool(Name = "query_metrics", ReadOnly = true, Idempotent = true), Description("Returns the resolver Prometheus metric exposition.")]
    public async Task<string> QueryMetrics() => await clients.CreateClient("resolver").GetStringAsync("/metrics");

    [McpServerTool(Name = "get_deployment_info", ReadOnly = true, Idempotent = true), Description("Returns sanitized deployment information.")]
    public async Task<string> GetDeploymentInfo() => await GetSystemSnapshot();

    [McpServerTool(Name = "get_recent_plays", ReadOnly = true, Idempotent = true), Description("Lists the most recently served tracks, with cache status, duration, and hashed listener identity.")]
    public async Task<string> GetRecentPlays(int limit = 5) =>
        await clients.CreateClient("resolver").GetStringAsync($"/internal/diagnostics/recent-plays?limit={Math.Clamp(limit, 1, 50)}");

    private async Task<string> QueryLokiAsync(string query, DateTimeOffset start, DateTimeOffset end, int limit)
    {
        var path = "/loki/api/v1/query_range?query=" + Uri.EscapeDataString(query) +
                   "&start=" + start.ToUnixTimeMilliseconds() * 1_000_000 +
                   "&end=" + end.ToUnixTimeMilliseconds() * 1_000_000 +
                   "&limit=" + limit + "&direction=backward";
        return await clients.CreateClient("loki").GetStringAsync(path);
    }
}
