using System.Text.Json.Serialization;
using BlueTusk.Data;

namespace BlueTusk.Workflows.LoadHarness;

internal sealed record LoadPayload(int Tenant, int Sequence, long SentTimestamp, string Padding);
internal sealed record LoadCase(string Name, string Product, int Count, int Tenants, int ConcurrencyPerTenant,
    int PaddingBytes, int RetainedRows, int HandlerDelayMilliseconds, int PoolSize = 64, int DurationSeconds = 0,
    int AdmissionWindow = 256, int HotTenantWeight = 1);
internal sealed record Percentiles(double Minimum, double P50, double P95, double P99, double Maximum);
internal sealed record TenantResult(string Tenant, int Completed, Percentiles DurableLatencyMilliseconds);
internal sealed record DatabaseCounters(ulong WalPosition, long Commits, long Rollbacks, long Inserts,
    long Updates, long Deletes, long Deadlocks, long BlocksRead, long BlocksHit, long TempBytes, long RelationBytes);
internal sealed record RuntimeMetrics(long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections,
    long PeakWorkingSetBytes, long PeakManagedBytes, int PeakPoolTotal, int PeakPoolBusy, int PeakPoolWaiting,
    long PeakDatabaseClientBackends, long PeakDatabaseLockWaiters, long PeakDatabaseWaitingBackends,
    BlueTuskPoolStatistics FinalPool, IReadOnlyList<InstrumentObservation> Instruments);
internal sealed record InstrumentObservation(string Name, long Measurements, double Sum, double Maximum);
internal sealed record CaseResult(LoadCase Configuration, int Produced, int Completed, int DurableEffects, int ActualPayloadBytes,
    double EnqueueSeconds, double DrainSeconds, double CompletedPerDrainSecond,
    Percentiles DurableLatencyMilliseconds, Percentiles HandlerDurationMilliseconds,
    IReadOnlyList<TenantResult> Tenants, RuntimeMetrics Runtime, DatabaseCounters Before, DatabaseCounters After,
    ulong ObservedClusterWalBytes, long RelationGrowthBytes, int PrunedTerminalRows, bool Verified);
internal sealed record FaultResult(string Name, bool Passed, double RecoveryMilliseconds, int Attempts,
    int DurableEffects, bool CommitAcknowledgementDropped, int CanceledAttempts = 0,
    int RejectedConnections = 0, int PeakProxyConnections = 0, double StoreFailureObservations = 0);
internal sealed record HarnessReport(DateTimeOffset StartedAt, string Profile, string OperatingSystem, string Framework,
    string ProcessArchitecture, int LogicalProcessors, string PostgreSqlVersion, string SourceSha256,
    string MeasurementLimitations, IReadOnlyList<CaseResult> Cases, IReadOnlyList<FaultResult> Faults,
    IReadOnlyList<OverloadResult> Overload, string PayloadMode = "Repeated");
internal sealed record OverloadTenantResult(string Tenant, int Accepted, long Rejected,
    Percentiles DurableLatencyMilliseconds, double MaximumCompletionGapMilliseconds);
internal sealed record StorageSample(double ElapsedSeconds, int RetainedPrimaryRows, int ActivePrimaryRows,
    int RetainedJobs, int JobAttemptRows, long RuntimeRelationBytes, PhysicalStorageObservation? Physical = null);
internal sealed record RelationObservation(string Schema, string Table, long TotalBytes, long HeapBytes, long IndexBytes,
    long EstimatedLiveRows, long EstimatedDeadRows, long VacuumCount, long AutovacuumCount, long AnalyzeCount,
    long AutoanalyzeCount, string? LastVacuumUtc, string? LastAutovacuumUtc);
internal sealed record PhysicalStorageObservation(ulong WalPosition, long WalRecords, long WalFullPageImages, long WalBytes,
    long TimedCheckpoints, long RequestedCheckpoints, double CheckpointWriteMilliseconds, double CheckpointSyncMilliseconds,
    double ProcessCpuMilliseconds, long ProcessWorkingSetBytes, long ManagedBytes, long AllocatedBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections, BlueTuskPoolStatistics Pool,
    IReadOnlyList<RelationObservation> Relations);
internal sealed record VacuumObservation(double ElapsedSeconds, string Schema, string Table, double DurationMilliseconds);
internal sealed record OverloadResult(string Product, int OfferedDurationSeconds, int MaximumAccepted,
    int AdmissionSlotsPerTenant, int ConcurrencyPerTenant, int HandlerDelayMilliseconds,
    int Accepted, long Rejected, int DurableEffects, int MaximumOutstanding, int PrunedPrimaryRows, int PrunedJobs,
    double AdmissionAndDrainSeconds, double CompletionsPerSecond, RuntimeMetrics Runtime,
    DatabaseCounters Before, DatabaseCounters After, IReadOnlyList<OverloadTenantResult> Tenants,
    IReadOnlyList<StorageSample> StorageSamples, bool Verified, int AutomaticVacuumObservationSeconds = 0,
    int ManualVacuumIntervalSeconds = 0, IReadOnlyList<VacuumObservation>? Vacuums = null,
    string PayloadMode = "Repeated");

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LoadPayload))]
[JsonSerializable(typeof(HarnessReport))]
[JsonSerializable(typeof(StorageSample))]
internal sealed partial class HarnessJsonContext : JsonSerializerContext;
