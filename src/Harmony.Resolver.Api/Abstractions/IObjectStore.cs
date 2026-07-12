namespace Harmony.Resolver.Api.Abstractions;

public interface IObjectStore
{
    Task EnsureReadyAsync(CancellationToken cancellationToken);
    Task PutAsync(string objectKey, Stream source, long length, CancellationToken cancellationToken);
    Task CopyToAsync(string objectKey, Stream destination, long offset, long length, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}
