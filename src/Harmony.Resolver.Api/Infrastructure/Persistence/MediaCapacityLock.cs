using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Resolver.Api.Infrastructure.Persistence;

/// <summary>
/// Cross-replica serialization for the final capacity check and ready-object commit. The known
/// normalized size is checked while a PostgreSQL advisory lock is held, so concurrent API replicas
/// cannot both observe the same free bytes and overrun the permanent-media ceiling.
/// </summary>
public static class MediaCapacityLock
{
    private const long LockId = 0x4841524D4F4E59;

    public static async Task<IAsyncDisposable> AcquireAsync(
        IDbContextFactory<ResolverDbContext> contexts,
        CancellationToken cancellationToken)
    {
        var db = await contexts.CreateDbContextAsync(cancellationToken);
        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await ExecuteAsync(db, "SELECT pg_advisory_lock(@lock_id)", cancellationToken);
            return new Releaser(db);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(
        ResolverDbContext db,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_id";
        parameter.DbType = DbType.Int64;
        parameter.Value = LockId;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private sealed class Releaser(ResolverDbContext db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await ExecuteAsync(db, "SELECT pg_advisory_unlock(@lock_id)", CancellationToken.None);
            }
            finally
            {
                await db.DisposeAsync();
            }
        }
    }
}
