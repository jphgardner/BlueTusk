using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BlueTusk.Transport;

namespace BlueTusk.Protocol;

/// <summary>Owns transport buffering and PostgreSQL frame boundaries for one physical connection.</summary>
public sealed class BlueTuskProtocolConnection : IAsyncDisposable, IDisposable
{
    private const int InitialBufferSize = 8 * 1024;
    private const int MaximumRetainedReadBufferSize = 64 * 1024;
    private const int MaximumPayloadReadAhead = 1024 * 1024;
    private const int DirectPayloadReadThreshold = 8 * 1024;
    private const int MinimumPayloadFillSize = 64 * 1024;
    private const int InitialWriteBufferSize = 4 * 1024;
    private const int MaximumRetainedWriteBufferSize = 64 * 1024;
    private readonly IBlueTuskTransport _transport;
    private readonly BlueTuskBackendMessageParser _parser;
    private ArrayBufferWriter<byte> _writeBuffer = new(InitialWriteBufferSize);
    private byte[] _buffer;
    private int _start;
    private int _count;
    private int _activePayloadRemaining = -1;
    private int _activeReads;
    private int _bufferReturned;
    private int _writeInProgress;
    private volatile bool _disposed;

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
        BeginRead();
        try
        {
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
        finally
        {
            EndRead();
        }
    }

