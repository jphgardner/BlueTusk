using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.Nats;

internal static class NatsSyncEnvelopeCodec
{
    internal const int CurrentFormatVersion = NatsSyncEnvelopeReader.CurrentFormatVersion;

    private const int IntegrityLength = 32;
    private const int MaximumMutationCount = 1_000_000;
    private static ReadOnlySpan<byte> Magic => "BTSN"u8;

    internal static byte[] EncodeTransaction(SyncTransactionBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var writer = Start(
            NatsSyncEnvelopeKind.Transaction,
            batch.PipelineId,
            batch.Transform,
            batch.Transaction.Source);
        WriteUInt32(writer, batch.Transaction.TransactionId);
        WriteUInt64(writer, batch.Transaction.CommitEndPosition.Value);
        WriteInt64(writer, batch.Transaction.CommitTimestamp.UtcTicks);
        WriteByte(writer, (byte)batch.Transaction.Outcome);
        WriteString(writer, batch.Transaction.GlobalTransactionId);
        WriteInt32(writer, batch.Mutations.Count);
        foreach (var mutation in batch.Mutations)
        {
            WriteMutation(
                writer,
                StableChangeId(mutation.ChangeId),
                mutation.Kind,
                mutation.Collection,
                mutation.Key,
                mutation.Content,
                mutation.ContentType,
                mutation.PartitionKey);
        }

        return Finish(writer);
    }

    internal static byte[] EncodeSnapshotReset(
        string pipelineId,
        SyncTransformVersion transform,
        SnapshotReset reset)
    {
        ArgumentNullException.ThrowIfNull(reset);
        var writer = Start(
            NatsSyncEnvelopeKind.SnapshotReset,
            pipelineId,
            transform,
            reset.Epoch.Source);
        WriteSnapshotIdentity(writer, reset.Epoch);
        WriteNullableGuid(writer, reset.AbandonedEpoch);
        WriteString(writer, reset.Reason);
        return Finish(writer);
    }

    internal static byte[] EncodeSnapshotStart(
        string pipelineId,
        SyncTransformVersion transform,
        SnapshotStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var writer = Start(
            NatsSyncEnvelopeKind.SnapshotStart,
            pipelineId,
            transform,
            start.Epoch.Source);
        WriteSnapshotIdentity(writer, start.Epoch);
        WriteInt32(writer, start.TableCount);
        return Finish(writer);
    }

    internal static byte[] EncodeSnapshotBatch(SyncSnapshotBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var sourceBatch = batch.SourceBatch;
        var writer = Start(
            NatsSyncEnvelopeKind.SnapshotBatch,
            batch.PipelineId,
            batch.Transform,
            sourceBatch.Epoch.Source);
        WriteSnapshotIdentity(writer, sourceBatch.Epoch);
        WriteString(writer, sourceBatch.Table.Schema + "." + sourceBatch.Table.Name);
        WriteInt64(writer, sourceBatch.Sequence);
        WriteByte(writer, sourceBatch.IsLastForTable ? (byte)1 : (byte)0);
        WriteInt32(writer, batch.Mutations.Count);
        foreach (var mutation in batch.Mutations)
        {
            WriteMutation(
                writer,
                StableSnapshotId(mutation.RowId),
                SyncMutationKind.Upsert,
                mutation.Collection,
                mutation.Key,
                mutation.Content,
                mutation.ContentType,
                mutation.PartitionKey);
        }

        return Finish(writer);
    }

    internal static byte[] EncodeSnapshotComplete(
        string pipelineId,
        SyncTransformVersion transform,
        SnapshotComplete complete)
    {
        ArgumentNullException.ThrowIfNull(complete);
        var writer = Start(
            NatsSyncEnvelopeKind.SnapshotComplete,
            pipelineId,
            transform,
            complete.Epoch.Source);
        WriteSnapshotIdentity(writer, complete.Epoch);
        WriteInt64(writer, complete.RowCount);
        WriteInt32(writer, complete.TableCount);
        return Finish(writer);
    }

