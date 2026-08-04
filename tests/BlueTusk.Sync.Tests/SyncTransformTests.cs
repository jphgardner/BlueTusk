using System.Text;
using System.Text.Json;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.Tests;

public sealed class SyncTransformTests
{
    private static readonly ChangeSourceIdentity Source =
        new("sync-transform-system", "sync-transform-db", "sync-transform-slot", "public:orders");

    [Fact]
    public async Task Composite_transform_filters_mutations_and_fingerprints_ordered_stages()
    {
        await using var delivery = Delivery();
        var firstMutation = Mutation(delivery.Transaction, 0, "keep");
        var secondMutation = Mutation(delivery.Transaction, 1, "drop");
        var source = new RecordingTransform([firstMutation, secondMutation]);
        var keep = Predicate("keep", mutation => mutation.Key == "keep");
        var upserts = Predicate("upserts", mutation => mutation.Kind is SyncMutationKind.Upsert);
        var transform = new CompositeSyncTransform("orders", "v1", source, [keep, upserts]);
        var same = new CompositeSyncTransform("orders", "v1", source, [keep, upserts]);
        var reordered = new CompositeSyncTransform("orders", "v1", source, [upserts, keep]);

        var result = await transform.TransformTransactionAsync(delivery.Transaction);

        var retained = Assert.Single(result);
        Assert.Equal(firstMutation.ChangeId, retained.ChangeId);
        Assert.Equal(transform.Version, same.Version);
        Assert.NotEqual(transform.Version, reordered.Version);
    }

    [Fact]
    public async Task Json_stage_redacts_enriches_flattens_and_routes_transactions_and_snapshots()
    {
        await using var delivery = Delivery();
        var epoch = SnapshotEpoch.Create(Source, new BlueTuskLogSequenceNumber(100));
        var snapshot = SnapshotBatch(epoch);
        var stage = new JsonSyncTransformStage(new JsonSyncTransformStageOptions
        {
            Name = "secure-orders",
            Version = "v3",
            RedactedPaths = ["secret", "profile.secret"],
            EnrichmentJson = new Dictionary<string, string>
            {
                ["metadata"] = "{\"verified\":true}",
                ["region"] = "\"eu\"",
            },
            FlattenObjects = true,
            FlattenSeparator = "__",
            TenantPropertyPath = "tenant.id",
        });
        var content = Encoding.UTF8.GetBytes(
            "{\"tenant\":{\"id\":\"acme\"},\"profile\":{\"name\":\"Ada\",\"secret\":\"hidden\"},\"secret\":\"hidden\"}");
        var transactionMutation = new SyncMutation(
            new ChangeId(Source, delivery.Transaction.CommitEndPosition, delivery.Transaction.TransactionId, 0),
            SyncMutationKind.Upsert,
            "orders",
            "42",
            content,
            "application/json");
        var snapshotMutation = new SyncSnapshotMutation(
            new SnapshotRowId(epoch.Value, "public.orders", "42"),
            "orders",
            "42",
            content,
            "application/json");

        var transactionResult = Assert.Single(
            await stage.TransformTransactionAsync(delivery.Transaction, [transactionMutation]));
        var snapshotResult = Assert.Single(
            await stage.TransformSnapshotBatchAsync(snapshot, [snapshotMutation]));

        Assert.Equal("acme", transactionResult.PartitionKey);
        Assert.Equal("acme", snapshotResult.PartitionKey);
        Assert.Equal(transactionMutation.ChangeId, transactionResult.ChangeId);
        Assert.Equal(snapshotMutation.RowId, snapshotResult.RowId);
        AssertMaterialisedJson(transactionResult.Content.Span);
        AssertMaterialisedJson(snapshotResult.Content.Span);
    }

    [Fact]
    public async Task Json_stage_rejects_invalid_or_unsafe_materialisation()
    {
        await using var delivery = Delivery();
        var stage = new JsonSyncTransformStage(new JsonSyncTransformStageOptions
        {
            Name = "tenant-orders",
            Version = "v1",
            TenantPropertyPath = "tenant.id",
            MaximumDocumentBytes = 64,
        });

        await Assert.ThrowsAsync<SyncPoisonRecordException>(() => stage.TransformTransactionAsync(
            delivery.Transaction,
            [Mutation(delivery.Transaction, 0, "invalid", "not-json"u8.ToArray())]).AsTask());
        await Assert.ThrowsAsync<SyncPoisonRecordException>(() => stage.TransformTransactionAsync(
            delivery.Transaction,
            [Mutation(delivery.Transaction, 0, "missing", "{\"value\":\"missing tenant\"}"u8.ToArray())]).AsTask());
        await Assert.ThrowsAsync<SyncPoisonRecordException>(() => stage.TransformTransactionAsync(
            delivery.Transaction,
            [new SyncMutation(
                new ChangeId(Source, delivery.Transaction.CommitEndPosition, delivery.Transaction.TransactionId, 0),
                SyncMutationKind.Delete,
                "orders",
                "42",
                ReadOnlyMemory<byte>.Empty)]).AsTask());
        await Assert.ThrowsAsync<SyncPoisonRecordException>(() => stage.TransformTransactionAsync(
            delivery.Transaction,
            [new SyncMutation(
                new ChangeId(Source, delivery.Transaction.CommitEndPosition, delivery.Transaction.TransactionId, 0),
                SyncMutationKind.DeleteCollection,
                "orders",
                null,
                ReadOnlyMemory<byte>.Empty,
                partitionKey: "acme")]).AsTask());
    }

