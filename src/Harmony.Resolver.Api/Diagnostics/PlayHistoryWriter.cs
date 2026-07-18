using Harmony.Resolver.Api.Infrastructure.Persistence;
using Harmony.Resolver.Api.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Resolver.Api.Diagnostics;

public sealed class PlayHistoryWriter(IDbContextFactory<ResolverDbContext> contexts, TimeProvider clock)
{
    public async Task WriteAsync(string videoId, string identityHash, string cache, long durationMs, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.PlayEvents.Add(new PlayEventEntity
        {
            VideoId = videoId,
            IdentityHash = identityHash,
            Cache = cache,
            DurationMs = durationMs,
            PlayedAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlayEventEntity>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.PlayEvents.OrderByDescending(x => x.PlayedAt).Take(limit).ToListAsync(cancellationToken);
    }
}
