using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Streams;

public sealed record ChangeTransactionEnvelopeOptions
{
    public int MaxEnvelopeBytes { get; init; } = 256 * 1024 * 1024;

    public int MaxChanges { get; init; } = 1_000_000;

    public int MaxTables { get; init; } = 4096;

    public int MaxColumnsPerTable { get; init; } = 16_384;

    public int MaxStringBytes { get; init; } = 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEnvelopeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxChanges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTables);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxColumnsPerTable);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxStringBytes);
    }
}

public sealed class ChangeTransactionEnvelope
{
    private readonly int _formatVersion;

    internal ChangeTransactionEnvelope(byte[] data, int formatVersion)
    {
        Data = data;
        _formatVersion = formatVersion;
    }

    public const int MinimumSupportedFormatVersion = 1;

    public const int CurrentFormatVersion = 2;

    public int FormatVersion => _formatVersion;

    public ReadOnlyMemory<byte> Data { get; }
}

public static class ChangeTransactionEnvelopeCodec
{
    private const uint Magic = 0x4C525442;
    private const int HashSize = 32;

    public static async ValueTask<ChangeTransactionEnvelope> EncodeAsync(
        ChangeTransaction transaction,
        ChangeTransactionEnvelopeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var effectiveOptions = options ?? new ChangeTransactionEnvelopeOptions();
        effectiveOptions.Validate();
        var changes = await transaction.Changes.MaterializeAsync(cancellationToken).ConfigureAwait(false);
        if (changes.Count > effectiveOptions.MaxChanges)
        {
            throw new ChangeTransactionEnvelopeException(
                $"Transaction has {changes.Count} changes; the envelope limit is {effectiveOptions.MaxChanges}.");
        }

        var tables = CollectTables(changes, effectiveOptions.MaxTables);
        using var stream = new MemoryStream(capacity: Math.Min(effectiveOptions.MaxEnvelopeBytes, 1024 * 1024));
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(ChangeTransactionEnvelope.CurrentFormatVersion);
            WriteSource(writer, transaction.Source, effectiveOptions);
            writer.Write(transaction.TransactionId);
            writer.Write(transaction.BeginFinalPosition.Value);
            writer.Write(transaction.CommitPosition.Value);
            writer.Write(transaction.CommitEndPosition.Value);
            writer.Write(transaction.CommitTimestamp.UtcTicks);
            WriteOptionalString(writer, transaction.Origin, effectiveOptions);
            writer.Write(transaction.IsSynthetic);
            writer.Write((byte)transaction.Outcome);
            WriteOptionalString(writer, transaction.GlobalTransactionId, effectiveOptions);
            writer.Write(tables.Count);
            foreach (var table in tables.Keys)
            {
                WriteTable(writer, table, effectiveOptions);
            }

            writer.Write(changes.Count);
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteChange(writer, change, tables, effectiveOptions);
                if (stream.Length + HashSize > effectiveOptions.MaxEnvelopeBytes)
                {
                    throw new ChangeTransactionEnvelopeException(
                        $"The transaction envelope exceeds {effectiveOptions.MaxEnvelopeBytes} bytes.");
                }
            }