    [Fact]
    public async Task Composite_transform_rejects_stage_generated_identifiers()
    {
        await using var delivery = Delivery();
        var source = new RecordingTransform([Mutation(delivery.Transaction, 0, "42")]);
        var transform = new CompositeSyncTransform(
            "orders",
            "v1",
            source,
            [new IdentifierChangingStage()]);

        await Assert.ThrowsAsync<SyncPoisonRecordException>(
            () => transform.TransformTransactionAsync(delivery.Transaction).AsTask());
    }

    [Fact]
    public void Json_stage_rejects_flattening_collisions_in_configuration()
    {
        Assert.Throws<ArgumentException>(() => new JsonSyncTransformStage(
            new JsonSyncTransformStageOptions
            {
                Name = "orders",
                Version = "v1",
                FlattenObjects = true,
                FlattenSeparator = "__",
                EnrichmentJson = new Dictionary<string, string>
                {
                    ["metadata__verified"] = "true",
                },
            }));
    }

    [Fact]
    public async Task Sandbox_executes_finite_json_program_and_preserves_source_identity()
    {
        await using var delivery = Delivery();
        var epoch = SnapshotEpoch.Create(Source, new BlueTuskLogSequenceNumber(100));
        var snapshot = SnapshotBatch(epoch);
        var stage = Sandbox(
            SyncSandboxInstruction.RequireEquals("kind", "\"order\""),
            SyncSandboxInstruction.Copy("customer.name", "displayName"),
            SyncSandboxInstruction.Set("metadata.source", "\"cdc\""),
            SyncSandboxInstruction.Remove("secret"),
            SyncSandboxInstruction.Route("tenant.id"),
            SyncSandboxInstruction.DropWhenEquals("status", "\"cancelled\""));
        var retained = Mutation(
            delivery.Transaction,
            0,
            "42",
            """{"kind":"order","status":"open","tenant":{"id":"acme"},"customer":{"name":"Ada"},"secret":"hidden"}"""u8.ToArray());
        var dropped = Mutation(
            delivery.Transaction,
            1,
            "43",
            """{"kind":"order","status":"cancelled","tenant":{"id":"acme"},"customer":{"name":"Grace"}}"""u8.ToArray());
        var snapshotMutation = new SyncSnapshotMutation(
            new SnapshotRowId(epoch.Value, "public.orders", "42"),
            "orders",
            "42",
            retained.Content,
            "application/json");

        var transactionResult = Assert.Single(
            await stage.TransformTransactionAsync(delivery.Transaction, [retained, dropped]));
        var snapshotResult = Assert.Single(
            await stage.TransformSnapshotBatchAsync(snapshot, [snapshotMutation]));

        Assert.Equal(retained.ChangeId, transactionResult.ChangeId);
        Assert.Equal(snapshotMutation.RowId, snapshotResult.RowId);
        Assert.Equal("acme", transactionResult.PartitionKey);
        Assert.Equal("acme", snapshotResult.PartitionKey);
        AssertSandboxJson(transactionResult.Content.Span);
        AssertSandboxJson(snapshotResult.Content.Span);
    }

    [Fact]
    public void Sandbox_fingerprint_is_canonical_and_program_order_sensitive()
    {
        var canonical = Sandbox(
            SyncSandboxInstruction.Set("metadata", """{"verified":true,"region":"eu"}"""),
            SyncSandboxInstruction.Remove("secret"));
        var reorderedJson = Sandbox(
            SyncSandboxInstruction.Set("metadata", """{"region":"eu","verified":true}"""),
            SyncSandboxInstruction.Remove("secret"));
        var reorderedProgram = Sandbox(
            SyncSandboxInstruction.Remove("secret"),
            SyncSandboxInstruction.Set("metadata", """{"region":"eu","verified":true}"""));

        Assert.Equal(canonical.Version, reorderedJson.Version);
        Assert.NotEqual(canonical.Version, reorderedProgram.Version);
    }

