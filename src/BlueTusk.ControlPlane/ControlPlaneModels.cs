using System.Data.Common;
using System.Text.RegularExpressions;

namespace BlueTusk.ControlPlane;

public sealed record ControlPlaneOverview(
    DateTimeOffset ObservedAt,
    IReadOnlyList<ControlPlaneSourceSnapshot> Sources);

public sealed record ControlPlaneSourceSnapshot(
    string SourceKey,
    string InstanceName,
    string SourceFingerprint,
    string SystemIdentifier,
    string DatabaseName,
    string SlotName,
    string PublicationFingerprint,
    long SourceEpoch,
    long LastSequence,
    string LastCommitPosition,
    ControlPlaneSlotSnapshot Slot,
    ControlPlaneRelaySnapshot Relay,
    IReadOnlyList<ControlPlaneConsumerGroupSnapshot> ConsumerGroups,
    IReadOnlyList<ControlPlaneSnapshotRunSnapshot> SnapshotRuns,
    IReadOnlyList<ControlPlaneCheckpointSnapshot> Checkpoints);

public sealed record ControlPlaneSlotSnapshot(
    bool SourceReachable,
    bool Exists,
    bool Active,
    string? OutputPlugin,
    string? RestartPosition,
    string? ConfirmedFlushPosition,
    string? WalStatus,
    long WalLagBytes,
    string? DiagnosticCode);

public sealed record ControlPlaneRelaySnapshot(
    long TransactionCount,
    long StorageBytes,
    long FirstSequence,
    long LastSequence,
    long MinimumCheckpointSequence,
    TimeSpan OldestUnacknowledgedAge);

public sealed record ControlPlaneConsumerGroupSnapshot(
    string Name,
    long StartSequence,
    long CheckpointSequence,
    long StoreGeneration,
    bool IsActive,
    bool IsLeased,
    DateTimeOffset? LeaseExpiresAt,
    long LastFencingToken,
    DateTimeOffset? RemovedAt,
    DateTimeOffset? RetentionProtectedUntil);

public sealed record ControlPlaneSnapshotRunSnapshot(
    string SnapshotEpoch,
    string State,
    int ProgressBytes,
    DateTimeOffset UpdatedAt);

public sealed record ControlPlaneCheckpointSnapshot(
    string ConsumerGroup,
    int FormatVersion,
    string SlotName,
    string OutputPlugin,
    string MappingFingerprint,
    string AcknowledgedPosition,
    long StoreGeneration,
    bool IsLeased,
    DateTimeOffset? LeaseExpiresAt,
    long LastFencingToken);

public sealed record ControlPlanePostgreSqlSource
{
    private static readonly Regex SchemaPattern = new(
        "^[A-Za-z_][A-Za-z0-9_$]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public ControlPlanePostgreSqlSource(
        string instanceName,
        DbDataSource sourceDataSource,
        DbDataSource controlDataSource,
        string controlSchema = "bluetusk_streams")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentNullException.ThrowIfNull(sourceDataSource);
        ArgumentNullException.ThrowIfNull(controlDataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlSchema);
        if (!SchemaPattern.IsMatch(controlSchema))
        {
            throw new ArgumentException(
                "The control schema must be one unquoted PostgreSQL identifier.",
                nameof(controlSchema));
        }

        InstanceName = instanceName;
        SourceDataSource = sourceDataSource;
        ControlDataSource = controlDataSource;
        ControlSchema = controlSchema;
    }

    public string InstanceName { get; }

    public DbDataSource SourceDataSource { get; }

    public DbDataSource ControlDataSource { get; }

    public string ControlSchema { get; }

    internal string QuotedControlSchema => $"\"{ControlSchema}\"";
}
