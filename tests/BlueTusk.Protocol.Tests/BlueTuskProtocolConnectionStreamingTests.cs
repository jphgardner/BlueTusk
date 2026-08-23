using System.Buffers.Binary;
using System.Net;
using System.Text;
using BlueTusk.Transport;

namespace BlueTusk.Protocol.Tests;

public sealed class BlueTuskProtocolConnectionStreamingTests
{
    [Fact]
    public void Streams_a_large_payload_without_buffering_the_next_frame()
    {
        var payload = Enumerable.Range(0, 1_100_000).Select(static value => (byte)value).ToArray();
        var ready = Frame((byte)'Z', [(byte)'I']);
        using var transport = new FragmentedTransport(
            Frame((byte)'D', payload).Concat(ready).ToArray(),
            32 * 1024);
        using var connection = new BlueTuskProtocolConnection(transport);

        var header = connection.ReadMessageHeader();
        Assert.Equal('D', header.Identifier);
        Assert.Equal(payload.Length, header.PayloadLength);

        var actual = new byte[payload.Length];
        var offset = 0;
        while (offset < actual.Length)
        {
            offset += connection.ReadMessagePayload(
                actual.AsSpan(offset, Math.Min(16 * 1024, actual.Length - offset)));
        }

        Assert.Equal(payload, actual);
        Assert.True(
            transport.MaximumRequestedReadLength <= 64 * 1024,
            $"Sequential reads requested {transport.MaximumRequestedReadLength} buffered bytes.");
        Assert.Equal('Z', connection.ReadMessage().Identifier);
    }

