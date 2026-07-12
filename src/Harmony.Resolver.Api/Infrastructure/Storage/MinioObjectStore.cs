using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using Minio;
using Minio.DataModel.Args;

namespace Harmony.Resolver.Api.Infrastructure.Storage;

public sealed class MinioObjectStore(IMinioClient client, ObjectStorageOptions options) : IObjectStore
{
    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        var exists = await client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(options.Bucket), cancellationToken);
        if (!exists)
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(options.Bucket), cancellationToken);
    }

    public async Task PutAsync(string objectKey, Stream source, long length, CancellationToken cancellationToken)
    {
        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(options.Bucket)
            .WithObject(objectKey)
            .WithStreamData(source)
            .WithObjectSize(length)
            .WithContentType("audio/ogg"), cancellationToken);
    }

    public async Task CopyToAsync(
        string objectKey, Stream destination, long offset, long length, CancellationToken cancellationToken)
    {
        await client.GetObjectAsync(new GetObjectArgs()
            .WithBucket(options.Bucket)
            .WithObject(objectKey)
            .WithCallbackStream((source, token) => CopyRangeAsync(source, destination, offset, length, token)), cancellationToken);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        await client.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket(options.Bucket)
            .WithObject(objectKey), cancellationToken);
    }

    private static async Task CopyRangeAsync(
        Stream source, Stream destination, long offset, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (offset > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, offset)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Object ended before the requested range.");
            offset -= read;
        }

        while (length > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Object ended before the requested range.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            length -= read;
        }
    }
}