    internal static NatsSyncEnvelope Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Magic.Length + sizeof(ushort) + 2 + IntegrityLength)
        {
            throw new NatsSyncEnvelopeException("The NATS Sync envelope is truncated.");
        }

        var content = payload[..^IntegrityLength];
        Span<byte> actualIntegrity = stackalloc byte[IntegrityLength];
        _ = SHA256.HashData(content, actualIntegrity);
        if (!CryptographicOperations.FixedTimeEquals(actualIntegrity, payload[^IntegrityLength..]))
        {
            throw new NatsSyncEnvelopeException("The NATS Sync envelope integrity check failed.");
        }

        try
        {
            var reader = new EnvelopeReader(content);
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            {
                throw new NatsSyncEnvelopeException("The NATS Sync envelope magic is invalid.");
            }

            var version = reader.ReadUInt16();
            if (version != CurrentFormatVersion)
            {
                throw new NatsSyncEnvelopeException(
                    $"NATS Sync envelope format {version} is unsupported; this build requires format {CurrentFormatVersion}.");
            }

            var kind = (NatsSyncEnvelopeKind)reader.ReadByte();
            if (!Enum.IsDefined(kind))
            {
                throw new NatsSyncEnvelopeException($"NATS Sync envelope kind {(byte)kind} is invalid.");
            }

            if (reader.ReadByte() != 0)
            {
                throw new NatsSyncEnvelopeException("The NATS Sync envelope reserved header byte is nonzero.");
            }

            var pipelineId = reader.ReadRequiredString();
            var transform = new SyncTransformVersion(
                reader.ReadRequiredString(),
                reader.ReadRequiredString());
            var source = new ChangeSourceIdentity(
                reader.ReadRequiredString(),
                reader.ReadRequiredString(),
                reader.ReadRequiredString(),
                reader.ReadRequiredString());
            NatsSyncTransaction? transaction = null;
            NatsSyncSnapshot? snapshot = null;
            IReadOnlyList<NatsSyncMutation> mutations = [];
            switch (kind)
            {
                case NatsSyncEnvelopeKind.Transaction:
                    transaction = new NatsSyncTransaction(
                        reader.ReadUInt32(),
                        new BlueTuskLogSequenceNumber(reader.ReadUInt64()),
                        ReadTimestamp(ref reader),
                        ReadOutcome(ref reader),
                        reader.ReadString());
                    mutations = ReadMutations(ref reader);
                    break;

                case NatsSyncEnvelopeKind.SnapshotReset:
                    var resetIdentity = ReadSnapshotIdentity(ref reader);
                    snapshot = new NatsSyncSnapshot(
                        resetIdentity.Epoch,
                        resetIdentity.Position,
                        resetIdentity.StartedAt,
                        reader.ReadNullableGuid(),
                        reader.ReadRequiredString(),
                        null,
                        null,
                        null,
                        null,
                        null);
                    break;

                case NatsSyncEnvelopeKind.SnapshotStart:
                    var startIdentity = ReadSnapshotIdentity(ref reader);
                    snapshot = new NatsSyncSnapshot(
                        startIdentity.Epoch,
                        startIdentity.Position,
                        startIdentity.StartedAt,
                        null,
                        null,
                        reader.ReadInt32(),
                        null,
                        null,
                        null,
                        null);
                    break;

                case NatsSyncEnvelopeKind.SnapshotBatch:
                    var batchIdentity = ReadSnapshotIdentity(ref reader);
                    var tableIdentity = reader.ReadRequiredString();
                    var sequence = reader.ReadInt64();
                    var isLast = reader.ReadBoolean();
                    snapshot = new NatsSyncSnapshot(
                        batchIdentity.Epoch,
                        batchIdentity.Position,
                        batchIdentity.StartedAt,
                        null,
                        null,
                        null,
                        null,
                        tableIdentity,
                        sequence,
                        isLast);
                    mutations = ReadMutations(ref reader);
                    break;

                case NatsSyncEnvelopeKind.SnapshotComplete:
                    var completeIdentity = ReadSnapshotIdentity(ref reader);
                    var rowCount = reader.ReadInt64();
                    var tableCount = reader.ReadInt32();
                    snapshot = new NatsSyncSnapshot(
                        completeIdentity.Epoch,
                        completeIdentity.Position,
                        completeIdentity.StartedAt,
                        null,
                        null,
                        tableCount,
                        rowCount,
                        null,
                        null,
                        null);
                    break;

                default:
                    throw new NatsSyncEnvelopeException($"NATS Sync envelope kind {(byte)kind} is invalid.");
            }

            reader.EnsureComplete();
            return new NatsSyncEnvelope(
                version,
                kind,
                pipelineId,
                transform,
                source,
                transaction,
                snapshot,
                mutations);
        }
        catch (NatsSyncEnvelopeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new NatsSyncEnvelopeException("The NATS Sync envelope contains invalid data.", exception);
        }
    }

    private static ArrayBufferWriter<byte> Start(
        NatsSyncEnvelopeKind kind,
        string pipelineId,
        SyncTransformVersion transform,
        ChangeSourceIdentity source)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteBytes(writer, Magic);
        WriteUInt16(writer, CurrentFormatVersion);
        WriteByte(writer, (byte)kind);
        WriteByte(writer, 0);
        WriteString(writer, pipelineId);
        WriteString(writer, transform.Name);
        WriteString(writer, transform.Fingerprint);
        WriteString(writer, source.SystemIdentifier);
        WriteString(writer, source.DatabaseName);
        WriteString(writer, source.SlotName);
        WriteString(writer, source.PublicationFingerprint);
        return writer;
    }

    private static byte[] Finish(ArrayBufferWriter<byte> writer)
    {
        Span<byte> integrity = stackalloc byte[IntegrityLength];
        _ = SHA256.HashData(writer.WrittenSpan, integrity);
        WriteBytes(writer, integrity);
        return writer.WrittenSpan.ToArray();
    }

    private static void WriteSnapshotIdentity(ArrayBufferWriter<byte> writer, SnapshotEpoch epoch)
    {
        WriteGuid(writer, epoch.Value);
        WriteUInt64(writer, epoch.ConsistentPosition.Value);
        WriteInt64(writer, epoch.StartedAt.UtcTicks);
    }

    private static (Guid Epoch, BlueTuskLogSequenceNumber Position, DateTimeOffset StartedAt)
        ReadSnapshotIdentity(ref EnvelopeReader reader) =>
        (
            reader.ReadGuid(),
            new BlueTuskLogSequenceNumber(reader.ReadUInt64()),
            ReadTimestamp(ref reader));

    private static DateTimeOffset ReadTimestamp(ref EnvelopeReader reader) =>
        new(reader.ReadInt64(), TimeSpan.Zero);

    private static ChangeTransactionOutcome ReadOutcome(ref EnvelopeReader reader)
    {
        var outcome = (ChangeTransactionOutcome)reader.ReadByte();
        if (!Enum.IsDefined(outcome))
        {
            throw new NatsSyncEnvelopeException($"Transaction outcome {(byte)outcome} is invalid.");
        }

        return outcome;
    }

    private static NatsSyncMutation[] ReadMutations(ref EnvelopeReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumMutationCount)
        {
            throw new NatsSyncEnvelopeException($"Mutation count {count} is outside the supported range.");
        }

        var mutations = new NatsSyncMutation[count];
        for (var index = 0; index < count; index++)
        {
            var stableId = reader.ReadRequiredString();
            var kind = (SyncMutationKind)reader.ReadByte();
            if (!Enum.IsDefined(kind))
            {
                throw new NatsSyncEnvelopeException($"Mutation kind {(byte)kind} is invalid.");
            }

            mutations[index] = new NatsSyncMutation(
                stableId,
                kind,
                reader.ReadRequiredString(),
                reader.ReadString(),
                reader.ReadMemory(),
                reader.ReadString(),
                reader.ReadString());
        }

        return mutations;
    }

    private static void WriteMutation(
        ArrayBufferWriter<byte> writer,
        string stableId,
        SyncMutationKind kind,
        string collection,
        string? key,
        ReadOnlyMemory<byte> content,
        string? contentType,
        string? partitionKey)
    {
        WriteString(writer, stableId);
        WriteByte(writer, (byte)kind);
        WriteString(writer, collection);
        WriteString(writer, key);
        WriteMemory(writer, content);
        WriteString(writer, contentType);
        WriteString(writer, partitionKey);
    }

    private static string StableChangeId(ChangeId id) =>
        $"{id.Source.Fingerprint}:{id.CommitEndPosition.Value:x16}:{id.TransactionId:x8}:{id.Ordinal:x8}";

    private static string StableSnapshotId(SnapshotRowId id) =>
        $"{id.Epoch:N}:{id.TableIdentity}:{id.KeyIdentity}";

    private static void WriteString(ArrayBufferWriter<byte> writer, string? value)
    {
        if (value is null)
        {
            WriteInt32(writer, -1);
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteInt32(writer, byteCount);
        var destination = writer.GetSpan(byteCount);
        _ = Encoding.UTF8.GetBytes(value, destination);
        writer.Advance(byteCount);
    }

    private static void WriteMemory(ArrayBufferWriter<byte> writer, ReadOnlyMemory<byte> value)
    {
        WriteInt32(writer, value.Length);
        WriteBytes(writer, value.Span);
    }

    private static void WriteNullableGuid(ArrayBufferWriter<byte> writer, Guid? value)
    {
        WriteByte(writer, value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            WriteGuid(writer, value.Value);
        }
    }

    private static void WriteGuid(ArrayBufferWriter<byte> writer, Guid value)
    {
        var destination = writer.GetSpan(16);
        if (!value.TryWriteBytes(destination, bigEndian: true, out var bytesWritten) || bytesWritten != 16)
        {
            throw new NatsSyncEnvelopeException("Unable to encode a snapshot identifier.");
        }

        writer.Advance(bytesWritten);
    }

    private static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    private static void WriteUInt16(ArrayBufferWriter<byte> writer, int value)
    {
        var destination = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(destination, checked((ushort)value));
        writer.Advance(sizeof(ushort));
    }

    private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
    {
        var destination = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(destination, value);
        writer.Advance(sizeof(int));
    }

    private static void WriteUInt32(ArrayBufferWriter<byte> writer, uint value)
    {
        var destination = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        writer.Advance(sizeof(uint));
    }

    private static void WriteInt64(ArrayBufferWriter<byte> writer, long value)
    {
        var destination = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(destination, value);
        writer.Advance(sizeof(long));
    }

    private static void WriteUInt64(ArrayBufferWriter<byte> writer, ulong value)
    {
        var destination = writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
        writer.Advance(sizeof(ulong));
    }

    private static void WriteBytes(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }

    private ref struct EnvelopeReader
    {
        private readonly ReadOnlySpan<byte> _payload;
        private int _offset;

        internal EnvelopeReader(ReadOnlySpan<byte> payload)
        {
            _payload = payload;
            _offset = 0;
        }

        internal byte ReadByte() => ReadBytes(1)[0];

        internal bool ReadBoolean()
        {
            var value = ReadByte();
            if (value > 1)
            {
                throw new NatsSyncEnvelopeException($"Boolean value {value} is invalid.");
            }

            return value == 1;
        }

        internal ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(sizeof(ushort)));

        internal int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(sizeof(int)));

        internal uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint)));

        internal long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(sizeof(long)));

        internal ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(sizeof(ulong)));

        internal Guid ReadGuid() => new(ReadBytes(16), bigEndian: true);

        internal Guid? ReadNullableGuid() => ReadBoolean() ? ReadGuid() : null;

        internal string ReadRequiredString() =>
            ReadString() ?? throw new NatsSyncEnvelopeException("A required string is null.");

        internal string? ReadString()
        {
            var length = ReadInt32();
            if (length == -1)
            {
                return null;
            }

            if (length < 0)
            {
                throw new NatsSyncEnvelopeException($"String length {length} is invalid.");
            }

            return Encoding.UTF8.GetString(ReadBytes(length));
        }

        internal ReadOnlyMemory<byte> ReadMemory()
        {
            var length = ReadInt32();
            if (length < 0)
            {
                throw new NatsSyncEnvelopeException($"Content length {length} is invalid.");
            }

            return ReadBytes(length).ToArray();
        }

        internal ReadOnlySpan<byte> ReadBytes(int length)
        {
            if (length < 0 || length > _payload.Length - _offset)
            {
                throw new NatsSyncEnvelopeException("The NATS Sync envelope is truncated.");
            }

            var value = _payload.Slice(_offset, length);
            _offset += length;
            return value;
        }

        internal void EnsureComplete()
        {
            if (_offset != _payload.Length)
            {
                throw new NatsSyncEnvelopeException(
                    $"The NATS Sync envelope contains {_payload.Length - _offset} trailing bytes.");
            }
        }
    }
}
