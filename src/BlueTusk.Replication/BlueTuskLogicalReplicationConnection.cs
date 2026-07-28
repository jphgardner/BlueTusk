using System.Text;
using BlueTusk.Client;

namespace BlueTusk.Replication;

/// <summary>A PostgreSQL logical streaming replication connection.</summary>
public sealed class BlueTuskLogicalReplicationConnection : BlueTuskReplicationConnection
{
    private BlueTuskLogicalReplicationConnection(BlueTuskSession session)
        : base(session)
    {
    }

    public static ValueTask<BlueTuskLogicalReplicationConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken = default) =>
        OpenAsync(
            BlueTuskClientOptions.FromConnectionString(connectionString),
            cancellationToken);

    public static async ValueTask<BlueTuskLogicalReplicationConnection> OpenAsync(
        BlueTuskClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var session = await BlueTuskSession.OpenAsync(
            options with { ReplicationMode = BlueTuskReplicationMode.Database },
            cancellationToken).ConfigureAwait(false);
        return new BlueTuskLogicalReplicationConnection(session);
    }

    /// <summary>Streams pgoutput changes for one publication.</summary>
    public IAsyncEnumerable<BlueTuskReplicationMessage> StartReplicationAsync(
        string slotName,
        string publicationName,
        CancellationToken cancellationToken = default) =>
        StartReplicationAsync(
            new BlueTuskLogicalReplicationRequest
            {
                SlotName = slotName,
                PluginOptions = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["proto_version"] = "1",
                    ["publication_names"] = publicationName,
                },
            },
            cancellationToken);

    /// <summary>Streams output from a logical decoding plugin.</summary>
    public IAsyncEnumerable<BlueTuskReplicationMessage> StartReplicationAsync(
        BlueTuskLogicalReplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SlotName);
        ArgumentNullException.ThrowIfNull(request.PluginOptions);

        var command = new StringBuilder()
            .Append("START_REPLICATION SLOT ")
            .Append(BlueTuskSql.QuoteIdentifier(request.SlotName))
            .Append(" LOGICAL ")
            .Append(request.StartPosition);
        if (request.PluginOptions.Count > 0)
        {
            command.Append(" (");
            var first = true;
            foreach (var option in request.PluginOptions)
            {
                if (!first)
                {
                    command.Append(", ");
                }

                command.Append(BlueTuskSql.QuoteIdentifier(option.Key));
                if (option.Value is { } value)
                {
                    command.Append(' ').Append(QuoteOptionValue(value));
                }

                first = false;
            }

            command.Append(')');
        }

        return StreamAsync(command.ToString(), cancellationToken);
    }

    /// <summary>Creates a logical replication slot.</summary>
    public async ValueTask<BlueTuskReplicationSlotCreationResult> CreateReplicationSlotAsync(
        string slotName,
        string outputPlugin = "pgoutput",
        bool temporary = false,
        bool twoPhase = false,
        CancellationToken cancellationToken = default)
    {
        var command =
            $"CREATE_REPLICATION_SLOT {BlueTuskSql.QuoteIdentifier(slotName)}";
        if (temporary)
        {
            command += " TEMPORARY";
        }

        command += $" LOGICAL {BlueTuskSql.QuoteIdentifier(outputPlugin)} NOEXPORT_SNAPSHOT";
        if (twoPhase)
        {
            command += " TWO_PHASE";
        }

        var result = await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        var row = GetSingleRow(result, "CREATE_REPLICATION_SLOT");
        return new BlueTuskReplicationSlotCreationResult(
            GetRequiredText(row, 0, "slot_name"),
            BlueTuskLogSequenceNumber.Parse(GetRequiredText(row, 1, "consistent_point")),
            GetOptionalText(row, 2),
            GetOptionalText(row, 3));
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

    private static string QuoteOptionValue(string value)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Logical replication option values cannot contain a null character.",
                nameof(value));
        }

        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
