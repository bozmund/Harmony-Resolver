namespace Harmony.Resolver.Api.Infrastructure.Extraction;

public sealed class ExtractionException(string code, string adapter, Exception? innerException = null)
    : Exception($"{adapter} failed with {code}.", innerException)
{
    public string Code { get; } = code;
    public string Adapter { get; } = adapter;
}
