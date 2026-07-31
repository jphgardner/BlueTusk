using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Protocol;

namespace BlueTusk.Client;

/// <summary>Reads rows incrementally from a bounded named PostgreSQL portal.</summary>
public sealed class BlueTuskPortal : IDisposable, IAsyncDisposable
{
    private BlueTuskSession? _session;
    private BlueTuskPortalRow? _currentRow;
    private bool _completed;

    internal BlueTuskPortal(
        BlueTuskSession session,
        string name,
        IReadOnlyList<BlueTuskFieldDescription> fields,
        int fetchSize,
        long startedTimestamp)
    {
        _session = session;
        Name = name;
        Fields = fields;
        FetchSize = fetchSize;
        StartedTimestamp = startedTimestamp;
    }

    public string Name { get; }

    public IReadOnlyList<BlueTuskFieldDescription> Fields { get; }

    public int FetchSize { get; }

    public string? CommandTag { get; private set; }

    public long RowsRead { get; private set; }

    public bool IsCompleted => _completed;

    internal long StartedTimestamp { get; }

    public BlueTuskPortalRow? Read()
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        if (_completed)
        {
            return null;
        }

        try
        {
            _currentRow?.Finish();
            _currentRow = _session.ReadPortalRow(this);
            if (_currentRow is not null)
            {
                RowsRead++;
            }

            return _currentRow;
        }
        catch
        {
            _session.AbortPortal(this);
            throw;
        }
    }

    public async ValueTask<BlueTuskPortalRow?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        if (_completed)
        {
            return null;
        }

        try
        {
            if (_currentRow is not null)
            {
                await _currentRow.FinishAsync(cancellationToken).ConfigureAwait(false);
            }

            _currentRow = await _session.ReadPortalRowAsync(this, cancellationToken).ConfigureAwait(false);
            if (_currentRow is not null)
            {
                RowsRead++;
            }

            return _currentRow;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _session.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await _session.AbortPortalAsync(this).ConfigureAwait(false);
            }

            throw;
        }
        catch
        {
            await _session.AbortPortalAsync(this).ConfigureAwait(false);
            throw;
        }
    }

    internal void SetCommandTag(string commandTag) => CommandTag = commandTag;

    internal void SetCompleted() => _completed = true;

    public void Dispose()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        try
        {
            _currentRow?.Finish();
        }
        finally
        {
            if (!_completed)
            {
                session.AbortPortal(this);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        try
        {
            if (_currentRow is not null)
            {
                await _currentRow.FinishAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!_completed)
            {
                await session.AbortPortalAsync(this).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Provides forward-only access to the fields in one streamed PostgreSQL row.</summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The active field stream is a view owned and completed by the containing portal row lifecycle.")]
[SuppressMessage(
    "Usage",
    "CA2201:Do not raise reserved exception types",
    Justification = "Forward-only data readers conventionally use IndexOutOfRangeException for invalid ordinals.")]
public sealed class BlueTuskPortalRow
{
    private readonly BlueTuskSession _session;
    private readonly BlueTuskPortal _portal;
    private readonly int _payloadLength;
    private int _payloadConsumed;
    private int _activeOrdinal = -1;
    private int _activeLength = -2;
    private int _activePosition;
    private byte[]? _cachedValue;
    private BlueTuskPortalFieldStream? _activeStream;
    private bool _finished;

    internal BlueTuskPortalRow(
        BlueTuskSession session,
        BlueTuskPortal portal,
        int payloadLength,
        int expectedFieldCount)
    {
        _session = session;
        _portal = portal;
        _payloadLength = payloadLength;
        Span<byte> countBytes = stackalloc byte[sizeof(short)];
        ReadExactly(countBytes);
        FieldCount = BinaryPrimitives.ReadInt16BigEndian(countBytes);
        ValidateFieldCount(expectedFieldCount);
    }

    private BlueTuskPortalRow(
        BlueTuskSession session,
        BlueTuskPortal portal,
        int payloadLength)
    {
        _session = session;
        _portal = portal;
        _payloadLength = payloadLength;
    }

    public int FieldCount { get; private set; }

    internal static async ValueTask<BlueTuskPortalRow> CreateAsync(
        BlueTuskSession session,
        BlueTuskPortal portal,
        int payloadLength,
        int expectedFieldCount,
        CancellationToken cancellationToken)
    {
        var row = new BlueTuskPortalRow(session, portal, payloadLength);
        var countBytes = new byte[sizeof(short)];
        await row.ReadExactlyAsync(countBytes, cancellationToken).ConfigureAwait(false);
        row.FieldCount = BinaryPrimitives.ReadInt16BigEndian(countBytes);
        row.ValidateFieldCount(expectedFieldCount);
        return row;
    }

    public bool IsDBNull(int ordinal)
    {
        MoveToField(ordinal);
        return _activeLength == -1;
    }

    public int GetFieldLength(int ordinal)
    {
        MoveToField(ordinal);
        return _activeLength == -1
            ? throw new InvalidCastException("A database NULL does not have a field length.")
            : _activeLength;
    }

    public async ValueTask<bool> IsDBNullAsync(
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        await MoveToFieldAsync(ordinal, cancellationToken).ConfigureAwait(false);
        return _activeLength == -1;
    }

    public async ValueTask<int> GetFieldLengthAsync(
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        await MoveToFieldAsync(ordinal, cancellationToken).ConfigureAwait(false);
        return _activeLength == -1
            ? throw new InvalidCastException("A database NULL does not have a field length.")
            : _activeLength;
    }

    public ReadOnlyMemory<byte>? ReadField(int ordinal)
    {
        MoveToField(ordinal);
        if (_activeLength == -1)
        {
            return null;
        }

        if (_cachedValue is not null)
        {
            return _cachedValue;
        }

        if (_activePosition != 0)
        {
            throw new InvalidOperationException(
                "A partially consumed sequential field cannot be materialized from its beginning.");
        }

        _cachedValue = GC.AllocateUninitializedArray<byte>(_activeLength);
        ReadExactly(_cachedValue);
        _activePosition = _activeLength;
        return _cachedValue;
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadFieldAsync(
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        await MoveToFieldAsync(ordinal, cancellationToken).ConfigureAwait(false);
        if (_activeLength == -1)
        {
            return null;
        }

        if (_cachedValue is not null)
        {
            return _cachedValue;
        }

        if (_activePosition != 0)
        {
            throw new InvalidOperationException(
                "A partially consumed sequential field cannot be materialized from its beginning.");
        }

        _cachedValue = GC.AllocateUninitializedArray<byte>(_activeLength);
        await ReadExactlyAsync(_cachedValue, cancellationToken).ConfigureAwait(false);
        _activePosition = _activeLength;
        return _cachedValue;
    }

    public long ReadBytes(
        int ordinal,
        long dataOffset,
        Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        MoveToField(ordinal);
        if (_activeLength == -1)
        {
            throw new InvalidCastException("A database NULL cannot be read as bytes.");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(dataOffset, _activeLength);
        if (dataOffset < _activePosition)
        {
            throw new InvalidOperationException(
                "Sequential field offsets must not move backwards.");
        }

        Skip(checked((int)dataOffset - _activePosition));
        _activePosition = checked((int)dataOffset);
        var count = Math.Min(destination.Length, _activeLength - _activePosition);
        ReadExactly(destination[..count]);
        _activePosition += count;
        return count;
    }

    public Stream OpenFieldStream(int ordinal)
    {
        MoveToField(ordinal);
        if (_activeLength == -1)
        {
            throw new InvalidCastException("A database NULL cannot be read as a stream.");
        }

        if (_activePosition != 0 || _cachedValue is not null)
        {
            throw new InvalidOperationException("The sequential field has already been consumed.");
        }

        _activeStream = new BlueTuskPortalFieldStream(this, _activeLength);
        return _activeStream;
    }

    internal void Finish()
    {
        if (_finished)
        {
            return;
        }

        _activeStream?.CompleteFromOwner();
        _activeStream = null;
        while (_activeOrdinal + 1 < FieldCount)
        {
            MoveToField(_activeOrdinal + 1);
        }

        SkipActiveField();
        EnsurePayloadConsumed();
        _finished = true;
    }

    internal async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return;
        }

        if (_activeStream is not null)
        {
            await _activeStream.CompleteFromOwnerAsync(cancellationToken).ConfigureAwait(false);
            _activeStream = null;
        }

        while (_activeOrdinal + 1 < FieldCount)
        {
            await MoveToFieldAsync(_activeOrdinal + 1, cancellationToken).ConfigureAwait(false);
        }

        await SkipActiveFieldAsync(cancellationToken).ConfigureAwait(false);
        EnsurePayloadConsumed();
        _finished = true;
    }

    internal int ReadActiveField(Span<byte> destination)
    {
        var count = Math.Min(destination.Length, _activeLength - _activePosition);
        ReadExactly(destination[..count]);
        _activePosition += count;
        return count;
    }

    internal async ValueTask<int> ReadActiveFieldAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var count = Math.Min(destination.Length, _activeLength - _activePosition);
        await ReadExactlyAsync(destination[..count], cancellationToken).ConfigureAwait(false);
        _activePosition += count;
        return count;
    }

    internal void CompleteActiveStream(BlueTuskPortalFieldStream stream)
    {
        if (!ReferenceEquals(_activeStream, stream))
        {
            return;
        }

        SkipActiveField();
        _activeStream = null;
    }

    internal async ValueTask CompleteActiveStreamAsync(
        BlueTuskPortalFieldStream stream,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_activeStream, stream))
        {
            return;
        }

        await SkipActiveFieldAsync(cancellationToken).ConfigureAwait(false);
        _activeStream = null;
    }

    private void MoveToField(int ordinal)
    {
        ValidateOrdinal(ordinal);
        EnsureNoActiveStream();
        if (ordinal < _activeOrdinal)
        {
            throw new InvalidOperationException("Sequential fields must be accessed in ordinal order.");
        }

        while (_activeOrdinal < ordinal)
        {
            SkipActiveField();
            ReadFieldHeader();
        }
    }

    private async ValueTask MoveToFieldAsync(int ordinal, CancellationToken cancellationToken)
    {
        ValidateOrdinal(ordinal);
        EnsureNoActiveStream();
        if (ordinal < _activeOrdinal)
        {
            throw new InvalidOperationException("Sequential fields must be accessed in ordinal order.");
        }

        while (_activeOrdinal < ordinal)
        {
            await SkipActiveFieldAsync(cancellationToken).ConfigureAwait(false);
            await ReadFieldHeaderAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReadFieldHeader()
    {
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        ReadExactly(lengthBytes);
        SetActiveField(BinaryPrimitives.ReadInt32BigEndian(lengthBytes));
    }

    private async ValueTask ReadFieldHeaderAsync(CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        SetActiveField(BinaryPrimitives.ReadInt32BigEndian(lengthBytes));
    }

    private void SetActiveField(int length)
    {
        if (length < -1)
        {
            throw new BlueTuskProtocolException("DataRow declared an invalid negative field length.");
        }

        _activeOrdinal++;
        _activeLength = length;
        _activePosition = 0;
        _cachedValue = null;
    }

    private void SkipActiveField()
    {
        if (_activeLength >= 0)
        {
            Skip(_activeLength - _activePosition);
            _activePosition = _activeLength;
        }
    }

    private async ValueTask SkipActiveFieldAsync(CancellationToken cancellationToken)
    {
        if (_activeLength >= 0)
        {
            await SkipAsync(_activeLength - _activePosition, cancellationToken).ConfigureAwait(false);
            _activePosition = _activeLength;
        }
    }

    private void Skip(int count)
    {
        Span<byte> scratch = stackalloc byte[4096];
        while (count > 0)
        {
            var chunk = Math.Min(count, scratch.Length);
            ReadExactly(scratch[..chunk]);
            count -= chunk;
        }
    }

    private async ValueTask SkipAsync(int count, CancellationToken cancellationToken)
    {
        var scratch = new byte[Math.Min(count, 4096)];
        while (count > 0)
        {
            var chunk = Math.Min(count, scratch.Length);
            await ReadExactlyAsync(scratch.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            count -= chunk;
        }
    }

    private void ReadExactly(Span<byte> destination)
    {
        EnsurePayloadAvailable(destination.Length);
        _session.ReadPortalPayloadExactly(destination);
        _payloadConsumed += destination.Length;
    }

    private async ValueTask ReadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        EnsurePayloadAvailable(destination.Length);
        await _session.ReadPortalPayloadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
        _payloadConsumed += destination.Length;
    }

    private void EnsurePayloadAvailable(int count)
    {
        if (count < 0 || count > _payloadLength - _payloadConsumed)
        {
            throw new BlueTuskProtocolException("DataRow field lengths exceed the message payload.");
        }
    }

    private void EnsurePayloadConsumed()
    {
        if (_payloadConsumed != _payloadLength)
        {
            throw new BlueTuskProtocolException(
                $"DataRow left {_payloadLength - _payloadConsumed} unexpected payload bytes.");
        }
    }

    private void ValidateFieldCount(int expectedFieldCount)
    {
        if (FieldCount < 0 || FieldCount != expectedFieldCount)
        {
            throw new BlueTuskProtocolException(
                "DataRow field count does not match its row description.");
        }
    }

    private void ValidateOrdinal(int ordinal)
    {
        if ((uint)ordinal >= (uint)FieldCount)
        {
            throw new IndexOutOfRangeException(
                $"Column ordinal {ordinal} is outside the streamed row.");
        }
    }

    private void EnsureNoActiveStream()
    {
        if (_activeStream is not null)
        {
            throw new InvalidOperationException(
                "The active field stream must be consumed or disposed before accessing another field.");
        }
    }
}

internal sealed class BlueTuskPortalFieldStream : Stream
{
    private readonly BlueTuskPortalRow _row;
    private readonly int _length;
    private bool _disposed;
    private int _remaining;

    public BlueTuskPortalFieldStream(BlueTuskPortalRow row, int length)
    {
        _row = row;
        _length = length;
        _remaining = length;
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _length - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_remaining == 0 || count == 0)
        {
            return 0;
        }

        var read = _row.ReadActiveField(buffer.AsSpan(offset, Math.Min(count, _remaining)));
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_remaining == 0 || buffer.IsEmpty)
        {
            return 0;
        }

        var read = await _row.ReadActiveFieldAsync(
            buffer[..Math.Min(buffer.Length, _remaining)],
            cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    internal void CompleteFromOwner()
    {
        if (!_disposed)
        {
            _disposed = true;
            _row.CompleteActiveStream(this);
            _remaining = 0;
        }
    }

    internal async ValueTask CompleteFromOwnerAsync(CancellationToken cancellationToken)
    {
        if (!_disposed)
        {
            _disposed = true;
            await _row.CompleteActiveStreamAsync(this, cancellationToken).ConfigureAwait(false);
            _remaining = 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteFromOwner();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CompleteFromOwnerAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
