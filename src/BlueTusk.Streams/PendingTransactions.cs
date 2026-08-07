using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using BlueTusk.Replication.PgOutput;

namespace BlueTusk.Streams;

internal sealed class PendingTransaction
{
    private readonly ChangeSourceIdentity _source;
    private readonly TransactionAssemblyOptions _options;
    private readonly ITransactionSpool _spool;
    private readonly List<PendingChange> _changes = [];
    private readonly List<ChangeTable> _tables = [];
    private readonly Dictionary<ChangeTable, int> _tableTokens = new(ReferenceEqualityComparer.Instance);
    private ITransactionSpoolWriter? _spoolWriter;

    public PendingTransaction(
        ChangeSourceIdentity source,
        uint transactionId,
        BlueTuskLogSequenceNumber beginFinalPosition,
        DateTimeOffset beginTimestamp,
        TransactionAssemblyOptions options,
        ITransactionSpool spool)
    {
        _source = source;
        TransactionId = transactionId;
        BeginFinalPosition = beginFinalPosition;
        BeginTimestamp = beginTimestamp;
        _options = options;
        _spool = spool;
    }

    public uint TransactionId { get; }

    public BlueTuskLogSequenceNumber BeginFinalPosition { get; }

    public DateTimeOffset BeginTimestamp { get; }

    public string? Origin { get; set; }

    public int ChangeCount { get; private set; }

    public long EstimatedBytes { get; private set; }

    public async ValueTask AppendAsync(
        PendingChange change,
        long estimatedBytes,
        CancellationToken cancellationToken)
    {
        if (ChangeCount >= _options.MaxChangesPerTransaction)
        {
            throw new TransactionAssemblyLimitExceededException(
                $"Transaction {TransactionId} exceeds the {_options.MaxChangesPerTransaction}-change limit.");
        }

        var updatedBytes = checked(EstimatedBytes + Math.Max(estimatedBytes, 1));
        if (updatedBytes > _options.MaxTransactionBytes)
        {
            throw new TransactionAssemblyLimitExceededException(
                $"Transaction {TransactionId} exceeds the {_options.MaxTransactionBytes}-byte limit.");
        }

        if (_spoolWriter is null && updatedBytes > _options.MaxInMemoryTransactionBytes)
        {
            _spoolWriter = await _spool.CreateAsync(
                new TransactionSpoolKey(_source.Fingerprint, TransactionId),
                cancellationToken).ConfigureAwait(false);
            foreach (var buffered in _changes)
            {
                await AppendSpoolChangeAsync(_spoolWriter, buffered, cancellationToken)
                    .ConfigureAwait(false);
            }

            _changes.Clear();
        }

        if (_spoolWriter is null)
        {
            _changes.Add(change);
        }
        else
        {
            await AppendSpoolChangeAsync(_spoolWriter, change, cancellationToken)
                .ConfigureAwait(false);
        }

        ChangeCount++;
        EstimatedBytes = updatedBytes;
    }

    private static ValueTask AppendSpoolChangeAsync(
        ITransactionSpoolWriter writer,
        PendingChange change,
        CancellationToken cancellationToken) =>
        writer is ITransactionSpoolBufferWriter bufferWriter
            ? bufferWriter.AppendAsync(
                change,
                static (destination, pending) => PendingChangeCodec.Write(destination, pending),
                cancellationToken)
            : writer.AppendAsync(PendingChangeCodec.Serialize(change), cancellationToken);

    public int GetTableToken(ChangeTable table)
    {
        if (_tableTokens.TryGetValue(table, out var token))
        {
            return token;
        }

        if (_tables.Count >= _options.MaxRelationsPerTransaction)
        {
            throw new TransactionAssemblyLimitExceededException(
                $"Transaction {TransactionId} exceeds the {_options.MaxRelationsPerTransaction}-relation limit.");
        }

        token = _tables.Count;
        _tables.Add(table);
        _tableTokens.Add(table, token);
        return token;
    }

    public async ValueTask<CompletedPendingTransaction> CompleteAsync(CancellationToken cancellationToken)
    {
        ITransactionSpoolReader? reader = null;
        if (_spoolWriter is not null)
        {
            reader = await _spoolWriter.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await _spoolWriter.DisposeAsync().ConfigureAwait(false);
            _spoolWriter = null;
        }

        return new CompletedPendingTransaction(
            _changes.ToArray(),
            reader,
            _tables.ToArray(),
            ChangeCount,
            EstimatedBytes);
    }

