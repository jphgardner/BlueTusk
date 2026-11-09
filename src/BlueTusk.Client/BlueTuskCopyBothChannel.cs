using BlueTusk.Protocol;
using System.Runtime.ExceptionServices;

namespace BlueTusk.Client;

/// <summary>Provides duplex messaging over PostgreSQL's COPY BOTH protocol mode.</summary>
public sealed class BlueTuskCopyBothChannel : IAsyncDisposable
{
    private readonly BlueTuskSession _session;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly object _completionSync = new();
    private Task<string?>? _completion;
    private int _activeWrites;
    private bool _readyWhileWriting;
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

    internal bool MarkBackendCompleted()
    {
        lock (_completionSync)
        {
            Volatile.Write(ref _completed, 1);
            _readyWhileWriting |= _activeWrites != 0;
            return _readyWhileWriting;
        }
    }

    /// <summary>Reads the next backend COPY data payload, or null after COPY completes.</summary>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsCompleted)
        {
            return null;
        }

        BlueTuskCopyBothReadResult result = default;
        ExceptionDispatchInfo? failure = null;
        var terminal = false;
        var discard = false;
        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsCompleted) { return null; }
            try
            {
                result = await _session.ReadCopyBothAsync(this, cancellationToken).ConfigureAwait(false);
                terminal = result.IsCompleted;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                terminal = true;
            }

            if (terminal)
            {
                lock (_completionSync)
                {
                    if (result.CommandTag is not null) { CommandTag = result.CommandTag; }
                    Volatile.Write(ref _completed, 1);
                    // ReadyForQuery cannot qualify bytes still being sent in the
                    // opposite direction. Never append CopyDone to that frame.
                    discard = _readyWhileWriting || _activeWrites != 0;
                }
            }
        }
        finally { _readLock.Release(); }

        if (terminal)
        {
            if (discard) { await _session.DiscardCopyBothAsync(this).ConfigureAwait(false); }
            try { _ = await GetCompletion().ConfigureAwait(false); }
            catch when (failure is not null) { failure.Throw(); }
            failure?.Throw();
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
        var writing = false;
        try
        {
            lock (_completionSync)
            {
                ObjectDisposedException.ThrowIf(IsCompleted, this);
                _activeWrites++;
                writing = true;
            }
            await _session.WriteCopyBothAsync(this, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (writing) { lock (_completionSync) { _activeWrites--; } }
            _writeLock.Release();
        }
    }

    /// <summary>Ends frontend COPY and drains PostgreSQL through ReadyForQuery.</summary>
    public ValueTask<string?> CompleteAsync() => new(GetCompletion());

    private Task<string?> GetCompletion()
    {
        lock (_completionSync)
        {
            Volatile.Write(ref _completed, 1);
            return _completion ??= CompleteCoreAsync();
        }
    }

    private async Task<string?> CompleteCoreAsync()
    {
        // The deadline includes waiting for a writer, sending CopyDone, waiting
        // for the current reader, and draining through ReadyForQuery.
        using var deadline = new CancellationTokenSource(BlueTuskSession.CancellationRecoveryTimeout);
        var completion = FinishCoreAsync(deadline.Token);
        try
        {
            return await completion.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            _ = completion.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await _session.DiscardCopyBothAsync(this).ConfigureAwait(false);
            throw new TimeoutException("COPY BOTH completion exceeded its recovery deadline; the physical session was discarded.", exception);
        }
        catch
        {
            // A drained SQL error has already released ownership. All other
            // failures still owned by this channel discard the physical session.
            await _session.DiscardCopyBothAsync(this).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string?> FinishCoreAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Send before taking the read lock: an existing reader may be waiting
            // for the server to respond to frontend CopyDone.
            lock (_completionSync) { _activeWrites++; }
            try { await _session.SendCopyBothDoneAsync(this, cancellationToken).ConfigureAwait(false); }
            finally { lock (_completionSync) { _activeWrites--; } }
            await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var tag = await _session.CompleteCopyBothAsync(this, cancellationToken).ConfigureAwait(false);
                if (tag is not null) { CommandTag = tag; }
                return CommandTag;
            }
            finally { _readLock.Release(); }
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        Task<string?> completion;
        bool alreadyCompleted;
        lock (_completionSync)
        {
            alreadyCompleted = _completion?.IsCompleted == true;
            completion = GetCompletion();
        }
        try { _ = await completion.ConfigureAwait(false); }
        catch when (alreadyCompleted) { }
        // These semaphores never create wait handles. Keeping their managed
        // lifetime with the channel lets interrupted readers/writers unwind
        // without SemaphoreSlim.Dispose masking the original transport failure.
        GC.SuppressFinalize(this);
    }

    internal async ValueTask AbortAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        lock (_completionSync) { Volatile.Write(ref _completed, 1); }
        await _session.DiscardCopyBothAsync(this).ConfigureAwait(false);
        // Abort is used while propagating an existing replication failure.
        try { _ = await GetCompletion().ConfigureAwait(false); }
        catch { }
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
