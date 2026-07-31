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

    public void Dispose()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null && !_completed)
        {
            session.AbortCopyInOperation();
        }
    }
}

/// <summary>Reads a synchronous, streaming PostgreSQL COPY OUT operation.</summary>
public sealed class BlueTuskCopyOutOperation : IDisposable
{
    private BlueTuskSession? _session;
    private byte[]? _current;
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

internal enum BlueTuskCopyOutEventKind
{
    None,
    Data,
    CommandComplete,
    Error,
    Completed,
}
