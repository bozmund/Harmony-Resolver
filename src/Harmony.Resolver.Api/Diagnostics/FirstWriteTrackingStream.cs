namespace Harmony.Resolver.Api.Diagnostics;

/// <summary>
/// A non-owning response-body wrapper that invokes a callback after the first
/// successful, non-empty write. Failed and zero-length writes do not count.
/// </summary>
internal sealed class FirstWriteTrackingStream(Stream inner, Action onFirstWrite) : Stream
{
    private int _firstWriteRecorded;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanTimeout => inner.CanTimeout;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int ReadTimeout
    {
        get => inner.ReadTimeout;
        set => inner.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => inner.WriteTimeout;
        set => inner.WriteTimeout = value;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        RecordFirstWrite(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        RecordFirstWrite(buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        inner.WriteByte(value);
        RecordFirstWrite(1);
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer, offset, count, cancellationToken);
        RecordFirstWrite(count);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        RecordFirstWrite(buffer.Length);
    }

    protected override void Dispose(bool disposing)
    {
        // ASP.NET owns the response body. This wrapper intentionally never closes it.
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void RecordFirstWrite(int byteCount)
    {
        if (byteCount > 0 && Interlocked.CompareExchange(ref _firstWriteRecorded, 1, 0) == 0)
            onFirstWrite();
    }
}
