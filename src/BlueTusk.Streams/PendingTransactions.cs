using System.Runtime.InteropServices;
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
                await _spoolWriter.AppendAsync(PendingChangeCodec.Serialize(buffered), cancellationToken)
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
            await _spoolWriter.AppendAsync(PendingChangeCodec.Serialize(change), cancellationToken)
                .ConfigureAwait(false);
        }

        ChangeCount++;
        EstimatedBytes = updatedBytes;
    }

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
        using var stream = new MemoryStream(EstimateSerializedLength(change));
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)change.Kind);
        switch (change.Kind)
        {
            case PendingChangeKind.Insert:
                writer.Write(change.TableToken);
                WriteTuple(writer, change.NewRow!);
                break;
            case PendingChangeKind.Update:
                writer.Write(change.TableToken);
                writer.Write(change.OldRowKind.HasValue);
                if (change.OldRowKind.HasValue)
                {
                    writer.Write((byte)change.OldRowKind.Value);
                    WriteTuple(writer, change.OldRow!);
                }

                WriteTuple(writer, change.NewRow!);
                break;
            case PendingChangeKind.Delete:
                writer.Write(change.TableToken);
                writer.Write((byte)change.OldRowKind!.Value);
                WriteTuple(writer, change.OldRow!);
                break;
            case PendingChangeKind.Truncate:
                writer.Write((byte)change.TruncateOptions);
                writer.Write(change.TableTokens.Length);
                foreach (var token in change.TableTokens)
                {
                    writer.Write(token);
                }

                break;
            case PendingChangeKind.LogicalMessage:
                writer.Write(change.IsTransactionalMessage);
                writer.Write(change.MessagePosition.Value);
                writer.Write(change.MessagePrefix!);
                writer.Write(change.MessageContent.Length);
                writer.Write(change.MessageContent.Span);
                break;
            default:
                throw new InvalidOperationException($"Unsupported pending change kind {change.Kind}.");
        }

        writer.Flush();
        return new ReadOnlyMemory<byte>(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    public static PendingChange Deserialize(ReadOnlyMemory<byte> payload)
    {
        ReadOnlyMemory<byte> ownedPayload;
        MemoryStream stream;
        if (MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array is not null)
        {
            ownedPayload = payload;
            stream = new MemoryStream(
                segment.Array,
                segment.Offset,
                segment.Count,
                writable: false,
                publiclyVisible: true);
        }
        else
        {
            var copy = payload.ToArray();
            ownedPayload = copy;
            stream = new MemoryStream(copy, writable: false);
        }

        using var streamOwner = stream;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
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
                    NewRow = ReadTuple(reader, ownedPayload),
                },
                PendingChangeKind.Update => ReadUpdate(reader, ownedPayload),
                PendingChangeKind.Delete => new PendingChange
                {
                    Kind = kind,
                    TableToken = reader.ReadInt32(),
                    OldRowKind = ReadOldRowKind(reader),
                    OldRow = ReadTuple(reader, ownedPayload),
                },
                PendingChangeKind.Truncate => ReadTruncate(reader),
                PendingChangeKind.LogicalMessage => ReadLogicalMessage(reader, ownedPayload),
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

        if (stream.Position != stream.Length)
        {
            throw new TransactionSpoolIntegrityException("A spooled transaction change has trailing data.");
        }

        return change;
    }

    private static PendingChange ReadUpdate(BinaryReader reader, ReadOnlyMemory<byte> payload)
    {
        var tableToken = reader.ReadInt32();
        BlueTuskPgOutputOldRowKind? oldRowKind = null;
        PendingTuple? oldRow = null;
        if (reader.ReadBoolean())
        {
            oldRowKind = ReadOldRowKind(reader);
            oldRow = ReadTuple(reader, payload);
        }

        return new PendingChange
        {
            Kind = PendingChangeKind.Update,
            TableToken = tableToken,
            OldRowKind = oldRowKind,
            OldRow = oldRow,
            NewRow = ReadTuple(reader, payload),
        };
    }

    private static PendingChange ReadTruncate(BinaryReader reader)
    {
        var options = (BlueTuskPgOutputTruncateOptions)reader.ReadByte();
        var count = ReadCount(reader);
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

    private static PendingChange ReadLogicalMessage(BinaryReader reader, ReadOnlyMemory<byte> payload)
    {
        var transactional = reader.ReadBoolean();
        var position = new BlueTuskLogSequenceNumber(reader.ReadUInt64());
        var prefix = reader.ReadString();
        var contentLength = ReadCount(reader);
        var content = ReadMemory(reader, payload, contentLength);

        return new PendingChange
        {
            Kind = PendingChangeKind.LogicalMessage,
            IsTransactionalMessage = transactional,
            MessagePosition = position,
            MessagePrefix = prefix,
            MessageContent = content,
        };
    }

    private static void WriteTuple(BinaryWriter writer, PendingTuple tuple)
    {
        writer.Write(tuple.Values.Length);
        foreach (var value in tuple.Values)
        {
            writer.Write((byte)value.Kind);
            writer.Write(value.Data.Length);
            writer.Write(value.Data.Span);
        }
    }

    private static PendingTuple ReadTuple(BinaryReader reader, ReadOnlyMemory<byte> payload)
    {
        var count = ReadCount(reader);
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

            var length = ReadCount(reader);
            var data = ReadMemory(reader, payload, length);

            values[index] = new PendingTupleValue(kind, data);
        }

        return new PendingTuple(values);
    }

    private static BlueTuskPgOutputOldRowKind ReadOldRowKind(BinaryReader reader)
    {
        var kind = (BlueTuskPgOutputOldRowKind)reader.ReadByte();
        if (kind is not (BlueTuskPgOutputOldRowKind.Key or BlueTuskPgOutputOldRowKind.Full))
        {
            throw new TransactionSpoolIntegrityException($"A spooled change contains unknown old-row kind {(byte)kind}.");
        }

        return kind;
    }

    private static int ReadCount(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new TransactionSpoolIntegrityException($"A spooled change contains an invalid length or count of {count}.");
        }

        return count;
    }

    private static ReadOnlyMemory<byte> ReadMemory(
        BinaryReader reader,
        ReadOnlyMemory<byte> payload,
        int length)
    {
        var offset = checked((int)reader.BaseStream.Position);
        if (length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new EndOfStreamException();
        }

        reader.BaseStream.Position += length;
        return payload.Slice(offset, length);
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

    private static long EstimateTuple(PendingTuple? tuple) =>
        tuple is null ? 0 : 4L + tuple.Values.Sum(value => 5L + value.Data.Length);
}
