using System.Buffers.Binary;
using System.Net;
using BenchmarkDotNet.Attributes;
using BlueTusk.Protocol;
using BlueTusk.Transport;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class ProtocolStreamingBenchmarks : IDisposable
{
    private readonly byte[] _frame;
    private readonly byte[] _destination = new byte[8192];
    private readonly BlueTuskProtocolConnection _connection;

    public ProtocolStreamingBenchmarks()
    {
        const int payloadLength = 1024 * 1024;
        _frame = new byte[payloadLength + 5];
        _frame[0] = (byte)'D';
        BinaryPrimitives.WriteInt32BigEndian(_frame.AsSpan(1), payloadLength + sizeof(int));
        _connection = new BlueTuskProtocolConnection(new MemoryTransport(_frame));
    }

    [Benchmark]
    public int StreamOneMegabyteBackendPayload()
    {
        var header = _connection.ReadMessageHeader();
        var total = 0;
        int read;
        while ((read = _connection.ReadMessagePayload(_destination)) != 0)
        {
            total += read;
        }

        return total + header.PayloadLength;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class MemoryTransport(byte[] input) : IBlueTuskTransport
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
            if (_offset == input.Length)
            {
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, input.Length - _offset);
            input.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Read(buffer.Span));

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
