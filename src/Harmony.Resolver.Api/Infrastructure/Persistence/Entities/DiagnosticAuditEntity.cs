using System.Text.Json;

namespace Harmony.Resolver.Api.Infrastructure.Persistence.Entities;

public sealed class DiagnosticAuditEntity
{
    public long Id { get; set; }
    public required string SubjectHash { get; set; }
    public required string ToolName { get; set; }
    public JsonDocument QuerySummary { get; set; } = JsonDocument.Parse("{}");
    public DateTimeOffset OccurredAt { get; set; }
}
