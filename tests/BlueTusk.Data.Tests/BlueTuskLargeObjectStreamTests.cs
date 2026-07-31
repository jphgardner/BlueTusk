using System.Text;
using BlueTusk.Data.LargeObjects;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskLargeObjectStreamTests
{
    [Fact]
    public async Task Reads_writes_seeks_and_truncates_asynchronously()
    {
        using var operations = new FakeLargeObjectOperations("abcdef"u8.ToArray());
        var stream = new BlueTuskLargeObjectStream(
            42,
            FileAccess.ReadWrite,
            operations.Length,
            position: 0,
            operations);

        var prefix = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(prefix));
        Assert.Equal("abc", Encoding.UTF8.GetString(prefix));
        Assert.Equal(3, stream.Position);
        Assert.Equal(6, stream.Length);

        Assert.Equal(2, await stream.SeekAsync(-1, SeekOrigin.Current));
        await stream.WriteAsync("XYZ"u8.ToArray());
        Assert.Equal(5, stream.Position);
        Assert.Equal(6, stream.Length);

        await stream.SetLengthAsync(4);
        Assert.Equal(4, stream.Length);
        Assert.Equal(5, stream.Position);

        await stream.DisposeAsync();
        Assert.True(operations.Closed);
        Assert.True(operations.Commit);
        Assert.Equal("abXY", Encoding.UTF8.GetString(operations.ToArray()));
    }

    [Fact]
    public async Task Chunks_large_writes_without_short_write_acceptance()
    {
        using var operations = new FakeLargeObjectOperations([]);
        await using var stream = new BlueTuskLargeObjectStream(
            42,
            FileAccess.Write,
            length: 0,
            position: 0,
            operations);
        var value = new byte[BlueTuskLargeObjectStream.MaximumTransferSize + 17];

        await stream.WriteAsync(value);

        Assert.Equal(
            [BlueTuskLargeObjectStream.MaximumTransferSize, 17],
            operations.WriteSizes);
        Assert.Equal(value.Length, stream.Length);
        Assert.Equal(value.Length, stream.Position);
    }

    [Fact]
    public async Task Faulted_operations_rollback_an_owned_stream()
    {
        using var operations = new FakeLargeObjectOperations("abc"u8.ToArray())
        {
            FailRead = true,
        };
        var stream = new BlueTuskLargeObjectStream(
            42,
            FileAccess.Read,
            operations.Length,
            position: 0,
            operations);

        await Assert.ThrowsAsync<IOException>(
            () => stream.ReadAsync(new byte[1]).AsTask());
        await stream.DisposeAsync();

        Assert.True(operations.Closed);
        Assert.False(operations.Commit);
    }

    [Fact]
    public async Task Enforces_access_contract_for_sync_and_async_operations()
    {
        using var readOperations = new FakeLargeObjectOperations("abc"u8.ToArray());
        await using var read = new BlueTuskLargeObjectStream(
            42,
            FileAccess.Read,
            readOperations.Length,
            position: 0,
            readOperations);
        using var writeOperations = new FakeLargeObjectOperations([]);
        await using var write = new BlueTuskLargeObjectStream(
            43,
            FileAccess.Write,
            length: 0,
            position: 0,
            writeOperations);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => read.WriteAsync(new byte[1]).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => write.ReadAsync(new byte[1]).AsTask());
        Assert.Throws<NotSupportedException>(() => read.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => write.Read(new byte[1], 0, 1));
        Assert.Equal(0, read.Seek(0, SeekOrigin.Begin));
        write.SetLength(0);
    }

    [Fact]
    public void Synchronous_operations_and_disposal_complete_normally()
    {
        using var operations = new FakeLargeObjectOperations("abcdef"u8.ToArray());
        var stream = new BlueTuskLargeObjectStream(
            42,
            FileAccess.ReadWrite,
            length: operations.Length,
            position: 0,
            operations);

        var prefix = new byte[3];
        Assert.Equal(3, stream.Read(prefix, 0, prefix.Length));
        Assert.Equal("abc", Encoding.UTF8.GetString(prefix));
        Assert.Equal(2, stream.Seek(-1, SeekOrigin.Current));
        stream.Write("XY"u8.ToArray(), 0, 2);
        stream.SetLength(4);
        stream.Dispose();

        Assert.True(operations.Closed);
        Assert.True(operations.Commit);
        Assert.False(operations.Abandoned);
        Assert.Equal("abXY", Encoding.UTF8.GetString(operations.ToArray()));
    }

    private sealed class FakeLargeObjectOperations :
        IBlueTuskLargeObjectOperations,
        IDisposable
    {
        private readonly MemoryStream _stream;

        public FakeLargeObjectOperations(byte[] value)
        {
            _stream = new MemoryStream();
            _stream.Write(value);
            _stream.Position = 0;
        }

        public bool FailRead { get; init; }

        public bool Closed { get; private set; }

        public bool Commit { get; private set; }

        public bool Abandoned { get; private set; }

        public long Length => _stream.Length;

        public List<int> WriteSizes { get; } = [];

        public byte[] Read(int count)
        {
            if (FailRead)
            {
                throw new IOException("Simulated large-object read failure.");
            }

            var value = new byte[Math.Min(count, checked((int)(_stream.Length - _stream.Position)))];
            _ = _stream.Read(value);
            return value;
        }

        public ValueTask<byte[]> ReadAsync(int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRead)
            {
                throw new IOException("Simulated large-object read failure.");
            }

            var value = new byte[Math.Min(count, checked((int)(_stream.Length - _stream.Position)))];
            _ = _stream.Read(value);
            return ValueTask.FromResult(value);
        }

        public ValueTask<int> WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteSizes.Add(buffer.Length);
            _stream.Write(buffer.Span);
            return ValueTask.FromResult(buffer.Length);
        }

        public int Write(ReadOnlySpan<byte> buffer)
        {
            WriteSizes.Add(buffer.Length);
            _stream.Write(buffer);
            return buffer.Length;
        }

        public long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

        public ValueTask<long> SeekAsync(
            long offset,
            SeekOrigin origin,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_stream.Seek(offset, origin));
        }

        public ValueTask SetLengthAsync(long value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream.SetLength(value);
            return ValueTask.CompletedTask;
        }

        public void SetLength(long value) => _stream.SetLength(value);

        public void Close(bool commit)
        {
            Closed = true;
            Commit = commit;
        }

        public ValueTask CloseAsync(bool commit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Closed = true;
            Commit = commit;
            return ValueTask.CompletedTask;
        }

        public void Abandon() => Abandoned = true;

        public byte[] ToArray() => _stream.ToArray();

        public void Dispose() => _stream.Dispose();
    }
}
