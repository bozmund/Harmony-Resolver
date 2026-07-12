namespace Harmony.Resolver.Api.Domain;

public sealed record RepositoryStatistics(IReadOnlyDictionary<string, int> StatusCounts, int ActiveLeases);
