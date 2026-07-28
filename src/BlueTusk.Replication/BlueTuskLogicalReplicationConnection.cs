using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Client;

namespace BlueTusk.Replication;

/// <summary>A PostgreSQL logical streaming replication connection.</summary>
public sealed class BlueTuskLogicalReplicationConnection : BlueTuskReplicationConnection
{
    private BlueTuskLogicalReplicationConnection(
        BlueTuskSession session,
        BlueTuskClientOptions catalogOptions)
        : base(session, catalogOptions)
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
        return new BlueTuskLogicalReplicationConnection(session, options);
    }

    /// <summary>Lists logical replication publications in the connected database.</summary>
    public async ValueTask<IReadOnlyList<BlueTuskPublicationInfo>> GetPublicationsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCatalogQueryAsync(
            """
            SELECT
                oid::text,
                pubname,
                pubowner::regrole::text,
                puballtables::text,
                pubinsert::text,
                pubupdate::text,
                pubdelete::text,
                pubtruncate::text,
                pubviaroot::text
            FROM pg_catalog.pg_publication
            ORDER BY pubname
            """,
            cancellationToken).ConfigureAwait(false);
        var resultSet = GetSingleResultSet(result, "publication discovery");
        var publications = new BlueTuskPublicationInfo[resultSet.Rows.Count];
        for (var index = 0; index < publications.Length; index++)
        {
            var row = resultSet.Rows[index];
            publications[index] = new BlueTuskPublicationInfo(
                ParseUInt32(GetRequiredText(row, 0, "oid"), "oid"),
                GetRequiredText(row, 1, "pubname"),
                GetRequiredText(row, 2, "pubowner"),
                ParseBoolean(GetRequiredText(row, 3, "puballtables"), "puballtables"),
                ParseBoolean(GetRequiredText(row, 4, "pubinsert"), "pubinsert"),
                ParseBoolean(GetRequiredText(row, 5, "pubupdate"), "pubupdate"),
                ParseBoolean(GetRequiredText(row, 6, "pubdelete"), "pubdelete"),
                ParseBoolean(GetRequiredText(row, 7, "pubtruncate"), "pubtruncate"),
                ParseBoolean(GetRequiredText(row, 8, "pubviaroot"), "pubviaroot"));
        }

        return publications;
    }

    /// <summary>Lists tables and filters exposed by one logical publication.</summary>
    public async ValueTask<IReadOnlyList<BlueTuskPublicationTableInfo>>
        GetPublicationTablesAsync(
            string publicationName,
            CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCatalogQueryAsync(
            $"""
             SELECT
                 pubname,
                 schemaname,
                 tablename,
                 CASE
                     WHEN attnames IS NULL THEN NULL
                     ELSE array_to_json(attnames)::text
                 END,
                 rowfilter
             FROM pg_catalog.pg_publication_tables
             WHERE pubname = {BlueTuskSql.QuoteLiteral(publicationName)}
             ORDER BY schemaname, tablename
             """,
            cancellationToken).ConfigureAwait(false);
        var resultSet = GetSingleResultSet(result, "publication table discovery");
        var tables = new BlueTuskPublicationTableInfo[resultSet.Rows.Count];
        for (var index = 0; index < tables.Length; index++)
        {
            var row = resultSet.Rows[index];
            var columnsJson = GetOptionalText(row, 3);
            tables[index] = new BlueTuskPublicationTableInfo(
                GetRequiredText(row, 0, "pubname"),
                GetRequiredText(row, 1, "schemaname"),
                GetRequiredText(row, 2, "tablename"),
                columnsJson is null
                    ? null
                    : JsonSerializer.Deserialize<string[]>(columnsJson) ??
                        throw new BlueTuskReplicationProtocolException(
                            "Publication columns were not a JSON array."),
                GetOptionalText(row, 4));
        }

        return tables;
    }

    /// <summary>Streams pgoutput changes for one publication.</summary>
    public IAsyncEnumerable<BlueTuskReplicationMessage> StartReplicationAsync(
        string slotName,
        string publicationName,
        CancellationToken cancellationToken = default) =>
        StartReplicationAsync(
            new BlueTuskPgOutputReplicationOptions
            {
                SlotName = slotName,
                PublicationNames = [publicationName],
            },
            cancellationToken);

    /// <summary>Streams changes using typed pgoutput plugin options.</summary>
    public IAsyncEnumerable<BlueTuskReplicationMessage> StartReplicationAsync(
        BlueTuskPgOutputReplicationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var pluginOptions = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["proto_version"] = options.ProtocolVersion.ToString(
                CultureInfo.InvariantCulture),
            ["publication_names"] = string.Join(
                ",",
                options.PublicationNames.Select(BlueTuskSql.QuoteIdentifier)),
        };
        if (options.Binary)
        {
            pluginOptions["binary"] = "true";
        }

        if (options.Messages)
        {
            pluginOptions["messages"] = "true";
        }

        if (options.StreamingMode != BlueTuskLogicalStreamingMode.Off)
        {
            pluginOptions["streaming"] = options.StreamingMode switch
            {
                BlueTuskLogicalStreamingMode.On => "on",
                BlueTuskLogicalStreamingMode.Parallel => "parallel",
                _ => throw new UnreachableException(),
            };
        }

        if (options.TwoPhase)
        {
            pluginOptions["two_phase"] = "true";
        }

        if (options.OriginMode == BlueTuskLogicalOriginMode.None)
        {
            pluginOptions["origin"] = "none";
        }

        return StartReplicationAsync(
            new BlueTuskLogicalReplicationRequest
            {
                SlotName = options.SlotName,
                StartPosition = options.StartPosition,
                PluginOptions = pluginOptions,
            },
            cancellationToken);
    }

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
