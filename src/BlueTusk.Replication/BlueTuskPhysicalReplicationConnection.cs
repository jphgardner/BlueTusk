using BlueTusk.Client;

namespace BlueTusk.Replication;

/// <summary>A PostgreSQL physical streaming replication connection.</summary>
public sealed class BlueTuskPhysicalReplicationConnection : BlueTuskReplicationConnection
{
    private BlueTuskPhysicalReplicationConnection(BlueTuskSession session)
        : base(session)
    {
    }

    public static ValueTask<BlueTuskPhysicalReplicationConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken = default) =>
        OpenAsync(
            BlueTuskClientOptions.FromConnectionString(connectionString),
            cancellationToken);

    public static async ValueTask<BlueTuskPhysicalReplicationConnection> OpenAsync(
        BlueTuskClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var session = await BlueTuskSession.OpenAsync(
            options with { ReplicationMode = BlueTuskReplicationMode.Physical },
            cancellationToken).ConfigureAwait(false);
        return new BlueTuskPhysicalReplicationConnection(session);
    }

    /// <summary>Streams physical WAL from the requested position.</summary>
    public IAsyncEnumerable<BlueTuskReplicationMessage> StartReplicationAsync(
        BlueTuskLogSequenceNumber startPosition,
        string? slotName = null,
        uint? timeline = null,
        CancellationToken cancellationToken = default)
    {
        var command = "START_REPLICATION ";
        if (!string.IsNullOrWhiteSpace(slotName))
        {
            command += $"SLOT {BlueTuskSql.QuoteIdentifier(slotName)} ";
        }

        command += $"PHYSICAL {startPosition}";
        if (timeline is { } timelineValue)
        {
            ArgumentOutOfRangeException.ThrowIfZero(timelineValue);
            command += $" TIMELINE {timelineValue}";
        }

        return StreamAsync(command, cancellationToken);
    }

    /// <summary>Creates a physical replication slot.</summary>
    public async ValueTask<BlueTuskReplicationSlotCreationResult> CreateReplicationSlotAsync(
        string slotName,
        bool temporary = false,
        bool reserveWal = false,
        CancellationToken cancellationToken = default)
    {
        var command =
            $"CREATE_REPLICATION_SLOT {BlueTuskSql.QuoteIdentifier(slotName)}";
        if (temporary)
        {
            command += " TEMPORARY";
        }

        command += " PHYSICAL";
        if (reserveWal)
        {
            command += " RESERVE_WAL";
        }

        var result = await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return ParseSlotCreationResult(result);
    }

    /// <summary>Reads restart information for a physical replication slot.</summary>
    public async ValueTask<BlueTuskPhysicalReplicationSlot> ReadReplicationSlotAsync(
        string slotName,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCommandAsync(
            $"READ_REPLICATION_SLOT {BlueTuskSql.QuoteIdentifier(slotName)}",
            cancellationToken).ConfigureAwait(false);
        var row = GetSingleRow(result, "READ_REPLICATION_SLOT");
        var restartPosition = GetOptionalText(row, 1);
        var restartTimeline = GetOptionalText(row, 2);
        return new BlueTuskPhysicalReplicationSlot(
            GetOptionalText(row, 0),
            restartPosition is null
                ? null
                : BlueTuskLogSequenceNumber.Parse(restartPosition),
            restartTimeline is null
                ? null
                : ulong.Parse(restartTimeline, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Drops a replication slot.</summary>
    public async ValueTask DropReplicationSlotAsync(
        string slotName,
        bool wait = false,
        CancellationToken cancellationToken = default)
    {
        var command = $"DROP_REPLICATION_SLOT {BlueTuskSql.QuoteIdentifier(slotName)}";
        if (wait)
        {
            command += " WAIT";
        }

        _ = await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static BlueTuskReplicationSlotCreationResult ParseSlotCreationResult(
        BlueTuskQueryResult result)
    {
        var row = GetSingleRow(result, "CREATE_REPLICATION_SLOT");
        return new BlueTuskReplicationSlotCreationResult(
            GetRequiredText(row, 0, "slot_name"),
            BlueTuskLogSequenceNumber.Parse(GetRequiredText(row, 1, "consistent_point")),
            GetOptionalText(row, 2),
            GetOptionalText(row, 3));
    }
}
