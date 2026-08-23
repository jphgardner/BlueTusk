using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
        bool syncSent,
        long startedTimestamp)
    {
        _session = session;
        Name = name;
        Fields = fields;
        FieldCount = fields.Count;
        FetchSize = fetchSize;
        SyncSent = syncSent;
        StartedTimestamp = startedTimestamp;
    }

    public string Name { get; }

    public IReadOnlyList<BlueTuskFieldDescription> Fields { get; }

    internal int FieldCount { get; }

    public int FetchSize { get; }

    internal bool SyncSent { get; }

    public string? CommandTag { get; private set; }

    public long RowsRead { get; private set; }

    public bool IsCompleted => _completed;

    internal long StartedTimestamp { get; }

    internal BlueTuskPortalRow? CurrentRow => _currentRow;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadBuffered(out BlueTuskPortalRow? row)
    {
        if (_completed)
        {
            row = null;
            return true;
        }

        if (_currentRow is not null && !_currentRow.TryFinishSynchronously())
        {
            row = null;
            return false;
        }

        if (!_session!.TryReadBufferedPortalRow(this, out row))
        {
            return false;
        }

        _currentRow = row;
        RowsRead++;
        return true;
    }

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

    public ValueTask<BlueTuskPortalRow?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_session is null, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_completed)
        {
            return ValueTask.FromResult<BlueTuskPortalRow?>(null);
        }

        try
        {
            if (_currentRow is not null)
            {
                var finish = _currentRow.FinishAsync(cancellationToken);
                if (!finish.IsCompletedSuccessfully)
                {
                    return FinishAndReadAsync(finish, cancellationToken);
                }
            }

            if (_session.TryReadBufferedPortalRow(this, out var bufferedRow))
            {
                _currentRow = bufferedRow;
                RowsRead++;
                return ValueTask.FromResult(_currentRow);
            }

            return _session.ReadPortalRowAsync(this, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancelAndAbortAsync(cancellationToken);
        }
        catch
        {
            _session.AbortPortal(this);
            throw;
        }
    }

    private async ValueTask<BlueTuskPortalRow?> FinishAndReadAsync(
        ValueTask finish,
        CancellationToken cancellationToken)
    {
        try
        {
            await finish.ConfigureAwait(false);
            return await _session!.ReadPortalRowAsync(this, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CancelAndAbortAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _session!.AbortPortalAsync(this).ConfigureAwait(false);
            throw;
        }
    }

    internal void SetAsyncReadResult(BlueTuskPortalRow? row)
    {
        _currentRow = row;
        if (row is not null)
        {
            RowsRead++;
        }
    }

    private async ValueTask<BlueTuskPortalRow?> CancelAndAbortAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _session!.CancelAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _session!.AbortPortalAsync(this).ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
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

        var row = _currentRow;
        _currentRow = null;
        try
        {
            row?.Finish();
        }
        finally
        {
            if (!_completed)
            {
                session.AbortPortal(this);
            }
        }

        if (row is not null)
        {
            session.ReturnPortalRow(row);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        var row = _currentRow;
        _currentRow = null;
        try
        {
            if (row is not null)
            {
                await row.FinishAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!_completed)
            {
                await session.AbortPortalAsync(this).ConfigureAwait(false);
            }
        }

        if (row is not null)
        {
            session.ReturnPortalRow(row);
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
    private const int InlinePayloadCapacity = 64;
    private readonly BlueTuskSession _session;
    private BlueTuskPortal _portal;
    private readonly byte[] _inlinePayload = new byte[InlinePayloadCapacity];
    private int _payloadLength;
    private bool _payloadBuffered;
    private ReadOnlyMemory<byte> _bufferedPayload;
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
        Reset(payloadLength);
        Span<byte> countBytes = stackalloc byte[sizeof(short)];
        ReadExactly(countBytes);
        FieldCount = BinaryPrimitives.ReadInt16BigEndian(countBytes);
        ValidateFieldCount(expectedFieldCount);
    }

    internal BlueTuskPortalRow(
        BlueTuskSession session,
        BlueTuskPortal portal)
    {
        _session = session;
        _portal = portal;
    }

    public int FieldCount { get; private set; }

    internal static async ValueTask<BlueTuskPortalRow> CreateAsync(
        BlueTuskSession session,
        BlueTuskPortal portal,
        BlueTuskPortalRow? reusableRow,
        int payloadLength,
        int expectedFieldCount,
        CancellationToken cancellationToken)
    {
        var row = reusableRow ?? new BlueTuskPortalRow(session, portal);
        row.Reset(payloadLength);
        await row.ReadExactlyAsync(
            row._inlinePayload.AsMemory(0, sizeof(short)),
            cancellationToken).ConfigureAwait(false);
        row.FieldCount = BinaryPrimitives.ReadInt16BigEndian(row._inlinePayload);
        row.ValidateFieldCount(expectedFieldCount);
        return row;
    }

    internal void Reset(int payloadLength, int expectedFieldCount)
    {
        Reset(payloadLength);
        Span<byte> countBytes = stackalloc byte[sizeof(short)];
        ReadExactly(countBytes);
        FieldCount = BinaryPrimitives.ReadInt16BigEndian(countBytes);
        ValidateFieldCount(expectedFieldCount);
    }

    internal void Rebind(BlueTuskPortal portal) => _portal = portal;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResetBuffered(
        ReadOnlyMemory<byte> payload,
        int expectedFieldCount)
    {
        ResetState(payload.Length, payloadBuffered: true);
        _bufferedPayload = payload;
        EnsurePayloadAvailable(sizeof(short));
        FieldCount = BinaryPrimitives.ReadInt16BigEndian(payload.Span);
        _payloadConsumed = sizeof(short);
        ValidateFieldCount(expectedFieldCount);
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

    internal bool ReadFieldExactly(int ordinal, Span<byte> destination)
    {
        MoveToField(ordinal);
        if (_activeLength == -1)
        {
            return false;
        }

        if (_cachedValue is not null || _activePosition != 0 || _activeLength != destination.Length)
        {
            return false;
        }

        ReadExactly(destination);
        _activePosition = _activeLength;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadInt32(int ordinal, out int value)
    {
        if (_activeStream is not null)
        {
            EnsureNoActiveStream();
        }

        if (!_payloadBuffered || ordinal != _activeOrdinal + 1 ||
            _payloadLength - _payloadConsumed < (sizeof(int) * 2))
        {
            value = default;
            return false;
        }

        var payload = _bufferedPayload.Span;
        var payloadOffset = _payloadConsumed;
        var encodedField = BinaryPrimitives.ReadInt64BigEndian(
            payload.Slice(payloadOffset, sizeof(long)));
        if ((int)(encodedField >> 32) != sizeof(int))
        {
            value = default;
            return false;
        }

        value = unchecked((int)encodedField);
        _payloadConsumed += sizeof(int) * 2;
        _activeOrdinal++;
        _activeLength = sizeof(int);
        _activePosition = sizeof(int);
        _cachedValue = null;
        return true;
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

    internal ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return ValueTask.CompletedTask;
        }

        if (_activeStream is null && _payloadConsumed == _payloadLength)
        {
            _finished = true;
            return ValueTask.CompletedTask;
        }

        return FinishSlowAsync(cancellationToken);
    }

    internal bool TryFinishSynchronously()
    {
        if (_finished)
        {
            return true;
        }

        if (_activeStream is not null || _payloadConsumed != _payloadLength)
        {
            return false;
        }

        _finished = true;
        return true;
    }

    private async ValueTask FinishSlowAsync(CancellationToken cancellationToken)
    {

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

    internal ValueTask<int> ReadActiveFieldAsync(
        Memory<byte> destination,
        BlueTuskPortalFieldStream stream,
        CancellationToken cancellationToken)
    {
        var count = Math.Min(destination.Length, _activeLength - _activePosition);
        return _session.ReadPortalPayloadAsync(
            destination[..count],
            new ActiveFieldReadState(this, stream),
            static (state, read) => state.Row.CompleteActiveFieldRead(read, state.Stream),
            cancellationToken);
    }

    private int CompleteActiveFieldRead(int read, BlueTuskPortalFieldStream stream)
    {
        if (read == 0)
        {
            throw new BlueTuskProtocolException("A backend message payload ended unexpectedly.");
        }

        _payloadConsumed += read;
        _activePosition += read;
        stream.Advance(read);
        return read;
    }

    private readonly record struct ActiveFieldReadState(
        BlueTuskPortalRow Row,
        BlueTuskPortalFieldStream Stream);

    internal void CompleteActiveStream(BlueTuskPortalFieldStream stream)
    {
        if (!ReferenceEquals(_activeStream, stream))
        {
            return;
        }

        SkipActiveField();
        _activeStream = null;
    }

    internal ValueTask CompleteActiveStreamAsync(
        BlueTuskPortalFieldStream stream,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_activeStream, stream))
        {
            return ValueTask.CompletedTask;
        }

        if (_activePosition == _activeLength)
        {
            _activeStream = null;
            return ValueTask.CompletedTask;
        }

        return CompleteActiveStreamSlowAsync(stream, cancellationToken);
    }

    private async ValueTask CompleteActiveStreamSlowAsync(
        BlueTuskPortalFieldStream stream,
        CancellationToken cancellationToken)
    {
        await SkipActiveFieldAsync(cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(_activeStream, stream))
        {
            return;
        }

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
        await ReadExactlyAsync(
            _inlinePayload.AsMemory(0, sizeof(int)),
            cancellationToken).ConfigureAwait(false);
        SetActiveField(BinaryPrimitives.ReadInt32BigEndian(_inlinePayload));
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
        if (count == 0)
        {
            return;
        }

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
        if (_payloadBuffered)
        {
            _bufferedPayload.Span.Slice(_payloadConsumed, destination.Length).CopyTo(destination);
        }
        else
        {
            _session.ReadPortalPayloadExactly(destination);
        }

        _payloadConsumed += destination.Length;
    }

    private ValueTask ReadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        EnsurePayloadAvailable(destination.Length);
        if (_payloadBuffered)
        {
            _bufferedPayload.Slice(_payloadConsumed, destination.Length).CopyTo(destination);
            _payloadConsumed += destination.Length;
            return ValueTask.CompletedTask;
        }

        return ReadExactlySlowAsync(destination, cancellationToken);
    }

    private ValueTask ReadExactlySlowAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var pendingRead = _session.ReadPortalPayloadExactlyAsync(destination, cancellationToken);
        if (pendingRead.IsCompletedSuccessfully)
        {
            _payloadConsumed += destination.Length;
            return ValueTask.CompletedTask;
        }

        return AwaitReadExactlySlowAsync(pendingRead, destination.Length);
    }

    private async ValueTask AwaitReadExactlySlowAsync(ValueTask pendingRead, int length)
    {
        await pendingRead.ConfigureAwait(false);
        _payloadConsumed += length;
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

    private void Reset(int payloadLength)
    {
        ResetState(payloadLength, payloadBuffered: false);
        _payloadBuffered = payloadLength <= _inlinePayload.Length &&
            _session.TryReadBufferedPortalPayloadExactly(_inlinePayload.AsSpan(0, payloadLength));
        if (_payloadBuffered)
        {
            _bufferedPayload = _inlinePayload.AsMemory(0, payloadLength);
        }
    }

    private void ResetState(int payloadLength, bool payloadBuffered)
    {
        _payloadLength = payloadLength;
        _payloadConsumed = 0;
        _payloadBuffered = payloadBuffered;
        _bufferedPayload = default;
        _activeOrdinal = -1;
        _activeLength = -2;
        _activePosition = 0;
        _cachedValue = null;
        _activeStream = null;
        _finished = false;
        FieldCount = 0;
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

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_remaining == 0 || buffer.IsEmpty)
        {
            return ValueTask.FromResult(0);
        }

        var pendingRead = _row.ReadActiveFieldAsync(
            buffer[..Math.Min(buffer.Length, _remaining)],
            this,
            cancellationToken);
        return pendingRead;
    }

    internal void Advance(int count)
    {
        _remaining -= count;
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

    [SuppressMessage(
        "Usage",
        "CA2215:Dispose methods should call base class dispose",
        Justification = "The completed fast path calls the base directly; the incomplete path calls it after asynchronous draining.")]
    public override ValueTask DisposeAsync()
    {
        if (_remaining == 0)
        {
            CompleteFromOwner();
            var completion = base.DisposeAsync();
            GC.SuppressFinalize(this);
            return completion;
        }

        var slowCompletion = DisposeSlowAsync();
        GC.SuppressFinalize(this);
        return slowCompletion;
    }

    private async ValueTask DisposeSlowAsync()
    {
        await CompleteFromOwnerAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
