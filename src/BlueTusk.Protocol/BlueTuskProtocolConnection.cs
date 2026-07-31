using System.Buffers;
using System.Buffers.Binary;
using BlueTusk.Transport;

namespace BlueTusk.Protocol;

/// <summary>Owns transport buffering and PostgreSQL frame boundaries for one physical connection.</summary>
public sealed class BlueTuskProtocolConnection : IAsyncDisposable, IDisposable
{
    private const int InitialBufferSize = 16 * 1024;
    private const int InitialWriteBufferSize = 4 * 1024;
    private const int MaximumRetainedWriteBufferSize = 64 * 1024;
    private readonly IBlueTuskTransport _transport;
    private readonly BlueTuskBackendMessageParser _parser;
    private ArrayBufferWriter<byte> _writeBuffer = new(InitialWriteBufferSize);
    private byte[] _buffer;
    private int _start;
    private int _count;
    private int _activePayloadRemaining = -1;
    private int _writeInProgress;
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

    /// <summary>Gets the unread byte count for the active incrementally consumed message payload.</summary>
    public int ActiveMessagePayloadRemaining => Math.Max(_activePayloadRemaining, 0);

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
        EnsureNoActivePayload();
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
        EnsureNoActivePayload();
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

    /// <summary>Reads a backend frame header while leaving its payload on the transport.</summary>
    /// <remarks>
    /// The caller must consume the complete payload through <see cref="ReadMessagePayload(Span{byte})"/>
    /// or <see cref="ReadMessagePayloadAsync(Memory{byte}, CancellationToken)"/> before reading another message.
    /// </remarks>
    public BlueTuskBackendMessageHeader ReadMessageHeader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BeginNextMessage();
        EnsureBuffered(HeaderSize);
        return ConsumeHeader();
    }

    /// <summary>Asynchronously reads a backend frame header while leaving its payload on the transport.</summary>
    public async ValueTask<BlueTuskBackendMessageHeader> ReadMessageHeaderAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BeginNextMessage();
        await EnsureBufferedAsync(HeaderSize, cancellationToken).ConfigureAwait(false);
        return ConsumeHeader();
    }

    /// <summary>Reads the next portion of the active backend message payload.</summary>
    public int ReadMessagePayload(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureActivePayload();
        if (destination.IsEmpty || _activePayloadRemaining == 0)
        {
            return 0;
        }

        var requested = Math.Min(destination.Length, _activePayloadRemaining);
        var read = ReadPayloadBytes(destination[..requested]);
        _activePayloadRemaining -= read;
        return read;
    }

    /// <summary>Asynchronously reads the next portion of the active backend message payload.</summary>
    public async ValueTask<int> ReadMessagePayloadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureActivePayload();
        if (destination.IsEmpty || _activePayloadRemaining == 0)
        {
            return 0;
        }

        var requested = Math.Min(destination.Length, _activePayloadRemaining);
        var read = await ReadPayloadBytesAsync(
            destination[..requested],
            cancellationToken).ConfigureAwait(false);
        _activePayloadRemaining -= read;
        return read;
    }

    public async ValueTask WriteAsync(
        Action<IBufferWriter<byte>> writeMessage,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(writeMessage);

        var output = BeginWrite();
        try
        {
            writeMessage(output);
            await _transport.WriteAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndWrite();
        }
    }

    public void Write(Action<IBufferWriter<byte>> writeMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(writeMessage);

        var output = BeginWrite();
        try
        {
            writeMessage(output);
            _transport.Write(output.WrittenSpan);
            _transport.Flush();
        }
        finally
        {
            EndWrite();
        }
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

    private const int HeaderSize = 5;

    private void BeginNextMessage()
    {
        if (_activePayloadRemaining > 0)
        {
            throw new InvalidOperationException(
                $"The active backend message still has {_activePayloadRemaining} unread payload bytes.");
        }

        _activePayloadRemaining = -1;
    }

    private BlueTuskBackendMessageHeader ConsumeHeader()
    {
        var code = _buffer[_start];
        var length = BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(_start + 1, sizeof(int)));
        if (length < sizeof(int))
        {
            throw new BlueTuskProtocolException(
                $"Backend message '{(char)code}' declared invalid length {length}.");
        }

        if (length > _parser.MaximumMessageSize)
        {
            throw new BlueTuskProtocolException(
                $"Backend message '{(char)code}' declared length {length}, exceeding the configured maximum {_parser.MaximumMessageSize}.");
        }

        _start += HeaderSize;
        _count -= HeaderSize;
        _activePayloadRemaining = length - sizeof(int);
        return new BlueTuskBackendMessageHeader(code, _activePayloadRemaining);
    }

    private void EnsureBuffered(int minimumCount)
    {
        while (_count < minimumCount)
        {
            PrepareForRead();
            var read = _transport.Read(_buffer.AsSpan(_start + _count));
            if (read == 0)
            {
                throw new EndOfStreamException(
                    _count == 0
                        ? "PostgreSQL closed the connection."
                        : "PostgreSQL disconnected in the middle of a protocol message header.");
            }

            _count += read;
        }
    }

    private async ValueTask EnsureBufferedAsync(int minimumCount, CancellationToken cancellationToken)
    {
        while (_count < minimumCount)
        {
            PrepareForRead();
            var read = await _transport.ReadAsync(
                _buffer.AsMemory(_start + _count),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    _count == 0
                        ? "PostgreSQL closed the connection."
                        : "PostgreSQL disconnected in the middle of a protocol message header.");
            }

            _count += read;
        }
    }

    private int ReadPayloadBytes(Span<byte> destination)
    {
        if (_count != 0)
        {
            var copied = Math.Min(destination.Length, _count);
            _buffer.AsSpan(_start, copied).CopyTo(destination);
            _start += copied;
            _count -= copied;
            return copied;
        }

        var read = _transport.Read(destination);
        return read != 0
            ? read
            : throw new EndOfStreamException(
                "PostgreSQL disconnected in the middle of a protocol message payload.");
    }

    private async ValueTask<int> ReadPayloadBytesAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (_count != 0)
        {
            var copied = Math.Min(destination.Length, _count);
            _buffer.AsMemory(_start, copied).CopyTo(destination);
            _start += copied;
            _count -= copied;
            return copied;
        }

        var read = await _transport.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        return read != 0
            ? read
            : throw new EndOfStreamException(
                "PostgreSQL disconnected in the middle of a protocol message payload.");
    }

    private void EnsureNoActivePayload()
    {
        if (_activePayloadRemaining > 0)
        {
            throw new InvalidOperationException(
                "A streamed backend message is active; consume its payload before reading another message.");
        }

        _activePayloadRemaining = -1;
    }

    private void EnsureActivePayload()
    {
        if (_activePayloadRemaining < 0)
        {
            throw new InvalidOperationException("No streamed backend message payload is active.");
        }
    }

    private void ReturnBuffer()
    {
        var buffer = _buffer;
        _buffer = [];
        _start = 0;
        _count = 0;
        _activePayloadRemaining = -1;
        ArrayPool<byte>.Shared.Return(buffer);
    }

    private ArrayBufferWriter<byte> BeginWrite()
    {
        if (Interlocked.CompareExchange(ref _writeInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A protocol write is already active on this physical connection.");
        }

        return _writeBuffer;
    }

    private void EndWrite()
    {
        if (_writeBuffer.Capacity > MaximumRetainedWriteBufferSize)
        {
            _writeBuffer = new ArrayBufferWriter<byte>(InitialWriteBufferSize);
        }
        else
        {
            _writeBuffer.Clear();
        }

        Volatile.Write(ref _writeInProgress, 0);
    }
}