    public byte ReadUnframedByte()
    {
        BeginRead();
        try
        {
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
        finally
        {
            EndRead();
        }
    }

    /// <remarks>The returned payload remains valid only until the next read from this connection.</remarks>
    public ValueTask<BlueTuskBackendMessage> ReadMessageAsync(
        CancellationToken cancellationToken)
    {
        if (TryBeginReadMessage(out var message, out var destination))
        {
            return new ValueTask<BlueTuskBackendMessage>(message);
        }

        return ReadMessageSlowAsync(destination, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<BlueTuskBackendMessage> ReadMessageSlowAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int read;
        try
        {
            read = await ReadTransportAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            AbortReadMessage();
            throw;
        }

        CompleteReadMessage(read);
        return await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
    }

    internal bool TryBeginReadMessage(
        out BlueTuskBackendMessage message,
        out Memory<byte> destination)
    {
        BeginRead();
        try
        {
            EnsureNoActivePayload();
            if (TryParseBufferedMessage(out message))
            {
                destination = default;
                EndRead();
                return true;
            }

            PrepareForRead();
            destination = _buffer.AsMemory(_start + _count);
            return false;
        }
        catch
        {
            EndRead();
            throw;
        }
    }

    internal ValueTask<int> ReadTransportAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken) =>
        _transport.ReadAsync(destination, cancellationToken);

    internal void CompleteReadMessage(int read)
    {
        try
        {
            if (read == 0)
            {
                throw new EndOfStreamException(
                    _count == 0
                        ? "PostgreSQL closed the connection."
                        : "PostgreSQL disconnected in the middle of a protocol message.");
            }

            _count += read;
        }
        finally
        {
            EndRead();
        }
    }

    internal void AbortReadMessage() => EndRead();

    internal void BeginPortalReadLease() => BeginRead();

    internal void EndPortalReadLease() => EndRead();

    /// <remarks>The returned payload remains valid only until the next read from this connection.</remarks>
    public BlueTuskBackendMessage ReadMessage()
    {
        BeginRead();
        try
        {
            EnsureNoActivePayload();
            while (true)
            {
                if (TryParseBufferedMessage(out var message))
                {
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
        finally
        {
            EndRead();
        }
    }

    /// <summary>Reads a backend frame header while leaving its payload on the transport.</summary>
    /// <remarks>
    /// The caller must consume the complete payload through <see cref="ReadMessagePayload(Span{byte})"/>
    /// or <see cref="ReadMessagePayloadAsync(Memory{byte}, CancellationToken)"/> before reading another message.
    /// </remarks>
    public BlueTuskBackendMessageHeader ReadMessageHeader()
    {
        BeginRead();
        try
        {
            BeginNextMessage();
            EnsureBuffered(HeaderSize);
            return ConsumeHeader();
        }
        finally
        {
            EndRead();
        }
    }

    internal bool TryReadBufferedDataRowHeader(out BlueTuskBackendMessageHeader header)
    {
        BeginNextMessage();
        if (_count < HeaderSize || _buffer[_start] != (byte)'D')
        {
            header = default;
            return false;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(
            _buffer.AsSpan(_start + 1, sizeof(int)));
        if (length < sizeof(int) + sizeof(short))
        {
            throw new BlueTuskProtocolException(
                $"Backend DataRow declared invalid length {length}.");
        }

        // The row object only needs the field count before returning control to
        // the caller. Requiring those bytes keeps the async API non-blocking.
        if (_count < HeaderSize + sizeof(short))
        {
            header = default;
            return false;
        }

        _start += HeaderSize;
        _count -= HeaderSize;
        _activePayloadRemaining = length - sizeof(int);
        header = new BlueTuskBackendMessageHeader((byte)'D', _activePayloadRemaining);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadBufferedDataRow(
        out ReadOnlyMemory<byte> payload,
        out BlueTuskBackendMessageHeader header)
    {
        BeginNextMessage();
        if (_count < HeaderSize || _buffer[_start] != (byte)'D')
        {
            payload = default;
            header = default;
            return false;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(
            _buffer.AsSpan(_start + 1, sizeof(int)));
        if (length < sizeof(int) + sizeof(short))
        {
            throw new BlueTuskProtocolException(
                $"Backend DataRow declared invalid length {length}.");
        }

        var payloadLength = length - sizeof(int);
        if (_count < HeaderSize + payloadLength)
        {
            payload = default;
            header = default;
            return false;
        }

        header = new BlueTuskBackendMessageHeader((byte)'D', payloadLength);
        payload = _buffer.AsMemory(_start + HeaderSize, payloadLength);
        var frameLength = HeaderSize + payloadLength;
        _start += frameLength;
        _count -= frameLength;
        _activePayloadRemaining = 0;
        return true;
    }

    internal bool TryBeginReadMessageHeader(
        out BlueTuskBackendMessageHeader header,
        out Memory<byte> destination)
    {
        BeginRead();
        try
        {
            BeginNextMessage();
            if (_count >= HeaderSize)
            {
                header = ConsumeHeader();
                destination = default;
                EndRead();
                return true;
            }

            PrepareForRead();
            header = default;
            destination = _buffer.AsMemory(_start + _count);
            return false;
        }
        catch
        {
            EndRead();
            throw;
        }
    }

    /// <summary>Asynchronously reads a backend frame header while leaving its payload on the transport.</summary>
    public ValueTask<BlueTuskBackendMessageHeader> ReadMessageHeaderAsync(
        CancellationToken cancellationToken)
    {
        BeginRead();
        try
        {
            BeginNextMessage();
            if (_count >= HeaderSize)
            {
                var header = ConsumeHeader();
                EndRead();
                return ValueTask.FromResult(header);
            }

            return ReadMessageHeaderSlowAsync(cancellationToken);
        }
        catch
        {
            EndRead();
            throw;
        }
    }

    private async ValueTask<BlueTuskBackendMessageHeader> ReadMessageHeaderSlowAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (_count < HeaderSize)
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

            return ConsumeHeader();
        }
        finally
        {
            EndRead();
        }
    }

    /// <summary>Reads the next portion of the active backend message payload.</summary>
    public int ReadMessagePayload(Span<byte> destination)
    {
        BeginRead();
        try
        {
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
        finally
        {
            EndRead();
        }
    }

    internal bool TryReadBufferedMessagePayloadExactly(Span<byte> destination)
    {
        BeginRead();
        try
        {
            EnsureActivePayload();
            if (destination.Length > _activePayloadRemaining)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(destination),
                    "The destination exceeds the active backend message payload.");
            }

            if (_count < destination.Length)
            {
                return false;
            }

            _buffer.AsSpan(_start, destination.Length).CopyTo(destination);
            _start += destination.Length;
            _count -= destination.Length;
            _activePayloadRemaining -= destination.Length;
            return true;
        }
        finally
        {
            EndRead();
        }
    }

    internal bool TryLeaseBufferedMessagePayload(
        int length,
        out ReadOnlyMemory<byte> payload)
    {
        BeginRead();
        try
        {
            EnsureActivePayload();
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (length > _activePayloadRemaining)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length),
                    "The requested lease exceeds the active backend message payload.");
            }

            if (_count < length)
            {
                payload = default;
                return false;
            }

            payload = _buffer.AsMemory(_start, length);
            _start += length;
            _count -= length;
            _activePayloadRemaining -= length;
            return true;
        }
        finally
        {
            EndRead();
        }
    }

    /// <summary>Asynchronously reads the next portion of the active backend message payload.</summary>
    public ValueTask<int> ReadMessagePayloadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        BeginRead();
        try
        {
            EnsureActivePayload();
            if (destination.IsEmpty || _activePayloadRemaining == 0)
            {
                EndRead();
                return ValueTask.FromResult(0);
            }

            var requested = Math.Min(destination.Length, _activePayloadRemaining);
            var pendingRead = ReadPayloadBytesAsync(destination[..requested], cancellationToken);
            if (!pendingRead.IsCompletedSuccessfully)
            {
                return AwaitMessagePayloadAsync(pendingRead);
            }

            var read = pendingRead.Result;
            _activePayloadRemaining -= read;
            EndRead();
            return ValueTask.FromResult(read);
        }
        catch
        {
            EndRead();
            throw;
        }
    }

