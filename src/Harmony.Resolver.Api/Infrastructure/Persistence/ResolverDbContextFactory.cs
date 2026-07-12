using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harmony.Resolver.Api.Infrastructure.Persistence;

public sealed class ResolverDbContextFactory : IDesignTimeDbContextFactory<ResolverDbContext>
{
    public ResolverDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSql")
            ?? "Host=localhost;Database=harmony;Username=harmony;Password=development-only";
        var options = new DbContextOptionsBuilder<ResolverDbContext>().UseNpgsql(connectionString).Options;
        return new ResolverDbContext(options);
    }
}
