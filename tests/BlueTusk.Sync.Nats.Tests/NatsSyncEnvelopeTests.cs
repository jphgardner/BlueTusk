using System.Buffers.Binary;
using System.Security.Cryptography;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.Nats.Tests;

public sealed class NatsSyncEnvelopeTests
{
    private static readonly ChangeSourceIdentity Source =
        new("nats-system", "nats-database", "nats-slot", "public:orders");

    [Fact]
    public async Task Transaction_envelope_round_trips_identity_mutations_and_integrity()
    {
        var transform = SyncTransformVersion.Create("orders", "v1");
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            42,
            new BlueTuskLogSequenceNumber(105));
        var batch = new SyncTransactionBatch(
            "orders",
            transform,
            delivery.Transaction,
            [
                new SyncMutation(
                    new ChangeId(Source, new BlueTuskLogSequenceNumber(105), 42, 0),
                    SyncMutationKind.Upsert,
                    "orders",
                    "42",
                    "{\"status\":\"new\"}"u8.ToArray(),
                    "application/json",
                    "tenant-1"),
                new SyncMutation(
                    new ChangeId(Source, new BlueTuskLogSequenceNumber(105), 42, 1),
                    SyncMutationKind.Delete,
                    "orders",
                    "43",
                    ReadOnlyMemory<byte>.Empty),
            ]);

        var payload = NatsSyncEnvelopeCodec.EncodeTransaction(batch);
        var decoded = NatsSyncEnvelopeReader.Decode(payload);

        Assert.Equal(NatsSyncEnvelopeReader.CurrentFormatVersion, decoded.FormatVersion);
        Assert.Equal(NatsSyncEnvelopeKind.Transaction, decoded.Kind);
        Assert.Equal("orders", decoded.PipelineId);
        Assert.Equal(transform, decoded.Transform);
        Assert.Equal(Source, decoded.Source);
        Assert.NotNull(decoded.Transaction);
        Assert.Equal(42u, decoded.Transaction.TransactionId);
        Assert.Equal(new BlueTuskLogSequenceNumber(105), decoded.Transaction.CommitEndPosition);
        Assert.Equal(2, decoded.Mutations.Count);
        Assert.Equal(SyncMutationKind.Upsert, decoded.Mutations[0].Kind);
        Assert.Equal("tenant-1", decoded.Mutations[0].PartitionKey);
        Assert.Equal("{\"status\":\"new\"}", System.Text.Encoding.UTF8.GetString(decoded.Mutations[0].Content.Span));
        Assert.Equal(SyncMutationKind.Delete, decoded.Mutations[1].Kind);
        Assert.Contains(Source.Fingerprint, decoded.Mutations[0].StableId, StringComparison.Ordinal);

        payload[payload.Length / 2] ^= 0x40;
        _ = Assert.Throws<NatsSyncEnvelopeException>(
            () => NatsSyncEnvelopeReader.Decode(payload));
    }

    [Fact]
    public async Task Reader_rejects_an_integrity_valid_future_format()
    {
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            42,
            new BlueTuskLogSequenceNumber(105));
        var payload = NatsSyncEnvelopeCodec.EncodeTransaction(
            new SyncTransactionBatch(
                "orders",
                SyncTransformVersion.Create("orders", "v1"),
                delivery.Transaction,
                []));

        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(4, sizeof(ushort)),
            checked((ushort)(NatsSyncEnvelopeReader.CurrentFormatVersion + 1)));
        SHA256.HashData(payload.AsSpan(0, payload.Length - 32))
            .CopyTo(payload.AsSpan(payload.Length - 32));

        var exception = Assert.Throws<NatsSyncEnvelopeException>(
            () => NatsSyncEnvelopeReader.Decode(payload));
        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_envelopes_round_trip_lifecycle_and_stable_row_identity()
    {
        var transform = SyncTransformVersion.Create("orders", "v1");
        var epoch = new SnapshotEpoch(
            Guid.Parse("88ebc9c9-276a-47c2-82d7-833f705e29c0"),
            Source,
            new BlueTuskLogSequenceNumber(200),
            new DateTimeOffset(2026, 8, 3, 10, 30, 0, TimeSpan.Zero));
        var table = new ChangeTable(
            7,
            "public",
            "orders",
            'd',
            [new ChangeColumn(0, "id", 23, -1, true)]);
        var sourceBatch = new ChangeSnapshotBatch(epoch, table, 4, [], true);
        var batch = new SyncSnapshotBatch(
            "orders",
            transform,
            sourceBatch,
            [new SyncSnapshotMutation(
                new SnapshotRowId(epoch.Value, "public.orders", "key-42"),
                "orders",
                "42",
                "{}"u8.ToArray(),
                "application/json")]);

        var decodedBatch = NatsSyncEnvelopeReader.Decode(
            NatsSyncEnvelopeCodec.EncodeSnapshotBatch(batch));
        var decodedReset = NatsSyncEnvelopeReader.Decode(
            NatsSyncEnvelopeCodec.EncodeSnapshotReset(
                "orders",
                transform,
                new SnapshotReset(epoch, Guid.NewGuid(), "exporter lost")));
        var decodedStart = NatsSyncEnvelopeReader.Decode(
            NatsSyncEnvelopeCodec.EncodeSnapshotStart(
                "orders",
                transform,
                new SnapshotStart(epoch, 1)));
        var decodedComplete = NatsSyncEnvelopeReader.Decode(
            NatsSyncEnvelopeCodec.EncodeSnapshotComplete(
                "orders",
                transform,
                new SnapshotComplete(epoch, 1, 1)));

        Assert.Equal(NatsSyncEnvelopeKind.SnapshotBatch, decodedBatch.Kind);
        Assert.Equal("public.orders", decodedBatch.Snapshot?.TableIdentity);
        Assert.Equal(4, decodedBatch.Snapshot?.BatchSequence);
        Assert.True(decodedBatch.Snapshot?.IsLastForTable);
        Assert.Contains("key-42", decodedBatch.Mutations[0].StableId, StringComparison.Ordinal);
        Assert.Equal("exporter lost", decodedReset.Snapshot?.Reason);
        Assert.Equal(1, decodedStart.Snapshot?.TableCount);
        Assert.Equal(1, decodedComplete.Snapshot?.RowCount);
        Assert.Equal(1, decodedComplete.Snapshot?.TableCount);
    }
}
