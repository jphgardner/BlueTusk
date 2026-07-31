using System.Buffers;
using BlueTusk.Transport;

namespace BlueTusk.Protocol;

/// <summary>Owns transport buffering and PostgreSQL frame boundaries for one physical connection.</summary>
public sealed class BlueTuskProtocolConnection : IAsyncDisposable, IDisposable
{
    private const int InitialBufferSize = 16 * 1024;
    private readonly IBlueTuskTransport _transport;
    private readonly BlueTuskBackendMessageParser _parser;
    private byte[] _buffer;
    private int _start;
    private int _count;
    private bool _disposed;

    public BlueTuskProtocolConnection(
        IBlueTuskTransport transport,
        int maximumMessageSize = BlueTuskBackendMessageParser.DefaultMaximumMessageSize)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _parser = new BlueTuskBackendMessageParser(maximumMessageSize);
        _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
    }

    public BlueTuskProtocolStateMachine StateMachine { get; } = new();

    public IBlueTuskTransport Transport => _transport;

    public void Connect(
        BlueTuskEndpoint endpoint,
        BlueTuskTransportOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _transport.Connect(endpoint, options);
        StateMachine.TransitionTo(BlueTuskConnectionState.TransportConnected);
    }

    public async ValueTask ConnectAsync(
        BlueTuskEndpoint endpoint,
        BlueTuskTransportOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transport.ConnectAsync(endpoint, options, cancellationToken).ConfigureAwait(false);
        StateMachine.TransitionTo(BlueTuskConnectionState.TransportConnected);
    }

    public async ValueTask<byte> ReadUnframedByteAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_count != 0)
        {
            throw new InvalidOperationException("Unframed bytes cannot be read after protocol buffering has started.");
        }

        var read = await _transport.ReadAsync(_buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException("PostgreSQL disconnected while an unframed response byte was expected.");
        }

        return _buffer[0];
    }

    public byte ReadUnframedByte()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_count != 0)
        {
            throw new InvalidOperationException("Unframed bytes cannot be read after protocol buffering has started.");
        }

        var read = _transport.Read(_buffer.AsSpan(0, 1));
        if (read == 0)
        {
            throw new EndOfStreamException("PostgreSQL disconnected while an unframed response byte was expected.");
        }

        return _buffer[0];
    }

    /// <remarks>The returned payload remains valid only until the next read from this connection.</remarks>
    public async ValueTask<BlueTuskBackendMessage> ReadMessageAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            var sequence = new ReadOnlySequence<byte>(_buffer.AsMemory(_start, _count));
            var originalLength = sequence.Length;
            if (_parser.TryParse(ref sequence, out var message))
            {
                var consumed = checked((int)(originalLength - sequence.Length));
                _start += consumed;
                _count -= consumed;
                return message;
            }

            PrepareForRead();
            var read = await _transport.ReadAsync(
                _buffer.AsMemory(_start + _count),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    _count == 0
                        ? "PostgreSQL closed the connection."
                        : "PostgreSQL disconnected in the middle of a protocol message.");
            }

            _count += read;
        }
    }

    /// <remarks>The returned payload remains valid only until the next read from this connection.</remarks>
    public BlueTuskBackendMessage ReadMessage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            var sequence = new ReadOnlySequence<byte>(_buffer.AsMemory(_start, _count));
            var originalLength = sequence.Length;
            if (_parser.TryParse(ref sequence, out var message))
            {
                var consumed = checked((int)(originalLength - sequence.Length));
                _start += consumed;
                _count -= consumed;
                return message;
            }

            PrepareForRead();
            var read = _transport.Read(_buffer.AsSpan(_start + _count));
            if (read == 0)
            {
                throw new EndOfStreamException(
                    _count == 0
                        ? "PostgreSQL closed the connection."
                        : "PostgreSQL disconnected in the middle of a protocol message.");
            }

            _count += read;
        }
    }

    public async ValueTask WriteAsync(
        Action<IBufferWriter<byte>> writeMessage,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(writeMessage);

        var output = new ArrayBufferWriter<byte>();
        writeMessage(output);
        await _transport.WriteAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Write(Action<IBufferWriter<byte>> writeMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(writeMessage);

        var output = new ArrayBufferWriter<byte>();
        writeMessage(output);
        _transport.Write(output.WrittenSpan);
        _transport.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReturnBuffer();
        _transport.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReturnBuffer();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private void PrepareForRead()
    {
        if (_start > 0)
        {
            _buffer.AsSpan(_start, _count).CopyTo(_buffer);
            _start = 0;
        }

        if (_count < _buffer.Length)
        {
            return;
        }

        var maximumBufferSize = checked(_parser.MaximumMessageSize + 1);
        if (_buffer.Length >= maximumBufferSize)
        {
            throw new BlueTuskProtocolException(
                $"A backend message exceeded the configured maximum {_parser.MaximumMessageSize}.");
        }

        var nextSize = Math.Min(checked(_buffer.Length * 2), maximumBufferSize);
        var replacement = ArrayPool<byte>.Shared.Rent(nextSize);
        _buffer.AsSpan(0, _count).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }

    private void ReturnBuffer()
    {
        var buffer = _buffer;
        _buffer = [];
        _start = 0;
        _count = 0;
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