    public async ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        if (_spoolWriter is not null)
        {
            await _spoolWriter.AbortAsync(cancellationToken).ConfigureAwait(false);
            await _spoolWriter.DisposeAsync().ConfigureAwait(false);
            _spoolWriter = null;
        }

        _changes.Clear();
    }
}

internal sealed record CompletedPendingTransaction(
    PendingChange[] InMemoryChanges,
    ITransactionSpoolReader? SpoolReader,
    ChangeTable[] Tables,
    int Count,
    long EstimatedBytes);

internal enum PendingChangeKind : byte
{
    Insert = 1,
    Update = 2,
    Delete = 3,
    Truncate = 4,
    LogicalMessage = 5,
}

internal sealed record PendingTuple(PendingTupleValue[] Values);

internal sealed record PendingTupleValue(
    BlueTuskPgOutputTupleValueKind Kind,
    ReadOnlyMemory<byte> Data);

internal sealed record PendingChange
{
    public required PendingChangeKind Kind { get; init; }

    public int TableToken { get; init; } = -1;

    public int[] TableTokens { get; init; } = [];

    public BlueTuskPgOutputOldRowKind? OldRowKind { get; init; }

    public PendingTuple? OldRow { get; init; }

    public PendingTuple? NewRow { get; init; }

    public BlueTuskPgOutputTruncateOptions TruncateOptions { get; init; }

    public bool IsTransactionalMessage { get; init; }

    public BlueTuskLogSequenceNumber MessagePosition { get; init; }

    public string? MessagePrefix { get; init; }

    public ReadOnlyMemory<byte> MessageContent { get; init; }
}

internal static class PendingChangeCodec
{
    public static ReadOnlyMemory<byte> Serialize(PendingChange change)
    {
        var writer = new ArrayBufferWriter<byte>(EstimateSerializedLength(change));
        Write(writer, change);
        return writer.WrittenMemory;
    }

