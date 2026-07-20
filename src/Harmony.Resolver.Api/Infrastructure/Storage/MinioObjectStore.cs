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
        await client.GetObjectAsync(
            CreateRangeRequest(options.Bucket, objectKey, destination, offset, length),
            cancellationToken);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        await client.RemoveObjectAsync(new RemoveObjectArgs()
            .WithBucket(options.Bucket)
            .WithObject(objectKey), cancellationToken);
    }

    internal static GetObjectArgs CreateRangeRequest(
        string bucket,
        string objectKey,
        Stream destination,
        long offset,
        long length)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        return new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectKey)
            .WithOffsetAndLength(offset, length)
            .WithCallbackStream((source, token) =>
                CopyLengthAsync(source, destination, length, token));
    }

    private static async Task CopyLengthAsync(
        Stream source,
        Stream destination,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (length > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Object ended before the requested range.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            length -= read;
        }
    }
}
