using System.Buffers;
using System.Runtime.CompilerServices;
using BlueTusk.Protocol;

namespace BlueTusk.Client;

/// <summary>Writes a synchronous, streaming PostgreSQL COPY IN operation.</summary>
public sealed class BlueTuskCopyInOperation : IDisposable
{
    private BlueTuskSession? _session;
    private long _bytesTransferred;
    private bool _completed;

    internal BlueTuskCopyInOperation(
        BlueTuskSession session,
        BlueTuskCopyResponse response)
    {
        _session = session;
        Response = response;
    }

    public BlueTuskCopyResponse Response { get; }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        if (_completed)
        {
            throw new InvalidOperationException("The COPY IN operation is already complete.");
        }

        if (data.IsEmpty)
        {
            return;
        }

        _session.WriteCopyIn(data);
        _bytesTransferred = checked(_bytesTransferred + data.Length);
    }

    internal ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        if (_completed)
        {
            throw new InvalidOperationException("The COPY IN operation is already complete.");
        }

        if (data.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        var write = _session.WriteCopyInAsync(data, cancellationToken);
        if (write.IsCompletedSuccessfully)
        {
            _bytesTransferred = checked(_bytesTransferred + data.Length);
            return ValueTask.CompletedTask;
        }

        return AwaitWriteAsync(write, data.Length);
    }

    public BlueTuskCopyResult Complete()
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        if (_completed)
        {
            throw new InvalidOperationException("The COPY IN operation is already complete.");
        }

        _completed = true;
        return _session.CompleteCopyIn(Response, _bytesTransferred);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<BlueTuskCopyResult> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        if (_completed)
        {
            throw new InvalidOperationException("The COPY IN operation is already complete.");
        }

        _completed = true;
        return await _session.CompleteCopyInAsync(
            Response,
            _bytesTransferred,
            cancellationToken).ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal async ValueTask DisposeAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null && !_completed)
        {
            await session.AbortCopyInOperationAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null && !_completed)
        {
            session.AbortCopyInOperation();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AwaitWriteAsync(ValueTask write, int length)
    {
        await write.ConfigureAwait(false);
        _bytesTransferred = checked(_bytesTransferred + length);
    }
}

/// <summary>Reads a synchronous, streaming PostgreSQL COPY OUT operation.</summary>
public sealed class BlueTuskCopyOutOperation : IDisposable
{
    private BlueTuskSession? _session;
    private byte[]? _current;
    private ReadOnlySequence<byte> _currentSequence;
    private int _currentOffset;
    private string? _commandTag;
    private BlueTuskServerException? _deferredError;
    private long _bytesTransferred;
    private bool _completed;

    internal BlueTuskCopyOutOperation(
        BlueTuskSession session,
        BlueTuskCopyResponse response)
    {
        _session = session;
        Response = response;
    }

    public BlueTuskCopyResponse Response { get; }

    public BlueTuskCopyResult? Result { get; private set; }

    public int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        if (destination.IsEmpty || _completed)
        {
            return 0;
        }

