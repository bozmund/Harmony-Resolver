namespace Harmony.Resolver.Api.Configuration;

public sealed class ObjectStorageOptions
{
    public string Endpoint { get; init; } = "http://localhost:9000";
    public string AccessKey { get; init; } = "harmony";
    public string SecretKey { get; init; } = "development-only-minio";
    public string Bucket { get; init; } = "harmony-audio";
}
