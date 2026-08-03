using System.IO.Pipelines;
using BlueTusk.Protocol;
using BlueTusk.Transport;

namespace BlueTusk.Benchmarks;

/// <summary>
/// A benchmark-only System.IO.Pipelines transport prototype. It deliberately stays outside the
/// production dependency graph while the transport decision is evaluated.
/// </summary>
internal sealed class TransportPipelinePrototype : IAsyncDisposable, IDisposable
{
    private const long PauseWriterThreshold = 2 * 1024 * 1024;
    private const int PumpWindowSize = 64 * 1024;
    private readonly Pipe _pipe = new(
        new PipeOptions(
            pauseWriterThreshold: PauseWriterThreshold,
            resumeWriterThreshold: PauseWriterThreshold / 2,
            minimumSegmentSize: 4 * 1024,
            useSynchronizationContext: false));
    private readonly BlueTuskBackendMessageParser _parser = new();

    public long ReadBatch(
        IBlueTuskTransport transport,
        int byteCount,
        int messageCount)
    {
        var checksum = 0L;
        var messagesRead = 0;
        var remaining = byteCount;
        while (messagesRead < messageCount)
        {
            remaining -= Pump(transport, Math.Min(remaining, PumpWindowSize));
            if (!_pipe.Reader.TryRead(out var result))
            {
                throw new InvalidOperationException("The pipeline did not expose a flushed transport read.");
            }

            var buffer = result.Buffer;
            while (messagesRead < messageCount && _parser.TryParse(ref buffer, out var message))
            {
                checksum += Consume(message);
                messagesRead++;
            }

            _pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
        }

        return checksum;
    }

    public async ValueTask<long> ReadBatchAsync(
        IBlueTuskTransport transport,
        int byteCount,
        int messageCount)
    {
        var checksum = 0L;
        var messagesRead = 0;
        var remaining = byteCount;
        while (messagesRead < messageCount)
        {
            remaining -= await PumpAsync(
                transport,
                Math.Min(remaining, PumpWindowSize)).ConfigureAwait(false);
            var result = await _pipe.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            var buffer = result.Buffer;
            while (messagesRead < messageCount && _parser.TryParse(ref buffer, out var message))
            {
                checksum += Consume(message);
                messagesRead++;
            }

            _pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
        }

        return checksum;
    }

    public void Dispose()
    {
        _pipe.Reader.Complete();
        _pipe.Writer.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
        await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
    }

    private int Pump(IBlueTuskTransport transport, int byteCount)
    {
        var remaining = byteCount;
        while (remaining != 0)
        {
            var destination = _pipe.Writer.GetSpan();
            var read = transport.Read(destination[..Math.Min(destination.Length, remaining)]);
            if (read == 0)
            {
                throw new EndOfStreamException("The prototype transport ended before the benchmark batch.");
            }

            _pipe.Writer.Advance(read);
            remaining -= read;
        }

        _pipe.Writer.FlushAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return byteCount;
    }

    private async ValueTask<int> PumpAsync(IBlueTuskTransport transport, int byteCount)
    {
        var remaining = byteCount;
        while (remaining != 0)
        {
            var destination = _pipe.Writer.GetMemory();
            var read = await transport.ReadAsync(
                destination[..Math.Min(destination.Length, remaining)],
                CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The prototype transport ended before the benchmark batch.");
            }

            _pipe.Writer.Advance(read);
            remaining -= read;
        }

        await _pipe.Writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return byteCount;
    }

    internal static long Consume(BlueTuskBackendMessage message)
    {
        var checksum = message.Identifier + message.Payload.Length;
        if (!message.Payload.IsEmpty)
        {
            checksum += message.Payload.FirstSpan[0];
        }

        return checksum;
    }
}

internal sealed class ReplayTransport(byte[] input, int fragmentSize) : IBlueTuskTransport
{
    private int _offset;

    public System.Net.EndPoint? RemoteEndPoint => null;

    public void Reset() => _offset = 0;

    public int Read(Span<byte> buffer)
    {
        var count = Math.Min(Math.Min(buffer.Length, fragmentSize), input.Length - _offset);
        input.AsSpan(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public void Connect(BlueTuskEndpoint endpoint, BlueTuskTransportOptions options)
    {
    }

    public ValueTask ConnectAsync(
        BlueTuskEndpoint endpoint,
        BlueTuskTransportOptions options,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

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