    [Fact]
    public async Task Sandbox_enforces_operation_byte_cancellation_and_delete_boundaries()
    {
        await using var delivery = Delivery();
        var operationBound = Sandbox(
            new SyncTransformSandboxOptions
            {
                Name = "sandbox-orders",
                Version = "v1",
                Instructions =
                [
                    SyncSandboxInstruction.Set("first", "1"),
                    SyncSandboxInstruction.Set("second", "2"),
                ],
                MaximumOperationsPerBatch = 1,
            });
        await Assert.ThrowsAsync<SyncPoisonRecordException>(() =>
            operationBound.TransformTransactionAsync(
                delivery.Transaction,
                [Mutation(delivery.Transaction, 0, "42")]).AsTask());

        var byteBound = Sandbox(
            new SyncTransformSandboxOptions
            {
                Name = "sandbox-orders",
                Version = "v1",
                Instructions = [SyncSandboxInstruction.Set("value", "\"expanded\"")],
                MaximumDocumentBytes = 32,
                MaximumBatchBytes = 4,
            });
        await Assert.ThrowsAsync<SyncPoisonRecordException>(() =>
            byteBound.TransformTransactionAsync(
                delivery.Transaction,
                [Mutation(delivery.Transaction, 0, "42")]).AsTask());

        var routed = Sandbox(SyncSandboxInstruction.Route("tenant"));
        await Assert.ThrowsAsync<SyncPoisonRecordException>(() =>
            routed.TransformTransactionAsync(
                delivery.Transaction,
                [new SyncMutation(
                    new ChangeId(Source, delivery.Transaction.CommitEndPosition, delivery.Transaction.TransactionId, 0),
                    SyncMutationKind.Delete,
                    "orders",
                    "42",
                    ReadOnlyMemory<byte>.Empty)]).AsTask());

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            routed.TransformTransactionAsync(
                delivery.Transaction,
                [Mutation(delivery.Transaction, 0, "42")],
                cancellation.Token).AsTask());
    }

    private static void AssertMaterialisedJson(ReadOnlySpan<byte> content)
    {
        using var document = JsonDocument.Parse(content.ToArray());
        var root = document.RootElement;
        Assert.Equal("acme", root.GetProperty("tenant__id").GetString());
        Assert.Equal("Ada", root.GetProperty("profile__name").GetString());
        Assert.Equal("eu", root.GetProperty("region").GetString());
        Assert.True(root.GetProperty("metadata__verified").GetBoolean());
        Assert.False(root.TryGetProperty("profile__secret", out _));
        Assert.False(root.TryGetProperty("secret", out _));
    }

    private static void AssertSandboxJson(ReadOnlySpan<byte> content)
    {
        using var document = JsonDocument.Parse(content.ToArray());
        var root = document.RootElement;
        Assert.Equal("Ada", root.GetProperty("displayName").GetString());
        Assert.Equal("cdc", root.GetProperty("metadata").GetProperty("source").GetString());
        Assert.False(root.TryGetProperty("secret", out _));
    }

    private static SandboxedSyncTransformStage Sandbox(
        params SyncSandboxInstruction[] instructions) =>
        Sandbox(new SyncTransformSandboxOptions
        {
            Name = "sandbox-orders",
            Version = "v1",
            Instructions = instructions,
        });

    private static SandboxedSyncTransformStage Sandbox(
        SyncTransformSandboxOptions options) =>
        new(options);

    private static SyncPredicateTransformStage Predicate(
        string name,
        Func<SyncMutation, bool> predicate) =>
        new(
            SyncTransformVersion.Create(name, "v1"),
            predicate,
            static _ => true);

    private static SyncMutation Mutation(
        ChangeTransaction transaction,
        int ordinal,
        string key,
        ReadOnlyMemory<byte> content = default) =>
        new(
            new ChangeId(Source, transaction.CommitEndPosition, transaction.TransactionId, ordinal),
            SyncMutationKind.Upsert,
            "orders",
            key,
            content.IsEmpty ? "{}"u8.ToArray() : content,
            "application/json");

    private static ChangeTransactionDelivery Delivery() =>
        ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            transactionId: 42,
            new BlueTuskLogSequenceNumber(105));

    private static ChangeSnapshotBatch SnapshotBatch(SnapshotEpoch epoch) =>
        new(
            epoch,
            new ChangeTable(
                7,
                "public",
                "orders",
                'd',
                [new ChangeColumn(0, "id", 23, -1, true)]),
            0,
            [],
            true);

    private sealed class RecordingTransform(IReadOnlyList<SyncMutation> mutations) : ISyncTransform
    {
        public SyncTransformVersion Version { get; } =
            SyncTransformVersion.Create("source-orders", "v1");

        public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(mutations);

        public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncSnapshotMutation>>([]);
    }

    private sealed class IdentifierChangingStage : ISyncTransformStage
    {
        public SyncTransformVersion Version { get; } =
            SyncTransformVersion.Create("bad-stage", "v1");

        public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
            ChangeTransaction transaction,
            IReadOnlyList<SyncMutation> mutations,
            CancellationToken cancellationToken = default)
        {
            var mutation = Assert.Single(mutations);
            return ValueTask.FromResult<IReadOnlyList<SyncMutation>>(
                [new SyncMutation(
                    mutation.ChangeId with { Ordinal = mutation.ChangeId.Ordinal + 1 },
                    mutation.Kind,
                    mutation.Collection,
                    mutation.Key,
                    mutation.Content,
                    mutation.ContentType,
                    mutation.PartitionKey)]);
        }

        public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            IReadOnlyList<SyncSnapshotMutation> mutations,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(mutations);
    }
}
