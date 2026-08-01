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
        var payload = Enumerable.Range(0, 40_000).Select(static value => (byte)value).ToArray();
        var ready = Frame((byte)'Z', [(byte)'I']);
        using var transport = new FragmentedTransport(Frame((byte)'D', payload).Concat(ready).ToArray(), 37);
        using var connection = new BlueTuskProtocolConnection(transport);

        var header = connection.ReadMessageHeader();
        Assert.Equal('D', header.Identifier);
        Assert.Equal(payload.Length, header.PayloadLength);

        var actual = new byte[payload.Length];
        var offset = 0;
        while (offset < actual.Length)
        {
            offset += connection.ReadMessagePayload(actual.AsSpan(offset, Math.Min(113, actual.Length - offset)));
        }

        Assert.Equal(payload, actual);
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
}
