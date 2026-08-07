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

/// <summary>Controls snapshot handling while a logical replication slot is created.</summary>
public enum BlueTuskLogicalSlotSnapshotMode
{
    NoExport,
    Export,
    Use,
}

/// <summary>Options for creating a logical replication slot.</summary>
public sealed record BlueTuskLogicalReplicationSlotCreationOptions
{
    public required string SlotName { get; init; }

    public string OutputPlugin { get; init; } = "pgoutput";

    public bool Temporary { get; init; }

    public bool TwoPhase { get; init; }

    public BlueTuskLogicalSlotSnapshotMode SnapshotMode { get; init; } =
        BlueTuskLogicalSlotSnapshotMode.NoExport;
}

/// <summary>Information returned by READ_REPLICATION_SLOT for a physical slot.</summary>
public sealed record BlueTuskPhysicalReplicationSlot(
    string? SlotType,
    BlueTuskLogSequenceNumber? RestartPosition,
    ulong? RestartTimeline);

/// <summary>A replication slot discovered through the PostgreSQL catalog.</summary>
public sealed record BlueTuskReplicationSlotInfo(
    string SlotName,
    string? OutputPlugin,
    string SlotType,
    string? DatabaseName,
    bool IsTemporary,
    bool IsActive,
    int? ActiveProcessId,
    BlueTuskLogSequenceNumber? RestartPosition,
    BlueTuskLogSequenceNumber? ConfirmedFlushPosition,
    string? WalStatus);

/// <summary>
/// Identifies a durable logical-replication position and the server resources
/// to which it belongs.
/// </summary>
public sealed record BlueTuskLogicalReplicationCheckpoint(
    string SystemIdentifier,
    string DatabaseName,
    string SlotName,
    string OutputPlugin,
    BlueTuskLogSequenceNumber AppliedPosition);

/// <summary>A PostgreSQL logical replication publication.</summary>
public sealed record BlueTuskPublicationInfo(
    uint Oid,
    string Name,
    string Owner,
    bool PublishesAllTables,
    bool PublishesInserts,
    bool PublishesUpdates,
    bool PublishesDeletes,
    bool PublishesTruncates,
    bool PublishesViaPartitionRoot);

/// <summary>A table exposed by a PostgreSQL logical replication publication.</summary>
public sealed record BlueTuskPublicationTableInfo(
    string PublicationName,
    string SchemaName,
    string TableName,
    IReadOnlyList<string>? Columns,
    string? RowFilter);

/// <summary>Options passed to a PostgreSQL logical decoding output plugin.</summary>
public sealed record BlueTuskLogicalReplicationRequest
{
    public required string SlotName { get; init; }

    public BlueTuskLogSequenceNumber StartPosition { get; init; }

    public IReadOnlyDictionary<string, string?> PluginOptions { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}

public enum BlueTuskLogicalStreamingMode
{
    Off,
    On,
    Parallel,
}

public enum BlueTuskLogicalOriginMode
{
    Any,
    None,
}

/// <summary>Typed startup options for PostgreSQL's pgoutput plugin.</summary>
public sealed record BlueTuskPgOutputReplicationOptions
{
    public required string SlotName { get; init; }

    public required IReadOnlyList<string> PublicationNames { get; init; }

    public BlueTuskLogSequenceNumber StartPosition { get; init; }

    public int ProtocolVersion { get; init; } = 1;

    public bool Binary { get; init; }

    public bool Messages { get; init; }

    public BlueTuskLogicalStreamingMode StreamingMode { get; init; }

    public bool TwoPhase { get; init; }

    public BlueTuskLogicalOriginMode OriginMode { get; init; } =
        BlueTuskLogicalOriginMode.Any;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SlotName);
        ArgumentNullException.ThrowIfNull(PublicationNames);
        if (PublicationNames.Count == 0)
        {
            throw new ArgumentException(
                "At least one publication name is required.",
                nameof(PublicationNames));
        }

        foreach (var publicationName in PublicationNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(publicationName);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ProtocolVersion, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ProtocolVersion, 4);
        if (!Enum.IsDefined(StreamingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(StreamingMode));
        }

        if (!Enum.IsDefined(OriginMode))
        {
            throw new ArgumentOutOfRangeException(nameof(OriginMode));
        }

        if (StreamingMode != BlueTuskLogicalStreamingMode.Off && ProtocolVersion < 2)
        {
            throw new ArgumentException(
                "Transaction streaming requires pgoutput protocol version 2 or later.");
        }

        if (StreamingMode == BlueTuskLogicalStreamingMode.Parallel && ProtocolVersion < 4)
        {
            throw new ArgumentException(
                "Parallel transaction streaming requires pgoutput protocol version 4.");
        }

        if (TwoPhase && ProtocolVersion < 3)
        {
            throw new ArgumentException(
                "Two-phase decoding requires pgoutput protocol version 3 or later.");
        }
    }
}
