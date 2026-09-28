using System.Text.Json.Serialization;
using BlueTusk.Live;
using BlueTusk.Projections.Live;

namespace BlueTusk.Projections.LoadHarness;

internal sealed record LoadCase(string Name, int Seconds, int PayloadBytes, int Tenants, int Writers,
    int Backlog, int Fanout, int OfferedPerSecond, int PoolSize, int QueueCapacity, int MaximumOperations, int DrainSeconds)
{
    public int MaximumPendingOperations => Math.Max(4096, Backlog);
}
internal sealed record CampaignReport(DateTimeOffset Started, DateTimeOffset Completed, string Profile, string Runtime,
    string Platform, bool ProductionQualified, bool Passed, ScenarioReport[] Scenarios, string? FailureType);
internal sealed record LatencySummary(long Samples, double P50Milliseconds, double P95Milliseconds,
    double P99Milliseconds, double MaximumMilliseconds);
internal sealed record TenantReport(string Tenant, long Offered, long Rejected, long Committed, long Delivered,
    decimal Aggregate, LatencySummary Commit, LatencySummary Projection, LatencySummary Inbox, LatencySummary LiveCoverage);
internal sealed record RuntimeObservation(long AllocatedBytes, double CpuMilliseconds, long PeakWorkingSetBytes,
    long PeakManagedBytes, int Gen0, int Gen1, int Gen2, int PoolTotal, int PoolBusy, int PoolWaiting,
    long ClientBackends, long LockWaiters, long WaitingBackends, long MaximumRetainedSlotBytes, long MaximumOwnedStorageBytes);
internal sealed record RelationObservation(string Schema, string Relation, long HeapBytes, long IndexBytes, long TotalBytes,
    long LiveTuples, long DeadTuples, long Vacuums, long Autovacuums, long Analyses, long Autoanalyses);
internal sealed record StorageObservation(string ServerVersion, ulong WalPosition, long WalRecords, long FullPageImages,
    long WalBytes, long TimedCheckpoints, long RequestedCheckpoints, double CheckpointWriteMilliseconds,
    double CheckpointSyncMilliseconds, RelationObservation[] Relations);
internal sealed record RecoveryObservation(string Mode, uint BeforeTimeline, uint AfterTimeline, long SourceAcknowledged,
    long PendingBeforePromotion, double PromotionMilliseconds, double RebuildMilliseconds, bool OldOwnerRejected,
    bool TimelineRejected, bool ReconnectReset, bool RemoteApplyVerified, long PrunedRetiredRows, int RetentionCalls);
internal sealed record ServiceWindow(double ElapsedSeconds, long[] Offered, long[] Rejected, long[] Committed,
    long[] Delivered, int Queued, int PoolBusy, int PoolWaiting);
internal sealed record ScenarioReport(LoadCase Configuration, string Scope, string PostgreSql, double SetupSeconds,
    double MeasuredSeconds, double DrainSeconds, double PipelineSeconds, double RuntimeSeconds, double VerificationSeconds,
    long Offered, long Rejected, long Committed, long WalTransactions, long StandbyFeedbackUpdates, long InboxEffects, long LiveFrames,
    long LiveReplayFrames, long LiveFanOutFrames, long DuplicateRetries, long LeaseRecoveries, int PeakQueued, long PeakQueuedPayloadBytes,
    double TransactionsPerSecond, LatencySummary Commit, LatencySummary Projection, LatencySummary Inbox,
    LatencySummary LiveCoverage, TenantReport[] Tenants, RuntimeObservation Runtime, StorageObservation Before,
    StorageObservation After, RecoveryObservation? Recovery, ServiceWindow[] ServiceWindows, bool ExactStateVerified);
internal sealed record LoadEvent(Guid OperationId, string Tenant, string Order, bool CustomerChange, string Padding);

[JsonSerializable(typeof(CampaignReport))]
[JsonSerializable(typeof(LoadEvent))]
[JsonSerializable(typeof(OrderState))]
[JsonSerializable(typeof(CustomerState))]
[JsonSerializable(typeof(OrderView))]
[JsonSerializable(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class ReportJson : JsonSerializerContext;
