using System.Buffers.Binary;
using System.Net;
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

    private static byte[] Frame(byte code, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = code;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + sizeof(int));
        payload.CopyTo(frame, 5);
        return frame;
    }

    private sealed class FragmentedTransport(byte[] input, int maximumRead) : IBlueTuskTransport
    {
        private int _offset;

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
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

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
