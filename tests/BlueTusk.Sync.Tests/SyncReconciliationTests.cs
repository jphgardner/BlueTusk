using System.Runtime.CompilerServices;
using System.Text;
using BlueTusk.Streams;

namespace BlueTusk.Sync.Tests;

public sealed class SyncReconciliationTests
{
    private static readonly string[] ContractKeys = ["a", "z"];

    [Fact]
    public async Task Count_mode_reports_cardinality_without_claiming_content_equality()
    {
        var source = Reader(("a", "source-a"), ("b", "source-b"));
        var destination = Destination(("a", "different-a"), ("b", "different-b"));
        var request = Request(SyncReconciliationMode.Count);

        var result = await SyncReconciler.ReconcileAsync(
            request,
            source,
            destination);

        Assert.True(result.IsMatch);
        Assert.Equal(2, result.SourceCount);
        Assert.Equal(2, result.DestinationCount);
        Assert.Equal(0, result.MatchedKeys);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public async Task Partitioned_hash_repair_is_bounded_idempotent_and_requires_verification()
    {
        var source = Reader(
            ("a", "same"),
            ("b", "source-only"),
            ("c", "source-current"));
        var destination = Destination(
            ("a", "same"),
            ("c", "destination-stale"),
            ("d", "destination-only"));
        var request = Request(SyncReconciliationMode.PartitionedContentHash) with
        {
            PartitionCount = 1,
            Repair = true,
            RepairBatchSize = 2,
            MaxReportedDifferences = 2,
        };

        var result = await SyncReconciler.ReconcileAsync(
            request,
            source,
            destination);

        Assert.False(result.IsMatch);
        Assert.True(result.RequiresVerification);
        Assert.True(result.DifferenceReportTruncated);
        Assert.Equal(1, result.MatchedKeys);
        Assert.Equal(1, result.MissingFromDestination);
        Assert.Equal(1, result.ExtraInDestination);
        Assert.Equal(1, result.ContentMismatches);
        Assert.Equal(3, result.RepairedDifferences);
        Assert.Equal(2, destination.RepairCalls);

        var verification = await SyncReconciler.ReconcileAsync(
            request with { Repair = false, MaxReportedDifferences = 10 },
            source,
            destination);

        Assert.True(verification.IsMatch);
        Assert.False(verification.RequiresVerification);
        Assert.Equal(3, verification.MatchedKeys);
        Assert.Empty(verification.Differences);
    }

    [Fact]
    public async Task Repair_partition_memory_ceiling_fails_before_destination_mutation()
    {
        var source = Reader(("a", "a"), ("b", "b"));
        var destination = Destination();
        var request = Request(SyncReconciliationMode.KeySet) with
        {
            PartitionCount = 1,
            Repair = true,
            MaxBufferedRepairsPerPartition = 1,
        };

        var exception = await Assert.ThrowsAsync<SyncReconciliationException>(
            () => SyncReconciler.ReconcileAsync(request, source, destination).AsTask());

        Assert.Contains("memory ceiling", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, destination.RepairCalls);
    }

    [Fact]
    public async Task Reader_count_must_match_its_partitioned_view()
    {
        var source = new IncompleteReader([("a", "a")]);
        var destination = Destination(("a", "a"));

        var exception = await Assert.ThrowsAsync<SyncReconciliationException>(
            () => SyncReconciler.ReconcileAsync(
                Request(SyncReconciliationMode.KeySet),
                source,
                destination).AsTask());

        Assert.Contains("incomplete view", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repair_batch_rejects_ambiguous_duplicate_keys()
    {
        var document = new SyncRepairDocument("{}"u8.ToArray(), "application/json");

        var exception = Assert.Throws<ArgumentException>(() => new SyncRepairBatch(
            "reconciliation",
            "items",
            [SyncRepairMutation.Upsert("a", document), SyncRepairMutation.Delete("a")]));

        Assert.Contains("same logical key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_set_mode_ignores_content_but_reports_missing_and_extra_keys()
    {
        var source = Reader(("a", "source-a"), ("b", "source-b"));
        var destination = Destination(("a", "different-a"), ("c", "destination-c"));

        var result = await SyncReconciler.ReconcileAsync(
            Request(SyncReconciliationMode.KeySet),
            source,
            destination);

        Assert.False(result.IsMatch);
        Assert.Equal(1, result.MatchedKeys);
        Assert.Equal(1, result.MissingFromDestination);
        Assert.Equal(1, result.ExtraInDestination);
        Assert.Equal(0, result.ContentMismatches);
    }

    [Fact]
    public async Task Reader_contract_rejects_unsorted_or_wrong_partition_entries()
    {
        var source = new UnsortedReader();
        var destination = Destination(("a", "a"), ("z", "z"));

        var exception = await Assert.ThrowsAsync<SyncReconciliationException>(
            () => SyncReconciler.ReconcileAsync(
                Request(SyncReconciliationMode.KeySet) with { PartitionCount = 1 },
                source,
                destination).AsTask());

        Assert.Contains("deterministically ordered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pipeline_enters_reconciling_and_returns_to_its_previous_state()
    {
        var source = Reader(("a", "same"));
        var destination = Destination(("a", "same"));
        await using var pipeline = new SyncPipeline(
            new SyncPipelineOptions { PipelineId = "reconciliation" },
            new ChangeSourceIdentity("system", "database", "slot", "public:items"),
            new EmptyTransform(),
            destination);
        await pipeline.ProvisionAsync();

        var result = await pipeline.ReconcileAsync(
            Request(SyncReconciliationMode.PartitionedContentHash),
            source);

        Assert.True(result.IsMatch);
        Assert.Equal(SyncPipelineState.Running, pipeline.Status.State);
    }

    private static SyncReconciliationRequest Request(SyncReconciliationMode mode) =>
        new()
        {
            PipelineId = "reconciliation",
            Collection = "items",
            Mode = mode,
            PartitionCount = 4,
        };

    private static InMemoryReader Reader(params (string Key, string Content)[] values) =>
        new(values, includeRepairDocuments: true);

    private static InMemoryDestination Destination(params (string Key, string Content)[] values) =>
        new(values);

    private class InMemoryReader
        : ISyncReconciliationReader
    {
        protected readonly Dictionary<string, byte[]> Documents;
        private readonly bool _includeRepairDocuments;

        public InMemoryReader(
            IEnumerable<(string Key, string Content)> values,
            bool includeRepairDocuments)
        {
            _includeRepairDocuments = includeRepairDocuments;
            Documents = values.ToDictionary(
                static value => value.Key,
                static value => Encoding.UTF8.GetBytes(value.Content),
                StringComparer.Ordinal);
        }

        public virtual ValueTask<long> CountAsync(
            string pipelineId,
            string collection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult((long)Documents.Count);
        }

        public virtual async IAsyncEnumerable<SyncReconciliationEntry> ReadPartitionAsync(
            string pipelineId,
            string collection,
            int partitionIndex,
            int partitionCount,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            foreach (var (key, content) in Documents
                         .Where(pair =>
                             SyncReconciler.GetPartitionIndex(pair.Key, partitionCount) ==
                             partitionIndex)
                         .OrderBy(static pair => SyncReconciler.GetKeyHash(pair.Key))
                         .ThenBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return SyncReconciliationEntry.FromContent(
                    key,
                    content,
                    _includeRepairDocuments ? "text/plain" : null);
            }
        }
    }

    private sealed class IncompleteReader(IEnumerable<(string Key, string Content)> values)
        : InMemoryReader(values, includeRepairDocuments: true)
    {
        public override async ValueTask<long> CountAsync(
            string pipelineId,
            string collection,
            CancellationToken cancellationToken = default) =>
            await base.CountAsync(pipelineId, collection, cancellationToken) + 1;
    }

    private sealed class InMemoryDestination(
        IEnumerable<(string Key, string Content)> values)
        : InMemoryReader(values, includeRepairDocuments: false),
          ISyncDestination,
          ISyncRepairSink
    {
        public string Name => "in-memory";

        public int RepairCalls { get; private set; }

        public SyncDestinationCapabilities Capabilities =>
            SyncDestinationCapabilities.IdempotentUpserts |
            SyncDestinationCapabilities.Deletes |
            SyncDestinationCapabilities.Reconciliation;

        public ValueTask<SyncProvisionResult> ProvisionAsync(
            SyncProvisionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SyncProvisionResult(SyncProvisionStatus.Ready));

        public ValueTask ResetSnapshotAsync(
            string pipelineId,
            SnapshotReset reset,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StartSnapshotAsync(
            string pipelineId,
            SnapshotStart start,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ApplySnapshotBatchAsync(
            SyncSnapshotBatch batch,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask CompleteSnapshotAsync(
            string pipelineId,
            SnapshotComplete complete,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<SyncApplyResult> ApplyTransactionAsync(
            SyncTransactionBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SyncApplyResult.Applied(batch.Transaction.CommitEndPosition));

        public ValueTask ApplyRepairBatchAsync(
            SyncRepairBatch batch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RepairCalls++;
            foreach (var mutation in batch.Mutations)
            {
                if (mutation.Kind is SyncRepairMutationKind.Delete)
                {
                    Documents.Remove(mutation.Key);
                }
                else
                {
                    Documents[mutation.Key] = mutation.Document!.Content.ToArray();
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnsortedReader : InMemoryReader
    {
        public UnsortedReader()
            : base([], includeRepairDocuments: true)
        {
        }

        public override async IAsyncEnumerable<SyncReconciliationEntry> ReadPartitionAsync(
            string pipelineId,
            string collection,
            int partitionIndex,
            int partitionCount,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var key in ContractKeys
                         .OrderByDescending(SyncReconciler.GetKeyHash))
            {
                yield return SyncReconciliationEntry.FromContent(
                    key,
                    Encoding.UTF8.GetBytes(key),
                    "text/plain");
            }
        }
    }

    private sealed class EmptyTransform : ISyncTransform
    {
        public SyncTransformVersion Version { get; } =
            SyncTransformVersion.Create("reconciliation", "v1");

        public ValueTask<IReadOnlyList<SyncMutation>> TransformTransactionAsync(
            ChangeTransaction transaction,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncMutation>>([]);

        public ValueTask<IReadOnlyList<SyncSnapshotMutation>> TransformSnapshotBatchAsync(
            ChangeSnapshotBatch batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SyncSnapshotMutation>>([]);
    }
}