            writer.Flush();
        }

        if (stream.Length + HashSize > effectiveOptions.MaxEnvelopeBytes)
        {
            throw new ChangeTransactionEnvelopeException(
                $"The transaction envelope exceeds {effectiveOptions.MaxEnvelopeBytes} bytes.");
        }

        var contentLength = checked((int)stream.Length);
        var envelope = new byte[checked(contentLength + HashSize)];
        stream.GetBuffer().AsSpan(0, contentLength).CopyTo(envelope);
        _ = SHA256.HashData(envelope.AsSpan(0, contentLength), envelope.AsSpan(contentLength));
        return new ChangeTransactionEnvelope(
            envelope,
            ChangeTransactionEnvelope.CurrentFormatVersion);
    }

    public static ChangeTransaction Decode(
        ChangeTransactionEnvelope envelope,
        ChangeTransactionEnvelopeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var effectiveOptions = options ?? new ChangeTransactionEnvelopeOptions();
        effectiveOptions.Validate();
        var data = envelope.Data;
        if (data.Length < sizeof(uint) + sizeof(int) + HashSize ||
            data.Length > effectiveOptions.MaxEnvelopeBytes)
        {
            throw new ChangeTransactionEnvelopeException("The transaction envelope length is invalid.");
        }

        var content = data[..^HashSize];
        Span<byte> actualHash = stackalloc byte[HashSize];
        _ = SHA256.HashData(content.Span, actualHash);
        if (!CryptographicOperations.FixedTimeEquals(data.Span[^HashSize..], actualHash))
        {
            throw new ChangeTransactionEnvelopeException("The transaction envelope integrity hash is invalid.");
        }

        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic)
            {
                throw new ChangeTransactionEnvelopeException("The transaction envelope header is invalid.");
            }

            var format = reader.ReadInt32();
            if (format < ChangeTransactionEnvelope.MinimumSupportedFormatVersion ||
                format > ChangeTransactionEnvelope.CurrentFormatVersion)
            {
                throw new ChangeTransactionEnvelopeException(
                    $"Transaction envelope format {format} is not supported.");
            }

            if (format != envelope.FormatVersion)
            {
                throw new ChangeTransactionEnvelopeException(
                    "The transaction envelope format metadata does not match its payload.");
            }

            var source = ReadSource(reader, effectiveOptions);
            var transactionId = reader.ReadUInt32();
            var beginFinalPosition = new BlueTuskLogSequenceNumber(reader.ReadUInt64());
            var commitPosition = new BlueTuskLogSequenceNumber(reader.ReadUInt64());
            var commitEndPosition = new BlueTuskLogSequenceNumber(reader.ReadUInt64());
            var commitTimestamp = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var origin = ReadOptionalString(reader, effectiveOptions);
            var isSynthetic = reader.ReadBoolean();
            var outcome = ChangeTransactionOutcome.Committed;
            string? globalTransactionId = null;
            if (format >= 2)
            {
                outcome = (ChangeTransactionOutcome)reader.ReadByte();
                if (!Enum.IsDefined(outcome))
                {
                    throw new ChangeTransactionEnvelopeException(
                        $"The transaction envelope contains unknown outcome {(byte)outcome}.");
                }

                globalTransactionId = ReadOptionalString(reader, effectiveOptions);
            }

            var tableCount = ReadCount(reader, effectiveOptions.MaxTables, "tables");
            var tables = new ChangeTable[tableCount];
            for (var index = 0; index < tables.Length; index++)
            {
                tables[index] = ReadTable(reader, effectiveOptions);
            }

            var changeCount = ReadCount(reader, effectiveOptions.MaxChanges, "changes");
            var changes = new Change[changeCount];
            for (var ordinal = 0; ordinal < changes.Length; ordinal++)
            {
                changes[ordinal] = ReadChange(
                    reader,
                    source,
                    transactionId,
                    commitEndPosition,
                    ordinal,
                    tables,
                    effectiveOptions);
            }

            if (stream.Position != stream.Length)
            {
                throw new ChangeTransactionEnvelopeException(
                    "The transaction envelope contains trailing data.");
            }

            var changeSet = new ChangeSet(
                changes.Length,
                data.Length,
                isSpooled: false,
                cancellationToken => EnumerateChanges(changes, cancellationToken));
            return new ChangeTransaction(
                source,
                transactionId,
                beginFinalPosition,
                commitPosition,
                commitEndPosition,
                commitTimestamp,
                origin,
                isSynthetic,
                outcome,
                globalTransactionId,
                changeSet);
        }
        catch (ChangeTransactionEnvelopeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or
                                          ArgumentException or OverflowException)
        {
            throw new ChangeTransactionEnvelopeException(
                "The transaction envelope payload is invalid.",
                exception);
        }
    }

    public static ChangeTransactionEnvelope FromData(
        ReadOnlySpan<byte> data,
        ChangeTransactionEnvelopeOptions? options = null)
    {
        var effectiveOptions = options ?? new ChangeTransactionEnvelopeOptions();
        effectiveOptions.Validate();
        if (data.Length < sizeof(uint) + sizeof(int) + HashSize ||
            data.Length > effectiveOptions.MaxEnvelopeBytes)
        {
            throw new ChangeTransactionEnvelopeException("The transaction envelope length is invalid.");
        }

        var formatVersion = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(sizeof(uint), sizeof(int)));
        var envelope = new ChangeTransactionEnvelope(
            data.ToArray(),
            formatVersion);
        _ = Decode(envelope, effectiveOptions);
        return envelope;
    }

    private static Dictionary<ChangeTable, int> CollectTables(
        IReadOnlyList<Change> changes,
        int maximum)
    {
        var tables = new Dictionary<ChangeTable, int>(ReferenceEqualityComparer.Instance);
        foreach (var change in changes)
        {
            switch (change)
            {
                case InsertChange insert:
                    AddTable(tables, insert.NewRow.Table, maximum);
                    break;
                case UpdateChange update:
                    AddTable(tables, update.OldRow.Table, maximum);
                    AddTable(tables, update.NewRow.Table, maximum);
                    break;
                case DeleteChange delete:
                    AddTable(tables, delete.OldRow.Table, maximum);
                    break;
                case TruncateChange truncate:
                    foreach (var table in truncate.Tables)
                    {
                        AddTable(tables, table, maximum);
                    }

                    break;
            }
        }

        return tables;
    }

    private static void AddTable(Dictionary<ChangeTable, int> tables, ChangeTable table, int maximum)
    {
        if (tables.ContainsKey(table))
        {
            return;
        }

        if (tables.Count >= maximum)
        {
            throw new ChangeTransactionEnvelopeException(
                $"The transaction envelope exceeds the {maximum}-table limit.");
        }

        tables.Add(table, tables.Count);
    }

    private static void WriteSource(
        BinaryWriter writer,
        ChangeSourceIdentity source,
        ChangeTransactionEnvelopeOptions options)
    {
        WriteString(writer, source.SystemIdentifier, options);
        WriteString(writer, source.DatabaseName, options);
        WriteString(writer, source.SlotName, options);
        WriteString(writer, source.PublicationFingerprint, options);
    }

    private static ChangeSourceIdentity ReadSource(
        BinaryReader reader,
        ChangeTransactionEnvelopeOptions options) =>
        new(
            ReadString(reader, options),
            ReadString(reader, options),
            ReadString(reader, options),
            ReadString(reader, options));

    private static void WriteTable(
        BinaryWriter writer,
        ChangeTable table,
        ChangeTransactionEnvelopeOptions options)
    {
        if (table.Columns.Count > options.MaxColumnsPerTable)
        {
            throw new ChangeTransactionEnvelopeException(
                $"Table '{table}' exceeds the {options.MaxColumnsPerTable}-column envelope limit.");
        }

        writer.Write(table.RelationId);
        WriteString(writer, table.Schema, options);
        WriteString(writer, table.Name, options);
        writer.Write(table.ReplicaIdentity);
        writer.Write(table.Columns.Count);
        foreach (var column in table.Columns)
        {
            writer.Write(column.Ordinal);
            WriteString(writer, column.Name, options);
            writer.Write(column.TypeOid);
            writer.Write(column.TypeModifier);
            writer.Write(column.IsKey);
            writer.Write(column.Type is not null);
            if (column.Type is not null)
            {
                writer.Write(column.Type.Oid);
                WriteString(writer, column.Type.Namespace, options);
                WriteString(writer, column.Type.Name, options);
            }
        }
    }

    private static ChangeTable ReadTable(
        BinaryReader reader,
        ChangeTransactionEnvelopeOptions options)
    {
        var relationId = reader.ReadUInt32();
        var schema = ReadString(reader, options);
        var name = ReadString(reader, options);
        var replicaIdentity = reader.ReadChar();
        var columnCount = ReadCount(reader, options.MaxColumnsPerTable, "columns");
        var columns = new ChangeColumn[columnCount];
        for (var index = 0; index < columns.Length; index++)
        {
            var ordinal = reader.ReadInt32();
            var columnName = ReadString(reader, options);
            var typeOid = reader.ReadUInt32();
            var typeModifier = reader.ReadInt32();
            var isKey = reader.ReadBoolean();
            ChangeTypeIdentity? type = null;
            if (reader.ReadBoolean())
            {
                type = new ChangeTypeIdentity(
                    reader.ReadUInt32(),
                    ReadString(reader, options),
                    ReadString(reader, options));
            }

            columns[index] = new ChangeColumn(
                ordinal,
                columnName,
                typeOid,
                typeModifier,
                isKey,
                type);
        }

        return new ChangeTable(relationId, schema, name, replicaIdentity, columns);
    }

    private static void WriteChange(
        BinaryWriter writer,
        Change change,
        Dictionary<ChangeTable, int> tables,
        ChangeTransactionEnvelopeOptions options)
    {
        writer.Write((byte)change.Kind);
        writer.Write(change.Id.Ordinal);
        switch (change)
        {
            case InsertChange insert:
                WriteRow(writer, insert.NewRow, tables, options);
                break;
            case UpdateChange update:
                WriteRow(writer, update.OldRow, tables, options);
                WriteRow(writer, update.NewRow, tables, options);
                writer.Write(update.ChangedColumns.IsExact);
                writer.Write(update.ChangedColumns.Ordinals.Count);
                foreach (var ordinal in update.ChangedColumns.Ordinals)
                {
                    writer.Write(ordinal);
                }

                break;
            case DeleteChange delete:
                WriteRow(writer, delete.OldRow, tables, options);
                break;
            case TruncateChange truncate:
                writer.Write(truncate.Cascade);
                writer.Write(truncate.RestartIdentity);
                writer.Write(truncate.Tables.Count);
                foreach (var table in truncate.Tables)
                {
                    writer.Write(tables[table]);
                }

                break;
            case LogicalMessageChange logical:
                writer.Write(logical.IsTransactional);
                writer.Write(logical.Position.Value);
                WriteString(writer, logical.Prefix, options);
                WriteBytes(writer, logical.Content.Span);
                break;
            default:
                throw new ChangeTransactionEnvelopeException(
                    $"Change type '{change.GetType()}' cannot be written to the relay envelope.");
        }
    }

    private static Change ReadChange(
        BinaryReader reader,
        ChangeSourceIdentity source,
        uint transactionId,
        BlueTuskLogSequenceNumber commitEndPosition,
        int expectedOrdinal,
        ChangeTable[] tables,
        ChangeTransactionEnvelopeOptions options)
    {
        var kind = (ChangeKind)reader.ReadByte();
        var ordinal = reader.ReadInt32();
        if (ordinal != expectedOrdinal)
        {
            throw new ChangeTransactionEnvelopeException(
                $"The transaction envelope change ordinal {ordinal} is out of sequence.");
        }

        var id = new ChangeId(source, commitEndPosition, transactionId, ordinal);
        return kind switch
        {
            ChangeKind.Insert => new InsertChange(id, ReadRow(reader, tables, options)),
            ChangeKind.Update => ReadUpdate(reader, id, tables, options),
            ChangeKind.Delete => new DeleteChange(id, ReadRow(reader, tables, options)),
            ChangeKind.Truncate => ReadTruncate(reader, id, tables),
            ChangeKind.LogicalMessage => new LogicalMessageChange(
                id,
                reader.ReadBoolean(),
                new BlueTuskLogSequenceNumber(reader.ReadUInt64()),
                ReadString(reader, options),
                ReadBytes(reader, options.MaxEnvelopeBytes)),
            _ => throw new ChangeTransactionEnvelopeException(
                $"The transaction envelope contains unknown change kind {(byte)kind}."),
        };
    }

    private static UpdateChange ReadUpdate(
        BinaryReader reader,
        ChangeId id,
        ChangeTable[] tables,
        ChangeTransactionEnvelopeOptions options)
    {
        var oldRow = ReadRow(reader, tables, options);
        var newRow = ReadRow(reader, tables, options);
        var exact = reader.ReadBoolean();
        var count = ReadCount(reader, newRow.Table.Columns.Count, "changed columns");
        var ordinals = new int[count];
        for (var index = 0; index < count; index++)
        {
            ordinals[index] = reader.ReadInt32();
            if ((uint)ordinals[index] >= (uint)newRow.Table.Columns.Count)
            {
                throw new ChangeTransactionEnvelopeException(
                    "The transaction envelope contains an invalid changed-column ordinal.");
            }
        }

        return new UpdateChange(id, oldRow, newRow, new ChangedColumnSet(exact, ordinals));
    }

    private static TruncateChange ReadTruncate(
        BinaryReader reader,
        ChangeId id,
        ChangeTable[] tables)
    {
        var cascade = reader.ReadBoolean();
        var restartIdentity = reader.ReadBoolean();
        var count = ReadCount(reader, tables.Length, "truncate tables");
        var truncated = new ChangeTable[count];
        for (var index = 0; index < count; index++)
        {
            truncated[index] = ReadTableReference(reader, tables);
        }

        return new TruncateChange(id, truncated, cascade, restartIdentity);
    }

    private static void WriteRow(
        BinaryWriter writer,
        ChangeRow row,
        Dictionary<ChangeTable, int> tables,
        ChangeTransactionEnvelopeOptions options)
    {
        writer.Write(tables[row.Table]);
        writer.Write(row.Values.Count);
        foreach (var value in row.Values)
        {
            writer.Write((byte)value.State);
            writer.Write((byte)value.Encoding);
            WriteBytes(writer, value.Data.Span);
            WriteOptionalString(writer, value.DecodingError, options);
        }
    }

    private static ChangeRow ReadRow(
        BinaryReader reader,
        ChangeTable[] tables,
        ChangeTransactionEnvelopeOptions options)
    {
        var table = ReadTableReference(reader, tables);
        var count = ReadCount(reader, options.MaxColumnsPerTable, "row values");
        if (count != table.Columns.Count)
        {
            throw new ChangeTransactionEnvelopeException(
                "The transaction envelope row does not match its table metadata.");
        }

        var values = new ChangeColumnValue[count];
        for (var index = 0; index < values.Length; index++)
        {
            var state = (ChangeColumnState)reader.ReadByte();
            var encoding = (ChangeValueEncoding)reader.ReadByte();
            var data = ReadBytes(reader, options.MaxEnvelopeBytes);
            var error = ReadOptionalString(reader, options);
            values[index] = state switch
            {
                ChangeColumnState.Value when encoding != ChangeValueEncoding.None && error is null =>
                    ChangeColumnValue.FromValue(data.Span, encoding),
                ChangeColumnState.DatabaseNull when encoding == ChangeValueEncoding.None && data.IsEmpty && error is null =>
                    ChangeColumnValue.DatabaseNull,
                ChangeColumnState.NotPublished when encoding == ChangeValueEncoding.None && data.IsEmpty && error is null =>
                    ChangeColumnValue.NotPublished,
                ChangeColumnState.OldValueUnavailable when encoding == ChangeValueEncoding.None && data.IsEmpty && error is null =>
                    ChangeColumnValue.OldValueUnavailable,
                ChangeColumnState.UnchangedToast when encoding == ChangeValueEncoding.None && data.IsEmpty && error is null =>
                    ChangeColumnValue.UnchangedToast,
                ChangeColumnState.DecodingFailure when encoding != ChangeValueEncoding.None && error is not null =>
                    ChangeColumnValue.DecodingFailure(data.Span, encoding, error),
                _ => throw new ChangeTransactionEnvelopeException(
                    "The transaction envelope contains an invalid column-state combination."),
            };
        }

        return new ChangeRow(table, values);
    }

    private static ChangeTable ReadTableReference(BinaryReader reader, ChangeTable[] tables)
    {
        var token = reader.ReadInt32();
        if ((uint)token >= (uint)tables.Length)
        {
            throw new ChangeTransactionEnvelopeException(
                $"The transaction envelope contains invalid table token {token}.");
        }

        return tables[token];
    }

    private static void WriteString(
        BinaryWriter writer,
        string value,
        ChangeTransactionEnvelopeOptions options)
    {
        if (Encoding.UTF8.GetByteCount(value) > options.MaxStringBytes)
        {
            throw new ChangeTransactionEnvelopeException(
                $"A transaction envelope string exceeds {options.MaxStringBytes} UTF-8 bytes.");
        }

        writer.Write(value);
    }

    private static string ReadString(
        BinaryReader reader,
        ChangeTransactionEnvelopeOptions options)
    {
        var value = reader.ReadString();
        if (Encoding.UTF8.GetByteCount(value) > options.MaxStringBytes)
        {
            throw new ChangeTransactionEnvelopeException(
                $"A transaction envelope string exceeds {options.MaxStringBytes} UTF-8 bytes.");
        }

        return value;
    }

    private static void WriteOptionalString(
        BinaryWriter writer,
        string? value,
        ChangeTransactionEnvelopeOptions options)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteString(writer, value, options);
        }
    }

    private static string? ReadOptionalString(
        BinaryReader reader,
        ChangeTransactionEnvelopeOptions options) =>
        reader.ReadBoolean() ? ReadString(reader, options) : null;

    private static void WriteBytes(BinaryWriter writer, ReadOnlySpan<byte> data)
    {
        writer.Write(data.Length);
        writer.Write(data);
    }

    private static ReadOnlyMemory<byte> ReadBytes(BinaryReader reader, int maximum)
    {
        var length = ReadCount(reader, maximum, "bytes");
        var data = reader.ReadBytes(length);
        if (data.Length != length)
        {
            throw new EndOfStreamException();
        }

        return data;
    }

    private static int ReadCount(BinaryReader reader, int maximum, string description)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new ChangeTransactionEnvelopeException(
                $"The transaction envelope contains invalid {description} count {count}.");
        }

        return count;
    }

    private static async IAsyncEnumerable<Change> EnumerateChanges(
        IEnumerable<Change> changes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return change;
        }
    }
}

public sealed class ChangeTransactionEnvelopeException : Exception
{
    public ChangeTransactionEnvelopeException(string message)
        : base(message)
    {
    }

    public ChangeTransactionEnvelopeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
