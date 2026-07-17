using System.Text.Json;
using Harmony.Resolver.Api.Infrastructure.Persistence;
using Harmony.Resolver.Api.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Resolver.Api.Diagnostics;

public sealed class DiagnosticAuditWriter(IDbContextFactory<ResolverDbContext> contexts, TimeProvider clock)
{
    public async Task WriteAsync(string subjectHash, string toolName, JsonDocument summary, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.DiagnosticAudits.Add(new DiagnosticAuditEntity
        {
            SubjectHash = subjectHash,
            ToolName = toolName,
            QuerySummary = summary,
            OccurredAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
