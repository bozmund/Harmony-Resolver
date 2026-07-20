using Harmony.Resolver.Api.Infrastructure.Storage;
using System.Reflection;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class MinioObjectStoreTests
{
    [Theory]
    [InlineData(0, 16)]
    [InlineData(4_194_304, 65_536)]
    public async Task Range_request_propagates_exact_native_offset_and_length(
        long offset,
        long length)
    {
        await using var destination = new MemoryStream();
        var args = MinioObjectStore.CreateRangeRequest(
            "audio",
            "tracks/AbCdEfGhIjK.ogg",
            destination,
            offset,
            length);

        Assert.True(GetInternalProperty<bool>(args, "OffsetLengthSet"));
        Assert.Equal(offset, GetInternalProperty<long>(args, "ObjectOffset"));
        Assert.Equal(length, GetInternalProperty<long>(args, "ObjectLength"));
        Assert.Equal("audio", GetInternalProperty<string>(args, "BucketName"));
        Assert.Equal(
            "tracks/AbCdEfGhIjK.ogg",
            GetInternalProperty<string>(args, "ObjectName"));

        var rangedResponse = Enumerable.Range(0, checked((int)length + 1))
            .Select(index => (byte)(index % 251))
            .ToArray();
        var callback = GetInternalProperty<Func<Stream, CancellationToken, Task>>(
            args,
            "CallBack");
        await callback(new MemoryStream(rangedResponse), CancellationToken.None);

        Assert.Equal(length, destination.Length);
        Assert.Equal(rangedResponse.AsSpan(0, checked((int)length)).ToArray(), destination.ToArray());
    }

    [Fact]
    public async Task Range_callback_honors_cancellation()
    {
        await using var destination = new MemoryStream();
        var args = MinioObjectStore.CreateRangeRequest(
            "audio",
            "tracks/AbCdEfGhIjK.ogg",
            destination,
            offset: 4096,
            length: 128);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var callback = GetInternalProperty<Func<Stream, CancellationToken, Task>>(
            args,
            "CallBack");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await callback(new MemoryStream(new byte[128]), cancellation.Token));
        Assert.Empty(destination.ToArray());
    }

    private static T GetInternalProperty<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(instance));
    }
}
