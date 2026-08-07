using System.Text;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.Testing.Tests;

public sealed class SyncDestinationConformanceSuiteTests
{
    [Fact]
    public async Task Shared_scenario_verifies_snapshot_restart_transform_and_quarantine()
    {
        var harness = new InMemoryHarness();

        var result = await SyncDestinationConformanceSuite.VerifyAsync(harness);

        Assert.Equal("conformance-memory", result.DestinationName);
        Assert.True(result.QuarantineVerified);
        Assert.Equal(4, harness.VerifiedStages.Count);
        Assert.Equal(
            [
                SyncDestinationConformanceStage.SnapshotApplied,
                SyncDestinationConformanceStage.SnapshotRestart,
                SyncDestinationConformanceStage.TransactionApplied,
                SyncDestinationConformanceStage.RestartRedelivery,
            ],
            harness.VerifiedStages);
    }

    [Fact]
    public async Task Shared_scenario_rejects_checkpoint_beyond_the_applied_transaction()
    {
        var harness = new InMemoryHarness(returnInvalidPosition: true);

        var exception = await Assert.ThrowsAsync<SyncDestinationConformanceException>(
            async () => await SyncDestinationConformanceSuite.VerifyAsync(harness));

        Assert.Contains("exact durable position", exception.Message, StringComparison.Ordinal);
    }

    private sealed class InMemoryHarness(bool returnInvalidPosition = false)
        : ISyncDestinationConformanceHarness
    {
        private readonly DurableState _state = new();

        public string PipelineId => "conformance";

        public ChangeSourceIdentity Source { get; } =
            new("testing-system", "testing-database", "testing-slot", "public:conformance");

        public List<SyncDestinationConformanceStage> VerifiedStages { get; } = [];

        public ValueTask<ISyncDestination> CreateDestinationAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISyncDestination>(
                new InMemoryDestination(_state, returnInvalidPosition));
        }

        public ValueTask VerifyDurableStateAsync(
            SyncDestinationConformanceStage stage,
            ISyncDestination destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsType<InMemoryDestination>(destination);
            Assert.True(_state.Documents.TryGetValue("conformance/42", out var content));
            var expected = stage is SyncDestinationConformanceStage.SnapshotApplied or
                SyncDestinationConformanceStage.SnapshotRestart
                ? "{\"stage\":\"snapshot\"}"
                : "{\"stage\":\"transaction\"}";
            Assert.Equal(expected, Encoding.UTF8.GetString(content));
            VerifiedStages.Add(stage);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DurableState
    {
        public Dictionary<string, byte[]> Documents { get; } = new(StringComparer.Ordinal);

        public HashSet<string> QuarantineRecords { get; } = new(StringComparer.Ordinal);

        public string? TransformFingerprint { get; set; }

        public BlueTuskLogSequenceNumber? Position { get; set; }
    }

    private sealed class InMemoryDestination(DurableState state, bool returnInvalidPosition)
        : ISyncDestination, ISyncQuarantineSink
    {
        public string Name => "conformance-memory";

        public SyncDestinationCapabilities Capabilities =>
            SyncDestinationCapabilities.TransactionalBatches |
            SyncDestinationCapabilities.IdempotentUpserts |
            SyncDestinationCapabilities.Deletes |
            SyncDestinationCapabilities.CoLocatedCheckpoint;

        public ValueTask<SyncProvisionResult> ProvisionAsync(
            SyncProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.TransformFingerprint is null)
            {
                state.TransformFingerprint = request.Transform.Fingerprint;
                return ValueTask.FromResult(new SyncProvisionResult(SyncProvisionStatus.Ready));
            }

            return ValueTask.FromResult(
                string.Equals(
                    state.TransformFingerprint,
                    request.Transform.Fingerprint,
                    StringComparison.Ordinal)
                    ? new SyncProvisionResult(SyncProvisionStatus.Ready)
                    : new SyncProvisionResult(
                        SyncProvisionStatus.RebuildRequired,
                        state.TransformFingerprint));
        }

        public ValueTask ResetSnapshotAsync(
            string pipelineId,
            SnapshotReset reset,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Documents.Clear();
            return ValueTask.CompletedTask;
        }

        public ValueTask StartSnapshotAsync(
            string pipelineId,
            SnapshotStart start,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplySnapshotBatchAsync(
            SyncSnapshotBatch batch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var mutation in batch.Mutations)
            {
                state.Documents[$"{mutation.Collection}/{mutation.Key}"] = mutation.Content.ToArray();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteSnapshotAsync(
            string pipelineId,
            SnapshotComplete complete,
            SyncTransformVersion transform,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<SyncApplyResult> ApplyTransactionAsync(
            SyncTransactionBatch batch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = batch.Transaction.CommitEndPosition;
            if (state.Position is { } durable && durable >= position)
            {
                return ValueTask.FromResult(SyncApplyResult.AlreadyApplied(durable));
            }

            foreach (var mutation in batch.Mutations)
            {
                var key = $"{mutation.Collection}/{mutation.Key}";
                if (mutation.Kind is SyncMutationKind.Upsert)
                {
                    state.Documents[key] = mutation.Content.ToArray();
                }
                else
                {
                    state.Documents.Remove(key);
                }
            }

            state.Position = position;
            return ValueTask.FromResult(SyncApplyResult.Applied(
                returnInvalidPosition
                    ? new BlueTuskLogSequenceNumber(position.Value + 1)
                    : position));
        }

        public ValueTask<bool> StoreAsync(
            SyncQuarantineRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = state.QuarantineRecords.Add(
                $"{record.PipelineId}/{record.TransactionId}/{record.CommitEndPosition.Value}");
            return ValueTask.FromResult(true);
        }
    }
}
