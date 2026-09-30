using System.Text.Json.Serialization;
using BlueTusk.Data;

namespace BlueTusk.Documents.LoadHarness;

public sealed record PayloadDocument(string TenantMarker, string Payload, int Count);
internal sealed record AssemblyIdentity(string Name, string Version, string Sha256);
internal sealed record EnvironmentReport(string OperatingSystem, string Runtime, string Architecture, int LogicalProcessors, string Host,
    string SourceFingerprint, string PostgreSqlImage, string PostgreSqlVersion, string SharedBuffers, string MaximumConnections,
    string SynchronousCommit, string FullPageWrites, string TrackIoTiming, AssemblyIdentity[] Assemblies);
internal sealed record CampaignReport(EnvironmentReport Environment, DateTimeOffset FinishedAt, int CellSeconds, int SustainedSeconds,
    string Limits, ScenarioReport[] Scenarios, RecoveryReport Recovery, ResourcePolicyReport? ResourcePolicy = null);
internal sealed record ResourcePolicyReport(string MaintenanceProfile, long MaximumDatabaseBytes,
    long MinimumFilesystemAvailableBytes, int IdleDrainSeconds, int MaximumFilesystemSampleAgeSeconds,
    int MaximumMaintenanceGapSeconds, int MaximumFilesystemReadAttempts, int MaximumFilesystemSamples);
internal sealed record CampaignFailureReport(string Status, string MaintenanceProfile, string Code, long? ObservedBytes,
    long? ConfiguredBytes, int CompletedScenarios, DateTimeOffset FailedUtc, string SourceFingerprint,
    AssemblyIdentity[] Assemblies, MaintenanceObservation? AtFailure, string Stage, string ExceptionType,
    ObserverDiagnostics? Observer, string? InnerExceptionType, string? PriorExceptionType,
    string? BoundedStackTrace, CleanupDiagnostics? Cleanup);
internal sealed record BackendLockObservation(int ProcessId, string BackendType, string State, string WaitEventType,
    string WaitEvent, int BlockingProcessCount);
internal sealed record CleanupDiagnostics(string Schema, bool? SchemaExists, string CaptureStatus,
    long WaitingLocks, long GrantedLocks, BackendLockObservation[] Backends);
internal sealed record Percentiles(long Count, double Minimum, double P50, double P95, double P99, double Maximum);
internal sealed record RelationObservation(string Name, long TotalBytes, long HeapBytes, long IndexBytes, long ToastBytes, long LiveRows, long DeadRows,
    long VacuumCount, long AutoVacuumCount, long AnalyzeCount, long AutoAnalyzeCount);
internal sealed record DatabaseObservation(string WalLsn, long WalRecords, long WalFullPageImages, long WalBytes, long Commits, long Rollbacks,
    long Inserts, long Updates, long Deletes, long Deadlocks, long BlocksRead, long BlocksHit, long TempBytes, RelationObservation[] Relations);
internal sealed record RuntimeMetrics(long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, double CpuMilliseconds,
    long PeakWorkingSetBytes, long PeakManagedBytes, int PeakPoolTotal, int PeakPoolBusy, int PeakPoolWaiters, long PeakDatabaseClients,
    long PeakDatabaseLockWaiters, int Samples, BlueTuskPoolStatistics FinalPool);
internal sealed record StorageSample(double ElapsedSeconds, DatabaseObservation Database);
internal sealed record MaintenanceSample(double ElapsedSeconds, MaintenanceObservation Observation);
internal sealed record ObserverDiagnostics(int DatabaseProbeTimeouts, int MaximumConsecutiveDatabaseProbeTimeouts,
    double MaximumMaintenanceGapSeconds);
internal sealed record MaintenanceEvidence(MaintenanceObservation BeforeWrites, MaintenanceObservation AfterWrites,
    MaintenanceObservation AfterHotKeyAndVerification, MaintenanceObservation? AfterIdleDrain,
    MaintenanceSample[] DuringWrites, MaintenanceSample[] DuringIdleDrain, ObserverDiagnostics Observer);
internal sealed record ScenarioReport(string Name, int PayloadBytes, int Tenants, int Writers, int PoolMaximum, int RowsPerWriterTenant,
    double MeasuredSeconds, long CommittedDocuments, double CommittedDocumentsPerSecond, double PayloadBytesPerSecond,
    long[] TenantProgress, long ReplaceSaves, long PatchSaves, long DeleteReinsertCycles, long HotKeyIncrements, long HotKeyConflicts,
    long RejectedBoundedWrites, Percentiles LoadLatency, Percentiles SaveLatency, Percentiles OperationLatency, Percentiles HotKeyLatency,
    RuntimeMetrics Runtime, DatabaseObservation Before, DatabaseObservation After, StorageSample[] StorageSamples, bool Verified,
    MaintenanceEvidence? Maintenance = null, string PayloadDistribution = "shared-within-scenario",
    string StorageMode = "InlineJsonb");
internal sealed record RecoveryReport(int AcknowledgedBatches, int AcknowledgedDocuments, int Tenants, bool BlockedSecondInsertObserved,
    bool ChildHardKilled, bool NoPartialBatch, bool AllAcknowledgedWritesSurvivedReopen, bool TenantIsolation, bool StaleRevisionRejectedAfterReinsert,
    double KillToVerifiedMilliseconds);

[JsonSerializable(typeof(PayloadDocument))]
[JsonSerializable(typeof(CampaignReport))]
[JsonSerializable(typeof(CampaignFailureReport))]
[JsonSerializable(typeof(ScenarioReport))]
[JsonSerializable(typeof(int))]
internal sealed partial class HarnessJson : JsonSerializerContext;
