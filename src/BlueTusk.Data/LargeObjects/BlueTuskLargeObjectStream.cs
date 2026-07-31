namespace BlueTusk.Data.LargeObjects;

/// <summary>Provides synchronous and asynchronous transactional access to a PostgreSQL large object.</summary>
public sealed class BlueTuskLargeObjectStream : Stream
{
    internal const int MaximumTransferSize = 1024 * 1024;
    private readonly IBlueTuskLargeObjectOperations _operations;
    private readonly FileAccess _access;
    private long _length;
    private long _position;
    private int _faulted;
    private int _disposed;

    internal BlueTuskLargeObjectStream(
        uint objectId,
        FileAccess access,
        long length,
        long position,
        IBlueTuskLargeObjectOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        ObjectId = objectId;
        _access = access;
        _length = length;
        _position = position;
        _operations = operations;
    }

    /// <summary>Gets the PostgreSQL object identifier backing this stream.</summary>
    public uint ObjectId { get; }

    public override bool CanRead => !IsDisposed && _access is FileAccess.Read or FileAccess.ReadWrite;

    public override bool CanSeek => !IsDisposed;

    public override bool CanWrite => !IsDisposed && _access is FileAccess.Write or FileAccess.ReadWrite;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set => _ = Seek(value, SeekOrigin.Begin);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public override void Flush()
    {
        ThrowIfDisposed();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfDisposed();
        if (!CanRead)
        {
            throw new NotSupportedException("The large object was not opened for reading.");
        }

        if (count == 0)
        {
            return 0;
        }

        try
        {
            var requested = Math.Min(count, MaximumTransferSize);
            var data = _operations.Read(requested);
            if (data.Length > requested)
            {
                throw new IOException(
                    $"PostgreSQL returned {data.Length} large-object bytes when {requested} were requested.");
            }

            data.CopyTo(buffer, offset);
            _position = checked(_position + data.Length);
            return data.Length;
        }
        catch
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!CanRead)
        {
            throw new NotSupportedException("The large object was not opened for reading.");
        }

        if (buffer.IsEmpty)
        {
            return 0;
        }

        try
        {
            var requested = Math.Min(buffer.Length, MaximumTransferSize);
            var data = await _operations.ReadAsync(requested, cancellationToken).ConfigureAwait(false);
            if (data.Length > requested)
            {
                throw new IOException(
                    $"PostgreSQL returned {data.Length} large-object bytes when {requested} were requested.");
            }

            data.CopyTo(buffer);
            _position = checked(_position + data.Length);
            return data.Length;
        }
        catch
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfDisposed();
        if (!CanWrite)
        {
            throw new NotSupportedException("The large object was not opened for writing.");
        }

        try
        {
            var remaining = buffer.AsSpan(offset, count);
            while (!remaining.IsEmpty)
            {
                var transferCount = Math.Min(remaining.Length, MaximumTransferSize);
                var written = _operations.Write(remaining[..transferCount]);
                if (written != transferCount)
                {
                    throw new IOException(
                        $"PostgreSQL wrote {written} large-object bytes when {transferCount} were supplied.");
                }

                _position = checked(_position + written);
                _length = Math.Max(_length, _position);
                remaining = remaining[transferCount..];
            }
        }
        catch
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw;
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!CanWrite)
        {
            throw new NotSupportedException("The large object was not opened for writing.");
        }

        try
        {
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(buffer.Length, MaximumTransferSize);
                var written = await _operations.WriteAsync(
                    buffer[..count],
                    cancellationToken).ConfigureAwait(false);
                if (written != count)
                {
                    throw new IOException(
                        $"PostgreSQL wrote {written} large-object bytes when {count} were supplied.");
                }

                _position = checked(_position + written);
                _length = Math.Max(_length, _position);
                buffer = buffer[count..];
            }
        }
        catch
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        if (origin is < SeekOrigin.Begin or > SeekOrigin.End)
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        try
        {
            var position = _operations.Seek(offset, origin);
            if (position < 0)
            {
                throw new IOException("PostgreSQL returned a negative large-object position.");
            }

            _position = position;
            return position;
        }
        catch
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw;
        }
    }

    /// <summary>Moves the large-object cursor asynchronously.</summary>
    public async ValueTask<long> SeekAsync(
        long offset,
        SeekOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (origin is < SeekOrigin.Begin or > SeekOrigin.End)
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        try
        {
            var position = await _operations.SeekAsync(
                offset,
                origin,
                cancellationToken).ConfigureAwait(false);
            if (position < 0)
            {
                throw new IOException("PostgreSQL returned a negative large-object position.");
            }

            _position = position;
            return position;
        }
        catch
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw;
        }
    }

    public override void SetLength(long value)
    {
        ThrowIfDisposed();
        if (!CanWrite)
        {
            throw new NotSupportedException("The large object was not opened for writing.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(value);
        try
        {
            _operations.SetLength(value);
            _length = value;
        }
        catch
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw;
        }
    }

    /// <summary>Changes the large-object length asynchronously.</summary>
    public async ValueTask SetLengthAsync(
        long value,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!CanWrite)
        {
            throw new NotSupportedException("The large object was not opened for writing.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(value);
        try
        {
            await _operations.SetLengthAsync(value, cancellationToken).ConfigureAwait(false);
            _length = value;
        }
        catch
        {
            Interlocked.Exchange(ref _faulted, 1);
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _operations.Close(commit: Volatile.Read(ref _faulted) == 0);
            }
            finally
            {
                base.Dispose(disposing);
            }

            return;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await _operations.CloseAsync(
                    commit: Volatile.Read(ref _faulted) == 0,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await base.DisposeAsync().ConfigureAwait(false);
                GC.SuppressFinalize(this);
            }
        }
        else
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
