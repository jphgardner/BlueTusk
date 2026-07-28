namespace BlueTusk.Replication;

/// <summary>The identity and current WAL position reported by a PostgreSQL server.</summary>
public sealed record BlueTuskReplicationSystemIdentity(
    string SystemIdentifier,
    uint Timeline,
    BlueTuskLogSequenceNumber WalPosition,
    string? DatabaseName);

/// <summary>Information returned when PostgreSQL creates a replication slot.</summary>
public sealed record BlueTuskReplicationSlotCreationResult(
    string SlotName,
    BlueTuskLogSequenceNumber ConsistentPoint,
    string? SnapshotName,
    string? OutputPlugin);

/// <summary>Information returned by READ_REPLICATION_SLOT for a physical slot.</summary>
public sealed record BlueTuskPhysicalReplicationSlot(
    string? SlotType,
    BlueTuskLogSequenceNumber? RestartPosition,
    ulong? RestartTimeline);

/// <summary>Options passed to a PostgreSQL logical decoding output plugin.</summary>
public sealed record BlueTuskLogicalReplicationRequest
{
    public required string SlotName { get; init; }

    public BlueTuskLogSequenceNumber StartPosition { get; init; }

    public IReadOnlyDictionary<string, string?> PluginOptions { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}
