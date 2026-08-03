using BlueTusk.Sync.DependencyInjection;
using BlueTusk.TypeSystem;

namespace BlueTusk.ControlPlane;

/// <summary>Contains one coherent observation of hosted Sync pipeline health.</summary>
public sealed record ControlPlaneSyncOverview(
    DateTimeOffset ObservedAt,
    IReadOnlyList<ControlPlaneSyncPipelineSnapshot> Pipelines);

/// <summary>Contains redacted operational state for one hosted Sync pipeline.</summary>
public sealed record ControlPlaneSyncPipelineSnapshot(
    string PipelineId,
    string SourceFingerprint,
    string State,
    DateTimeOffset ChangedAt,
    long AppliedTransactions,
    double? TransactionsPerSecond,
    long AppliedSnapshotBatches,
    long SnapshotRows,
    long QuarantinedTransactions,
    long FailureCount,
    long RetryAttempts,
    TimeSpan ThrottleDelay,
    string LastCommitPosition,
    long? CheckpointLagBytes,
    string? LagDiagnosticCode,
    Guid? SnapshotEpoch,
    bool HandoffCommitted,
    string? DiagnosticCode);

/// <summary>Reads product-specific Sync state without exposing worker internals.</summary>
public interface IControlPlaneSyncQueryService
{
    /// <summary>Gets the latest hosted Sync pipeline overview.</summary>
    ValueTask<ControlPlaneSyncOverview> GetSyncOverviewAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Projects hosted Sync health and relay head positions into control-plane models.</summary>
public sealed class HostedSyncControlPlaneQueryService : IControlPlaneSyncQueryService
{
    private readonly IBlueTuskSyncStatusSource _statuses;
    private readonly IControlPlaneQueryService _sources;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ThroughputSample> _throughput = new(StringComparer.Ordinal);
    private readonly Lock _throughputLock = new();

    /// <summary>Initializes a query service over hosted workers and the relay/source inventory.</summary>
    public HostedSyncControlPlaneQueryService(
        IBlueTuskSyncStatusSource statuses,
        IControlPlaneQueryService sources,
        TimeProvider? timeProvider = null)
    {
        _statuses = statuses ?? throw new ArgumentNullException(nameof(statuses));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<ControlPlaneSyncOverview> GetSyncOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        var observedAt = _timeProvider.GetUtcNow();
        var workers = _statuses.GetStatuses();
        var sourceOverview = await _sources.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        var heads = sourceOverview.Sources
            .GroupBy(static source => source.SourceFingerprint, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().LastCommitPosition,
                StringComparer.Ordinal);
        var rates = CalculateThroughput(workers, observedAt);
        var pipelines = new ControlPlaneSyncPipelineSnapshot[workers.Count];
        for (var index = 0; index < workers.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worker = workers[index];
            var (lag, lagDiagnostic) = CalculateLag(worker, heads);
            pipelines[index] = new ControlPlaneSyncPipelineSnapshot(
                worker.PipelineId,
                worker.SourceFingerprint,
                worker.State.ToString(),
                worker.ChangedAt,
                worker.AppliedTransactions,
                rates[worker.PipelineId],
                worker.AppliedSnapshotBatches,
                worker.SnapshotRows,
                worker.QuarantinedTransactions,
                worker.FailureCount,
                worker.RetryAttempts,
                worker.ThrottleDelay,
                worker.LastCommitPosition.ToString(),
                lag,
                lagDiagnostic,
                worker.SnapshotEpoch,
                worker.HandoffCommitted,
                worker.DiagnosticCode);
        }

        return new ControlPlaneSyncOverview(observedAt, pipelines);
    }

    private Dictionary<string, double?> CalculateThroughput(
        IReadOnlyList<BlueTuskSyncWorkerStatus> workers,
        DateTimeOffset observedAt)
    {
        var rates = new Dictionary<string, double?>(workers.Count, StringComparer.Ordinal);
        lock (_throughputLock)
        {
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var worker in workers)
            {
                active.Add(worker.PipelineId);
                double? rate = null;
                if (_throughput.TryGetValue(worker.PipelineId, out var previous))
                {
                    var elapsed = observedAt - previous.ObservedAt;
                    if (elapsed > TimeSpan.Zero && worker.AppliedTransactions >= previous.Transactions)
                    {
                        rate = (worker.AppliedTransactions - previous.Transactions) /
                               elapsed.TotalSeconds;
                    }
                }

                rates.Add(worker.PipelineId, rate);
                _throughput[worker.PipelineId] = new ThroughputSample(
                    worker.AppliedTransactions,
                    observedAt);
            }

            foreach (var pipelineId in _throughput.Keys.Where(id => !active.Contains(id)).ToArray())
            {
                _throughput.Remove(pipelineId);
            }
        }

        return rates;
    }

    private static (long? Lag, string? Diagnostic) CalculateLag(
        BlueTuskSyncWorkerStatus worker,
        Dictionary<string, string> heads)
    {
        if (!heads.TryGetValue(worker.SourceFingerprint, out var text) ||
            !BlueTuskLogSequenceNumber.TryParse(text, out var head))
        {
            return (null, "source-head-unavailable");
        }

        if (worker.LastCommitPosition > head)
        {
            return (null, "checkpoint-ahead-of-source");
        }

        var difference = head.Value - worker.LastCommitPosition.Value;
        return difference <= long.MaxValue
            ? ((long)difference, null)
            : (long.MaxValue, "lag-overflow");
    }

    private sealed record ThroughputSample(long Transactions, DateTimeOffset ObservedAt);
}