    [Fact]
    public async Task Streams_fragmented_headers_and_payloads_asynchronously()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7];
        await using var transport = new FragmentedTransport(Frame((byte)'D', payload), 2);
        await using var connection = new BlueTuskProtocolConnection(transport);

        var header = await connection.ReadMessageHeaderAsync(CancellationToken.None);
        var actual = new byte[payload.Length];
        var offset = 0;
        while (offset < actual.Length)
        {
            offset += await connection.ReadMessagePayloadAsync(
                actual.AsMemory(offset, Math.Min(3, actual.Length - offset)),
                CancellationToken.None);
        }

        Assert.Equal(payload, actual);
        Assert.Equal(0, await connection.ReadMessagePayloadAsync(actual.AsMemory(0, 1), CancellationToken.None));
    }

    [Fact]
    public void Requires_the_active_payload_to_be_consumed()
    {
        using var transport = new FragmentedTransport(Frame((byte)'D', [1, 2]), 16);
        using var connection = new BlueTuskProtocolConnection(transport);

        _ = connection.ReadMessageHeader();

        Assert.Throws<InvalidOperationException>(() => connection.ReadMessageHeader());
        Assert.Throws<InvalidOperationException>(() => connection.ReadMessage());
    }

    [Fact]
    public async Task Reused_write_storage_is_reset_between_sync_and_async_flushes()
    {
        await using var transport = new FragmentedTransport([], 16);
        await using var connection = new BlueTuskProtocolConnection(transport);

        connection.Write(output => WriteUtf8(output, "first"));
        await connection.WriteAsync(
            output => WriteUtf8(output, "second"),
            CancellationToken.None);

        Assert.Equal(["first", "second"], transport.Writes.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public async Task Pre_encoded_messages_bypass_reusable_write_storage_sync_and_async()
    {
        await using var transport = new FragmentedTransport([], 16);
        await using var connection = new BlueTuskProtocolConnection(transport);

        connection.WritePreEncoded("cached-sync"u8);
        await connection.WritePreEncodedAsync("cached-async"u8.ToArray(), CancellationToken.None);

        Assert.Equal(
            ["cached-sync", "cached-async"],
            transport.Writes.Select(Encoding.UTF8.GetString));
        Assert.Throws<ArgumentException>(() => connection.WritePreEncoded([]));
        await Assert.ThrowsAsync<ArgumentException>(
            () => connection.WritePreEncodedAsync(
                ReadOnlyMemory<byte>.Empty,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Large_leased_payload_reads_coalesce_transport_fragments()
    {
        var payload = Enumerable.Range(0, 192 * 1024).Select(static value => (byte)value).ToArray();
        await using var transport = new FragmentedTransport(Frame((byte)'D', payload), 32 * 1024);
        await using var connection = new BlueTuskProtocolConnection(transport);
        var header = await connection.ReadMessageHeaderAsync(CancellationToken.None);
        Assert.Equal(payload.Length, header.PayloadLength);

        var first = new byte[128 * 1024];
        var firstRead = await connection.ReadLeasedMessagePayloadAsync(
            first,
            0,
            static (_, read) => read,
            CancellationToken.None);
        Assert.Equal(first.Length, firstRead);
        Assert.Equal(payload.AsSpan(0, first.Length).ToArray(), first);

        var second = new byte[payload.Length - first.Length];
        var secondRead = await connection.ReadLeasedMessagePayloadAsync(
            second,
            0,
            static (_, read) => read,
            CancellationToken.None);
        Assert.Equal(second.Length, secondRead);
        Assert.Equal(payload.AsSpan(first.Length).ToArray(), second);
        Assert.Equal(0, connection.ActiveMessagePayloadRemaining);
    }

    [Fact]
    public async Task Sensitive_writes_overwrite_reusable_storage_after_sync_and_async_flushes()
    {
        await using var transport = new FragmentedTransport([], 16);
        await using var connection = new BlueTuskProtocolConnection(transport);
        Memory<byte> synchronousStorage = default;
        Memory<byte> asynchronousStorage = default;

        connection.WriteSensitive(output =>
        {
            synchronousStorage = output.GetMemory(6)[..6];
            "secret"u8.CopyTo(synchronousStorage.Span);
            output.Advance(6);
        });
        await connection.WriteSensitiveAsync(
            output =>
            {
                asynchronousStorage = output.GetMemory(5)[..5];
                "token"u8.CopyTo(asynchronousStorage.Span);
                output.Advance(5);
            },
            CancellationToken.None);

        Assert.All(synchronousStorage.ToArray(), static value => Assert.Equal(0, value));
        Assert.All(asynchronousStorage.ToArray(), static value => Assert.Equal(0, value));
        Assert.Equal(["secret", "token"], transport.Writes.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public async Task Disposal_does_not_return_the_read_buffer_while_an_async_read_is_active()
    {
        var transport = new BlockingReadTransport();
        var connection = new BlueTuskProtocolConnection(transport);
        var read = connection.ReadMessageAsync(CancellationToken.None).AsTask();

        await transport.ReadStarted;
        await connection.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => read);
    }

    private static byte[] Frame(byte code, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = code;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + sizeof(int));
        payload.CopyTo(frame, 5);
        return frame;
    }

    private static void WriteUtf8(System.Buffers.IBufferWriter<byte> output, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        Encoding.UTF8.GetBytes(value, output.GetSpan(length));
        output.Advance(length);
    }

    private sealed class FragmentedTransport(byte[] input, int maximumRead) : IBlueTuskTransport
    {
        private int _offset;

        public List<byte[]> Writes { get; } = [];

        public int MaximumRequestedReadLength { get; private set; }

        public EndPoint? RemoteEndPoint => null;

        public void Connect(BlueTuskEndpoint endpoint, BlueTuskTransportOptions options)
        {
        }

        public ValueTask ConnectAsync(
            BlueTuskEndpoint endpoint,
            BlueTuskTransportOptions options,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public int Read(Span<byte> buffer)
        {
            MaximumRequestedReadLength = Math.Max(MaximumRequestedReadLength, buffer.Length);
            var count = Math.Min(Math.Min(buffer.Length, maximumRead), input.Length - _offset);
            input.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public void Write(ReadOnlySpan<byte> buffer)
        {
            Writes.Add(buffer.ToArray());
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public void Flush()
        {
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingReadTransport : IBlueTuskTransport
    {
        private readonly TaskCompletionSource _disposed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;

        public EndPoint? RemoteEndPoint => null;

        public void Connect(BlueTuskEndpoint endpoint, BlueTuskTransportOptions options)
        {
        }

        public ValueTask ConnectAsync(
            BlueTuskEndpoint endpoint,
            BlueTuskTransportOptions options,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult();
            await _disposed.Task.WaitAsync(cancellationToken);
            throw new ObjectDisposedException(nameof(BlockingReadTransport));
        }

        public int Read(Span<byte> buffer) => throw new NotSupportedException();

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Write(ReadOnlySpan<byte> buffer)
        {
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Flush()
        {
        }

        public void Dispose() => _disposed.TrySetResult();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