        try
        {
            while (true)
            {
                if (_current is { } current && _currentOffset < current.Length)
                {
                    var count = Math.Min(destination.Length, current.Length - _currentOffset);
                    current.AsSpan(_currentOffset, count).CopyTo(destination);
                    _currentOffset += count;
                    _bytesTransferred = checked(_bytesTransferred + count);
                    return count;
                }

                _current = null;
                _currentOffset = 0;
                var copyEvent = _session.ReadCopyOutEvent();
                switch (copyEvent.Kind)
                {
                    case BlueTuskCopyOutEventKind.Data:
                        _current = copyEvent.Data;
                        break;
                    case BlueTuskCopyOutEventKind.CommandComplete:
                        _commandTag = copyEvent.CommandTag;
                        break;
                    case BlueTuskCopyOutEventKind.Error:
                        _deferredError = copyEvent.Error;
                        break;
                    case BlueTuskCopyOutEventKind.Completed:
                        _completed = true;
                        if (_deferredError is not null)
                        {
                            throw _deferredError;
                        }

                        Result = new BlueTuskCopyResult(
                            Response,
                            _commandTag ?? throw new BlueTuskProtocolException(
                                "COPY OUT completed without a command tag."),
                            _bytesTransferred);
                        return 0;
                    default:
                        break;
                }
            }
        }
        catch
        {
            _session.AbortCopyOutOperation();
            throw;
        }
    }

    internal ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        if (destination.IsEmpty || _completed)
        {
            return ValueTask.FromResult(0);
        }

        if (TryReadBuffered(destination, out var read))
        {
            return ValueTask.FromResult(read);
        }

        return ReadAsyncSlow(_session!, destination, cancellationToken);
    }

    internal bool TryReadExactly(Memory<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        if (_completed || _currentSequence.Length < destination.Length)
        {
            return false;
        }

        _currentSequence.Slice(0, destination.Length).CopyTo(destination.Span);
        _currentSequence = _currentSequence.Slice(destination.Length);
        _bytesTransferred = checked(_bytesTransferred + destination.Length);
        return true;
    }

    internal bool TryReadMemory(int length, out ReadOnlyMemory<byte> value)
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        if (_completed ||
            length < 0 ||
            _currentSequence.Length < length ||
            !_currentSequence.IsSingleSegment)
        {
            value = default;
            return false;
        }

        value = _currentSequence.First.Slice(0, length);
        _currentSequence = _currentSequence.Slice(length);
        _bytesTransferred = checked(_bytesTransferred + length);
        return true;
    }

    private bool TryReadBuffered(Memory<byte> destination, out int read)
    {
        if (_currentSequence.IsEmpty)
        {
            read = 0;
            return false;
        }

        read = (int)Math.Min(destination.Length, _currentSequence.Length);
        _currentSequence.Slice(0, read).CopyTo(destination.Span);
        _currentSequence = _currentSequence.Slice(read);
        _bytesTransferred = checked(_bytesTransferred + read);
        return true;
    }

    private ValueTask<int> ReadAsyncSlow(
        BlueTuskSession session,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        try
        {
            var pendingEvent = session.ReadCopyOutBufferEventAsync(cancellationToken);
            if (pendingEvent.IsCompletedSuccessfully)
            {
                return ProcessCopyOutBufferEvent(
                    session,
                    destination,
                    pendingEvent.Result,
                    cancellationToken);
            }

            return AwaitCopyOutBufferEventAsync(
                session,
                destination,
                pendingEvent,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return AbortCopyOutAndThrowAsync(session, exception);
        }
    }

    private ValueTask<int> ProcessCopyOutBufferEvent(
        BlueTuskSession session,
        Memory<byte> destination,
        BlueTuskCopyOutBufferEvent copyEvent,
        CancellationToken cancellationToken)
    {
        switch (copyEvent.Kind)
        {
            case BlueTuskCopyOutEventKind.Data:
                _currentSequence = copyEvent.Data;
                if (TryReadBuffered(destination, out var read))
                {
                    return ValueTask.FromResult(read);
                }

                break;
            case BlueTuskCopyOutEventKind.CommandComplete:
                _commandTag = copyEvent.CommandTag;
                break;
            case BlueTuskCopyOutEventKind.Error:
                _deferredError = copyEvent.Error;
                break;
            case BlueTuskCopyOutEventKind.Completed:
                _completed = true;
                if (_deferredError is not null)
                {
                    throw _deferredError;
                }

                Result = new BlueTuskCopyResult(
                    Response,
                    _commandTag ?? throw new BlueTuskProtocolException(
                        "COPY OUT completed without a command tag."),
                    _bytesTransferred);
                return ValueTask.FromResult(0);
            default:
                break;
        }

        return ReadAsyncSlow(session, destination, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> AwaitCopyOutBufferEventAsync(
        BlueTuskSession session,
        Memory<byte> destination,
        ValueTask<BlueTuskCopyOutBufferEvent> pendingEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            var copyEvent = await pendingEvent.ConfigureAwait(false);
            return await ProcessCopyOutBufferEvent(
                session,
                destination,
                copyEvent,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await session.AbortCopyOutOperationAsync().ConfigureAwait(false);
            throw;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<int> AbortCopyOutAndThrowAsync(
        BlueTuskSession session,
        Exception exception)
    {
        await session.AbortCopyOutOperationAsync().ConfigureAwait(false);
        throw exception;
    }
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal async ValueTask DisposeAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null && !_completed)
        {
            await session.AbortCopyOutOperationAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null && !_completed)
        {
            session.AbortCopyOutOperation();
        }
    }
}

internal readonly record struct BlueTuskCopyOutEvent(
    BlueTuskCopyOutEventKind Kind,
    byte[]? Data = null,
    string? CommandTag = null,
    BlueTuskServerException? Error = null);

internal readonly record struct BlueTuskCopyOutBufferEvent(
    BlueTuskCopyOutEventKind Kind,
    ReadOnlySequence<byte> Data = default,
    string? CommandTag = null,
    BlueTuskServerException? Error = null);

internal enum BlueTuskCopyOutEventKind
{
    None,
    Data,
    CommandComplete,
    Error,
    Completed,
}