    internal ValueTask<int> ReadLeasedMessagePayloadAsync<TState>(
        Memory<byte> destination,
        TState state,
        Func<TState, int, int> complete,
        CancellationToken cancellationToken)
    {
        EnsureActivePayload();
        if (destination.IsEmpty || _activePayloadRemaining == 0)
        {
            return ValueTask.FromResult(complete(state, 0));
        }

        var requested = Math.Min(destination.Length, _activePayloadRemaining);
        destination = destination[..requested];
        if (_count != 0)
        {
            var copied = CopyBufferedPayload(destination.Span);
            _activePayloadRemaining -= copied;
            if (destination.Length >= MinimumPayloadFillSize &&
                copied != destination.Length)
            {
                return FillPayloadReadAndCompleteAsync(
                    destination[copied..],
                    copied,
                    state,
                    complete,
                    cancellationToken);
            }

            return ValueTask.FromResult(complete(state, copied));
        }

        if (destination.Length >= DirectPayloadReadThreshold)
        {
            var pendingDirectRead = _transport.ReadAsync(destination, cancellationToken);
            if (!pendingDirectRead.IsCompletedSuccessfully)
            {
                return destination.Length >= MinimumPayloadFillSize
                    ? AwaitPayloadReadAndFillAndCompleteAsync(
                        pendingDirectRead,
                        destination,
                        state,
                        complete,
                        cancellationToken)
                    : AwaitPayloadReadAndCompleteAsync(pendingDirectRead, state, complete);
            }

            var directRead = ValidatePayloadRead(pendingDirectRead.Result);
            _activePayloadRemaining -= directRead;
            if (directRead == destination.Length ||
                destination.Length < MinimumPayloadFillSize)
            {
                return ValueTask.FromResult(complete(state, directRead));
            }

            return FillPayloadReadAndCompleteAsync(
                destination[directRead..],
                directRead,
                state,
                complete,
                cancellationToken);
        }

        PrepareForRead();
        GrowReadBufferForPayload();
        var readAheadLength = Math.Min(
            _buffer.Length - (_start + _count),
            MaximumPayloadReadAhead);
        if (destination.Length < readAheadLength)
        {
            var pendingReadAhead = _transport.ReadAsync(
                _buffer.AsMemory(_start + _count, readAheadLength),
                cancellationToken);
            if (!pendingReadAhead.IsCompletedSuccessfully)
            {
                return AwaitPayloadReadAheadAndCompleteAsync(
                    pendingReadAhead,
                    destination,
                    state,
                    complete);
            }

            var copied = CompletePayloadReadAhead(
                pendingReadAhead.Result,
                destination.Span);
            _activePayloadRemaining -= copied;
            return ValueTask.FromResult(complete(state, copied));
        }

        var pendingRead = _transport.ReadAsync(destination, cancellationToken);
        if (!pendingRead.IsCompletedSuccessfully)
        {
            return AwaitPayloadReadAndCompleteAsync(pendingRead, state, complete);
        }

        var read = ValidatePayloadRead(pendingRead.Result);
        _activePayloadRemaining -= read;
        return ValueTask.FromResult(complete(state, read));
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> AwaitMessagePayloadAsync(ValueTask<int> pendingRead)
    {
        try
        {
            var read = await pendingRead.ConfigureAwait(false);
            _activePayloadRemaining -= read;
            return read;
        }
        finally
        {
            EndRead();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> AwaitPayloadReadAndCompleteAsync<TState>(
        ValueTask<int> pendingRead,
        TState state,
        Func<TState, int, int> complete)
    {
        var read = ValidatePayloadRead(await pendingRead.ConfigureAwait(false));
        _activePayloadRemaining -= read;
        return complete(state, read);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> AwaitPayloadReadAndFillAndCompleteAsync<TState>(
        ValueTask<int> pendingRead,
        Memory<byte> destination,
        TState state,
        Func<TState, int, int> complete,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (true)
        {
            var read = ValidatePayloadRead(await pendingRead.ConfigureAwait(false));
            _activePayloadRemaining -= read;
            totalRead += read;
            if (totalRead == destination.Length)
            {
                return complete(state, totalRead);
            }

            pendingRead = _transport.ReadAsync(destination[totalRead..], cancellationToken);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> FillPayloadReadAndCompleteAsync<TState>(
        Memory<byte> remainingDestination,
        int totalRead,
        TState state,
        Func<TState, int, int> complete,
        CancellationToken cancellationToken)
    {
        while (!remainingDestination.IsEmpty)
        {
            var read = ValidatePayloadRead(
                await _transport.ReadAsync(
                    remainingDestination,
                    cancellationToken).ConfigureAwait(false));
            _activePayloadRemaining -= read;
            totalRead += read;
            remainingDestination = remainingDestination[read..];
        }

        return complete(state, totalRead);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> AwaitPayloadReadAheadAndCompleteAsync<TState>(
        ValueTask<int> pendingRead,
        Memory<byte> destination,
        TState state,
        Func<TState, int, int> complete)
    {
        var copied = CompletePayloadReadAhead(
            await pendingRead.ConfigureAwait(false),
            destination.Span);
        _activePayloadRemaining -= copied;
        return complete(state, copied);
    }

    public ValueTask WriteAsync(
        Action<IBufferWriter<byte>> writeMessage,
        CancellationToken cancellationToken) =>
        WriteCoreAsync(writeMessage, clearBuffer: false, cancellationToken);

    internal ValueTask WriteAsync<TState>(
        TState state,
        Action<IBufferWriter<byte>, TState> writeMessage,
        CancellationToken cancellationToken) =>
        WriteCoreAsync(state, writeMessage, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal async ValueTask WritePreEncodedAsync(
        ReadOnlyMemory<byte> messages,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (messages.IsEmpty)
        {
            throw new ArgumentException("At least one encoded message is required.", nameof(messages));
        }

        BeginDirectWrite();
        try
        {
            await _transport.WriteAsync(messages, cancellationToken).ConfigureAwait(false);
            await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndDirectWrite();
        }
    }

    /// <summary>Writes and flushes a message, then overwrites the reusable buffer that held it.</summary>
    public ValueTask WriteSensitiveAsync(
        Action<IBufferWriter<byte>> writeMessage,
        CancellationToken cancellationToken) =>
        WriteCoreAsync(writeMessage, clearBuffer: true, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteCoreAsync(
        Action<IBufferWriter<byte>> writeMessage,
        bool clearBuffer,
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
            EndWrite(clearBuffer);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteCoreAsync<TState>(
        TState state,
        Action<IBufferWriter<byte>, TState> writeMessage,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(writeMessage);

        var output = BeginWrite();
        try
        {
            writeMessage(output, state);
            await _transport.WriteAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndWrite(clearBuffer: false);
        }
    }

    public void Write(Action<IBufferWriter<byte>> writeMessage)
        => WriteCore(writeMessage, clearBuffer: false);

    internal void Write<TState>(
        TState state,
        Action<IBufferWriter<byte>, TState> writeMessage) =>
        WriteCore(state, writeMessage);

    internal void WritePreEncoded(ReadOnlySpan<byte> messages)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (messages.IsEmpty)
        {
            throw new ArgumentException("At least one encoded message is required.", nameof(messages));
        }

        BeginDirectWrite();
        try
        {
            _transport.Write(messages);
            _transport.Flush();
        }
        finally
        {
            EndDirectWrite();
        }
    }

    /// <summary>Writes and flushes a message, then overwrites the reusable buffer that held it.</summary>
    public void WriteSensitive(Action<IBufferWriter<byte>> writeMessage)
        => WriteCore(writeMessage, clearBuffer: true);

    private void WriteCore(Action<IBufferWriter<byte>> writeMessage, bool clearBuffer)
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
            EndWrite(clearBuffer);
        }
    }

    private void WriteCore<TState>(
        TState state,
        Action<IBufferWriter<byte>, TState> writeMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(writeMessage);

        var output = BeginWrite();
        try
        {
            writeMessage(output, state);
            _transport.Write(output.WrittenSpan);
            _transport.Flush();
        }
        finally
        {
            EndWrite(clearBuffer: false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.Dispose();
        ReturnBufferWhenIdle();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _transport.DisposeAsync().ConfigureAwait(false);
        ReturnBufferWhenIdle();
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BeginNextMessage()
    {
        if (_activePayloadRemaining > 0)
        {
            throw new InvalidOperationException(
                $"The active backend message still has {_activePayloadRemaining} unread payload bytes.");
        }

        _activePayloadRemaining = -1;
        ShrinkReadBufferIfPossible();
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

        if (destination.Length >= DirectPayloadReadThreshold)
        {
            return ValidatePayloadRead(_transport.Read(destination));
        }

        PrepareForRead();
        GrowReadBufferForPayload();
        var readAheadLength = Math.Min(
            _buffer.Length - (_start + _count),
            MaximumPayloadReadAhead);
        if (destination.Length < readAheadLength)
        {
            _count += ValidatePayloadRead(
                _transport.Read(_buffer.AsSpan(_start + _count, readAheadLength)));
            return CopyBufferedPayload(destination);
        }

        return ValidatePayloadRead(_transport.Read(destination));
    }

    private ValueTask<int> ReadPayloadBytesAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (_count != 0)
        {
            return ValueTask.FromResult(CopyBufferedPayload(destination.Span));
        }

        if (destination.Length >= DirectPayloadReadThreshold)
        {
            var pendingDirectRead = _transport.ReadAsync(destination, cancellationToken);
            return pendingDirectRead.IsCompletedSuccessfully
                ? ValueTask.FromResult(ValidatePayloadRead(pendingDirectRead.Result))
                : AwaitPayloadBytesAsync(pendingDirectRead);
        }

        PrepareForRead();
        GrowReadBufferForPayload();
        var readAheadLength = Math.Min(
            _buffer.Length - (_start + _count),
            MaximumPayloadReadAhead);
        if (destination.Length < readAheadLength)
        {
            var pendingReadAhead = _transport.ReadAsync(
                _buffer.AsMemory(_start + _count, readAheadLength),
                cancellationToken);
            return pendingReadAhead.IsCompletedSuccessfully
                ? ValueTask.FromResult(
                    CompletePayloadReadAhead(pendingReadAhead.Result, destination.Span))
                : AwaitPayloadReadAheadAsync(pendingReadAhead, destination);
        }

        var pendingRead = _transport.ReadAsync(destination, cancellationToken);
        return pendingRead.IsCompletedSuccessfully
            ? ValueTask.FromResult(ValidatePayloadRead(pendingRead.Result))
            : AwaitPayloadBytesAsync(pendingRead);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<int> AwaitPayloadBytesAsync(ValueTask<int> pendingRead) =>
        ValidatePayloadRead(await pendingRead.ConfigureAwait(false));

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> AwaitPayloadReadAheadAsync(
        ValueTask<int> pendingRead,
        Memory<byte> destination) =>
        CompletePayloadReadAhead(
            await pendingRead.ConfigureAwait(false),
            destination.Span);

    private int CompletePayloadReadAhead(int read, Span<byte> destination)
    {
        _count += ValidatePayloadRead(read);
        return CopyBufferedPayload(destination);
    }

    private int CopyBufferedPayload(Span<byte> destination)
    {
        var copied = Math.Min(destination.Length, _count);
        _buffer.AsSpan(_start, copied).CopyTo(destination);
        _start += copied;
        _count -= copied;
        return copied;
    }

    private static int ValidatePayloadRead(int read) =>
        read != 0
            ? read
            : throw new EndOfStreamException(
                "PostgreSQL disconnected in the middle of a protocol message payload.");

    private void GrowReadBufferForPayload()
    {
        var desiredReadAhead = Math.Min(_activePayloadRemaining, MaximumPayloadReadAhead);
        if (desiredReadAhead <= _buffer.Length - (_start + _count))
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(
            Math.Max(
                Math.Min(checked(_count + desiredReadAhead), MaximumPayloadReadAhead),
                InitialBufferSize));
        _buffer.AsSpan(_start, _count).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
        _start = 0;
    }

    private void ShrinkReadBufferIfPossible()
    {
        if (_buffer.Length <= MaximumRetainedReadBufferSize || _count > InitialBufferSize)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        _buffer.AsSpan(_start, _count).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
        _start = 0;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryParseBufferedMessage(out BlueTuskBackendMessage message)
    {
        if (_count < HeaderSize)
        {
            message = default;
            return false;
        }

        var code = _buffer[_start];
        var length = BinaryPrimitives.ReadInt32BigEndian(
            _buffer.AsSpan(_start + 1, sizeof(int)));
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

        var frameLength = length + 1;
        if (_count < frameLength)
        {
            message = default;
            return false;
        }

        var payloadLength = length - sizeof(int);
        message = new BlueTuskBackendMessage(
            code,
            new ReadOnlySequence<byte>(_buffer.AsMemory(_start + HeaderSize, payloadLength)));
        _start += frameLength;
        _count -= frameLength;
        return true;
    }

    private void EnsureActivePayload()
    {
        if (_activePayloadRemaining < 0)
        {
            throw new InvalidOperationException("No streamed backend message payload is active.");
        }
    }

    private void BeginRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = Interlocked.Increment(ref _activeReads);
        if (_disposed)
        {
            EndRead();
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    private void EndRead()
    {
        if (Interlocked.Decrement(ref _activeReads) == 0 && _disposed)
        {
            ReturnBufferWhenIdle();
        }
    }

    private void ReturnBufferWhenIdle()
    {
        if (Volatile.Read(ref _activeReads) != 0 ||
            Interlocked.Exchange(ref _bufferReturned, 1) != 0)
        {
            return;
        }

        var buffer = _buffer;
        _buffer = [];
        _start = 0;
        _count = 0;
        _activePayloadRemaining = -1;
        if (buffer.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

    private void BeginDirectWrite()
    {
        if (Interlocked.CompareExchange(ref _writeInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A protocol write is already active on this physical connection.");
        }
    }

    private void EndDirectWrite() => Volatile.Write(ref _writeInProgress, 0);

    private void EndWrite(bool clearBuffer)
    {
        if (clearBuffer
            && MemoryMarshal.TryGetArray(_writeBuffer.WrittenMemory, out var segment))
        {
            CryptographicOperations.ZeroMemory(segment.AsSpan());
        }

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
