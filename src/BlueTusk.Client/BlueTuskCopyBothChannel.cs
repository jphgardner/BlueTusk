using BlueTusk.Protocol;

namespace BlueTusk.Client;

/// <summary>Provides duplex messaging over PostgreSQL's COPY BOTH protocol mode.</summary>
public sealed class BlueTuskCopyBothChannel : IAsyncDisposable
{
    private readonly BlueTuskSession _session;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _completed;
    private int _disposed;

    internal BlueTuskCopyBothChannel(
        BlueTuskSession session,
        BlueTuskCopyResponse response)
    {
        _session = session;
        Response = response;
    }

    public BlueTuskCopyResponse Response { get; }

    public string? CommandTag { get; private set; }

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    /// <summary>Reads the next backend COPY data payload, or null after COPY completes.</summary>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsCompleted)
        {
            return null;
        }

        var result = await _session.ReadCopyBothAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsCompleted)
        {
            CommandTag = result.CommandTag;
            Interlocked.Exchange(ref _completed, 1);
            return null;
        }

        return result.Data;
    }

    /// <summary>Writes one frontend COPY data payload.</summary>
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsCompleted, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsCompleted, this);
            await _session.WriteCopyBothAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Ends frontend COPY and drains PostgreSQL through ReadyForQuery.</summary>
    public async ValueTask<string?> CompleteAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return CommandTag;
        }

        await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            CommandTag = await _session.CompleteCopyBothAsync().ConfigureAwait(false);
            return CommandTag;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _ = await CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    internal async ValueTask AbortAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _completed, 1);
        await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _writeLock.Release();
        _writeLock.Dispose();
    }
}

internal readonly record struct BlueTuskCopyBothReadResult(
    ReadOnlyMemory<byte> Data,
    bool IsCompleted,
    string? CommandTag)
{
    public static BlueTuskCopyBothReadResult Payload(byte[] data) =>
        new(data, IsCompleted: false, CommandTag: null);

    public static BlueTuskCopyBothReadResult Completed(string? commandTag) =>
        new(ReadOnlyMemory<byte>.Empty, IsCompleted: true, commandTag);
}
