using BlueTusk.Sync;
using BlueTusk.Sync.DependencyInjection;
using BlueTusk.TypeSystem;

namespace BlueTusk.ControlPlane.Tests;

public sealed class HostedSyncControlPlaneQueryServiceTests
{
    [Fact]
    public async Task Hosted_sync_projection_reports_rate_lag_and_redacted_failures()
    {
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero));
        var statuses = new MutableStatusSource
        {
            Status = Worker(appliedTransactions: 10),
        };
        var service = new HostedSyncControlPlaneQueryService(
            statuses,
            new FakeSourceQueryService(),
            clock);

        var first = Assert.Single((await service.GetSyncOverviewAsync()).Pipelines);
        Assert.Null(first.TransactionsPerSecond);
        Assert.Equal(128, first.CheckpointLagBytes);
        Assert.Equal("worker-fault", first.DiagnosticCode);
        Assert.DoesNotContain("sensitive", first.ToString(), StringComparison.Ordinal);

        statuses.Status = Worker(appliedTransactions: 20);
        clock.Advance(TimeSpan.FromSeconds(2));
        var second = Assert.Single((await service.GetSyncOverviewAsync()).Pipelines);

        Assert.Equal(5, second.TransactionsPerSecond);
        Assert.Equal(128, second.CheckpointLagBytes);
    }

    private static BlueTuskSyncWorkerStatus Worker(long appliedTransactions) =>
        new(
            PipelineId: "search",
            SourceFingerprint: "source-fingerprint",
            State: SyncPipelineState.Running,
            ChangedAt: new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero),
            AppliedTransactions: appliedTransactions,
            AppliedSnapshotBatches: 1,
            SnapshotRows: 100,
            QuarantinedTransactions: 2,
            FailureCount: 1,
            RetryAttempts: 3,
            ThrottleDelay: TimeSpan.FromSeconds(1),
            LastCommitPosition: new BlueTuskLogSequenceNumber(128),
            SnapshotEpoch: null,
            HandoffCommitted: false,
            DiagnosticCode: "worker-fault");

    private sealed class MutableStatusSource : IBlueTuskSyncStatusSource
    {
        public required BlueTuskSyncWorkerStatus Status { get; set; }

        public IReadOnlyList<BlueTuskSyncWorkerStatus> GetStatuses() => [Status];
    }

    private sealed class FakeSourceQueryService : IControlPlaneQueryService
    {
        public ValueTask<ControlPlaneOverview> GetOverviewAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ControlPlaneOverview(
                    new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero),
                    [new ControlPlaneSourceSnapshot(
                        "source",
                        "instance",
                        "source-fingerprint",
                        "system",
                        "database",
                        "slot",
                        "publication",
                        1,
                        1,
                        "0/100",
                        new ControlPlaneSlotSnapshot(true, true, true, "pgoutput", "0/0", "0/100", "reserved", 0, null),
                        new ControlPlaneRelaySnapshot(1, 1, 1, 1, 1, TimeSpan.Zero),
                        [],
                        [],
                        [])]));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