    public static void Write(IBufferWriter<byte> writer, PendingChange change)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(change);
        WriteByte(writer, (byte)change.Kind);
        switch (change.Kind)
        {
            case PendingChangeKind.Insert:
                WriteInt32(writer, change.TableToken);
                WriteTuple(writer, change.NewRow!);
                break;
            case PendingChangeKind.Update:
                WriteInt32(writer, change.TableToken);
                WriteByte(writer, change.OldRowKind.HasValue ? (byte)1 : (byte)0);
                if (change.OldRowKind.HasValue)
                {
                    WriteByte(writer, (byte)change.OldRowKind.Value);
                    WriteTuple(writer, change.OldRow!);
                }

                WriteTuple(writer, change.NewRow!);
                break;
            case PendingChangeKind.Delete:
                WriteInt32(writer, change.TableToken);
                WriteByte(writer, (byte)change.OldRowKind!.Value);
                WriteTuple(writer, change.OldRow!);
                break;
            case PendingChangeKind.Truncate:
                WriteByte(writer, (byte)change.TruncateOptions);
                WriteInt32(writer, change.TableTokens.Length);
                foreach (var token in change.TableTokens)
                {
                    WriteInt32(writer, token);
                }

                break;
            case PendingChangeKind.LogicalMessage:
                WriteByte(writer, change.IsTransactionalMessage ? (byte)1 : (byte)0);
                WriteUInt64(writer, change.MessagePosition.Value);
                WriteString(writer, change.MessagePrefix!);
                WriteInt32(writer, change.MessageContent.Length);
                WriteBytes(writer, change.MessageContent);
                break;
            default:
                throw new InvalidOperationException($"Unsupported pending change kind {change.Kind}.");
        }
    }

    public static PendingChange Deserialize(ReadOnlyMemory<byte> payload)
    {
        var reader = new PendingChangeReader(payload);
        PendingChange change;
        try
        {
            var kind = (PendingChangeKind)reader.ReadByte();
            change = kind switch
            {
                PendingChangeKind.Insert => new PendingChange
                {
                    Kind = kind,
                    TableToken = reader.ReadInt32(),
                    NewRow = ReadTuple(ref reader),
                },
                PendingChangeKind.Update => ReadUpdate(ref reader),
                PendingChangeKind.Delete => new PendingChange
                {
                    Kind = kind,
                    TableToken = reader.ReadInt32(),
                    OldRowKind = ReadOldRowKind(ref reader),
                    OldRow = ReadTuple(ref reader),
                },
                PendingChangeKind.Truncate => ReadTruncate(ref reader),
                PendingChangeKind.LogicalMessage => ReadLogicalMessage(ref reader),
                _ => throw new TransactionSpoolIntegrityException(
                    $"A spooled transaction contains unknown change kind {(byte)kind}."),
            };
        }
        catch (EndOfStreamException exception)
        {
            throw new TransactionSpoolIntegrityException(
                "A spooled transaction change ended before its declared data was complete.",
                exception);
        }

        if (reader.Remaining != 0)
        {
            throw new TransactionSpoolIntegrityException("A spooled transaction change has trailing data.");
        }

        return change;
    }

    private static PendingChange ReadUpdate(ref PendingChangeReader reader)
    {
        var tableToken = reader.ReadInt32();
        BlueTuskPgOutputOldRowKind? oldRowKind = null;
        PendingTuple? oldRow = null;
        if (reader.ReadBoolean())
        {
            oldRowKind = ReadOldRowKind(ref reader);
            oldRow = ReadTuple(ref reader);
        }

        return new PendingChange
        {
            Kind = PendingChangeKind.Update,
            TableToken = tableToken,
            OldRowKind = oldRowKind,
            OldRow = oldRow,
            NewRow = ReadTuple(ref reader),
        };
    }

    private static PendingChange ReadTruncate(ref PendingChangeReader reader)
    {
        var options = (BlueTuskPgOutputTruncateOptions)reader.ReadByte();
        var count = reader.ReadCount();
        var tokens = new int[count];
        for (var index = 0; index < count; index++)
        {
            tokens[index] = reader.ReadInt32();
        }

        return new PendingChange
        {
            Kind = PendingChangeKind.Truncate,
            TruncateOptions = options,
            TableTokens = tokens,
        };
    }

    private static PendingChange ReadLogicalMessage(ref PendingChangeReader reader)
    {
        var transactional = reader.ReadBoolean();
        var position = new BlueTuskLogSequenceNumber(reader.ReadUInt64());
        var prefix = reader.ReadString();
        var contentLength = reader.ReadCount();
        var content = reader.ReadMemory(contentLength);

        return new PendingChange
        {
            Kind = PendingChangeKind.LogicalMessage,
            IsTransactionalMessage = transactional,
            MessagePosition = position,
            MessagePrefix = prefix,
            MessageContent = content,
        };
    }

    private static void WriteTuple(IBufferWriter<byte> writer, PendingTuple tuple)
    {
        WriteInt32(writer, tuple.Values.Length);
        foreach (var value in tuple.Values)
        {
            WriteByte(writer, (byte)value.Kind);
            WriteInt32(writer, value.Data.Length);
            WriteBytes(writer, value.Data);
        }
    }

    private static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        var destination = writer.GetSpan(1);
        destination[0] = value;
        writer.Advance(1);
    }

    private static void WriteInt32(IBufferWriter<byte> writer, int value)
    {
        var destination = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(destination, value);
        writer.Advance(sizeof(int));
    }

    private static void WriteUInt64(IBufferWriter<byte> writer, ulong value)
    {
        var destination = writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
        writer.Advance(sizeof(ulong));
    }

    private static void WriteString(IBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Write7BitEncodedInt(writer, byteCount);
        if (byteCount == 0)
        {
            return;
        }

        var destination = writer.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value.AsSpan(), destination);
        writer.Advance(written);
    }

    private static void Write7BitEncodedInt(IBufferWriter<byte> writer, int value)
    {
        var remaining = unchecked((uint)value);
        while (remaining >= 0x80)
        {
            WriteByte(writer, (byte)(remaining | 0x80));
            remaining >>= 7;
        }

        WriteByte(writer, (byte)remaining);
    }

    private static void WriteBytes(IBufferWriter<byte> writer, ReadOnlyMemory<byte> source)
    {
        if (writer is IBufferWriterSegmentSink segmentSink)
        {
            segmentSink.Write(source);
            return;
        }

        const int maximumSegmentBytes = 64 * 1024;
        var remaining = source.Span;
        while (!remaining.IsEmpty)
        {
            var count = Math.Min(remaining.Length, maximumSegmentBytes);
            var destination = writer.GetSpan(count);
            remaining[..count].CopyTo(destination);
            writer.Advance(count);
            remaining = remaining[count..];
        }
    }

    private static PendingTuple ReadTuple(ref PendingChangeReader reader)
    {
        var count = reader.ReadCount();
        var values = new PendingTupleValue[count];
        for (var index = 0; index < count; index++)
        {
            var kind = (BlueTuskPgOutputTupleValueKind)reader.ReadByte();
            if (kind is not (
                BlueTuskPgOutputTupleValueKind.Null or
                BlueTuskPgOutputTupleValueKind.UnchangedToast or
                BlueTuskPgOutputTupleValueKind.Text or
                BlueTuskPgOutputTupleValueKind.Binary))
            {
                throw new TransactionSpoolIntegrityException($"A spooled tuple contains unknown value kind {(byte)kind}.");
            }

            var length = reader.ReadCount();
            var data = reader.ReadMemory(length);

            values[index] = new PendingTupleValue(kind, data);
        }

        return new PendingTuple(values);
    }

    private static BlueTuskPgOutputOldRowKind ReadOldRowKind(ref PendingChangeReader reader)
    {
        var kind = (BlueTuskPgOutputOldRowKind)reader.ReadByte();
        if (kind is not (BlueTuskPgOutputOldRowKind.Key or BlueTuskPgOutputOldRowKind.Full))
        {
            throw new TransactionSpoolIntegrityException($"A spooled change contains unknown old-row kind {(byte)kind}.");
        }

        return kind;
    }

    private static int EstimateSerializedLength(PendingChange change)
    {
        var length = 32L;
        length += EstimateTuple(change.OldRow);
        length += EstimateTuple(change.NewRow);
        length += change.TableTokens.Length * sizeof(int);
        length += change.MessageContent.Length;
        if (change.MessagePrefix is not null)
        {
            length += Encoding.UTF8.GetByteCount(change.MessagePrefix) + 5L;
        }

        return checked((int)Math.Min(length, int.MaxValue));
    }

    private static long EstimateTuple(PendingTuple? tuple)
    {
        if (tuple is null)
        {
            return 0;
        }

        var length = 4L;
        foreach (var value in tuple.Values)
        {
            length += 5L + value.Data.Length;
        }

        return length;
    }

    private ref struct PendingChangeReader
    {
        private readonly ReadOnlyMemory<byte> _payload;
        private int _offset;

        internal PendingChangeReader(ReadOnlyMemory<byte> payload)
        {
            _payload = payload;
        }

        internal int Remaining => _payload.Length - _offset;

        internal byte ReadByte()
        {
            EnsureAvailable(1);
            return _payload.Span[_offset++];
        }

        internal bool ReadBoolean() => ReadByte() != 0;

        internal int ReadInt32()
        {
            EnsureAvailable(sizeof(int));
            var value = BinaryPrimitives.ReadInt32LittleEndian(
                _payload.Span.Slice(_offset, sizeof(int)));
            _offset += sizeof(int);
            return value;
        }

        internal ulong ReadUInt64()
        {
            EnsureAvailable(sizeof(ulong));
            var value = BinaryPrimitives.ReadUInt64LittleEndian(
                _payload.Span.Slice(_offset, sizeof(ulong)));
            _offset += sizeof(ulong);
            return value;
        }

        internal int ReadCount()
        {
            var count = ReadInt32();
            if (count < 0 || count > Remaining)
            {
                throw new TransactionSpoolIntegrityException(
                    $"A spooled change contains an invalid length or count of {count}.");
            }

            return count;
        }

        internal string ReadString()
        {
            var byteCount = Read7BitEncodedInt();
            return Encoding.UTF8.GetString(ReadMemory(byteCount).Span);
        }

        internal ReadOnlyMemory<byte> ReadMemory(int length)
        {
            EnsureAvailable(length);
            var memory = _payload.Slice(_offset, length);
            _offset += length;
            return memory;
        }

        private int Read7BitEncodedInt()
        {
            uint value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var current = ReadByte();
                value |= (uint)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    if (value > int.MaxValue)
                    {
                        throw new TransactionSpoolIntegrityException(
                            "A spooled transaction string length is outside the configured bounds.");
                    }

                    return (int)value;
                }
            }

            throw new TransactionSpoolIntegrityException(
                "A spooled transaction contains an invalid string length.");
        }

        private void EnsureAvailable(int length)
        {
            if (length < 0 || length > Remaining)
            {
                throw new EndOfStreamException();
            }
        }
    }
}
