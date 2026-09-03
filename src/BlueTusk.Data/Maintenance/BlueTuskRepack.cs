using System.Data;
using System.Text;
using BlueTusk.Client;

namespace BlueTusk.Data.Maintenance;

/// <summary>Describes one PostgreSQL 19 <c>REPACK</c> operation.</summary>
public sealed record BlueTuskRepackRequest
{
    /// <summary>The table to repack. Leave null to process eligible relations in the database.</summary>
    public string? TableName { get; init; }

    /// <summary>The table schema. This is valid only when <see cref="TableName"/> is set.</summary>
    public string? SchemaName { get; init; }

    /// <summary>Columns to analyze after repacking. Setting columns requires <see cref="Analyze"/>.</summary>
    public IReadOnlyList<string> AnalyzeColumns { get; init; } = Array.Empty<string>();

    /// <summary>Reorder the table with its configured clustering index.</summary>
    public bool UseIndex { get; init; }

    /// <summary>A specific index to use for physical ordering.</summary>
    public string? IndexName { get; init; }

    /// <summary>Emit PostgreSQL progress information at <c>INFO</c> level.</summary>
    public bool Verbose { get; init; }

    /// <summary>Analyze the table after repacking.</summary>
    public bool Analyze { get; init; }

    /// <summary>Keep a supported table available while PostgreSQL builds its replacement.</summary>
    public bool Concurrently { get; init; }

    /// <summary>
    /// Command timeout in seconds. Zero, the default, disables the client-side timeout for this
    /// potentially long-running maintenance operation.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; }

    /// <summary>Creates a request for one table.</summary>
    public static BlueTuskRepackRequest ForTable(string tableName, string? schemaName = null) =>
        new()
        {
            TableName = tableName,
            SchemaName = schemaName,
        };

    /// <summary>Creates a request for every eligible relation in the current database.</summary>
    public static BlueTuskRepackRequest ForDatabase() => new();
}

/// <summary>One row from PostgreSQL 19's <c>pg_stat_progress_repack</c> view.</summary>
public sealed record BlueTuskRepackProgress(
    int ProcessId,
    long DatabaseOid,
    string? DatabaseName,
    long RelationOid,
    string Command,
    string Phase,
    long RepackIndexRelationOid,
    long HeapTuplesScanned,
    long HeapTuplesInserted,
    long HeapTuplesUpdated,
    long HeapTuplesDeleted,
    long HeapBlocksTotal,
    long HeapBlocksScanned,
    long IndexRebuildCount)
{
    /// <summary>The reported heap-scan completion percentage, when PostgreSQL reports a total.</summary>
    public double? HeapScanPercent => HeapBlocksTotal > 0
        ? Math.Min(100d, 100d * HeapBlocksScanned / HeapBlocksTotal)
        : null;
}

/// <summary>Runs and observes PostgreSQL 19 native <c>REPACK</c> operations.</summary>
public static class BlueTuskRepackExtensions
{
    private const string ProgressSql = """
        SELECT pid,
               datid::int8,
               datname,
               relid::int8,
               command,
               phase,
               repack_index_relid::int8,
               heap_tuples_scanned,
               heap_tuples_inserted,
               heap_tuples_updated,
               heap_tuples_deleted,
               heap_blks_total,
               heap_blks_scanned,
               index_rebuild_count
        FROM pg_catalog.pg_stat_progress_repack
        ORDER BY pid
        """;

    /// <summary>Runs a native PostgreSQL 19 <c>REPACK</c> operation.</summary>
    public static void Repack(
        this BlueTuskConnection connection,
        BlueTuskRepackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureSupported(connection);
        ValidateTransaction(connection, request);

        using var command = CreateRepackCommand(connection, request);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>Runs a native PostgreSQL 19 <c>REPACK</c> operation.</summary>
    public static async Task RepackAsync(
        this BlueTuskConnection connection,
        BlueTuskRepackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureSupported(connection);
        ValidateTransaction(connection, request);

        await using var command = CreateRepackCommand(connection, request);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads currently visible PostgreSQL 19 <c>REPACK</c> operations.</summary>
    public static IReadOnlyList<BlueTuskRepackProgress> GetRepackProgress(
        this BlueTuskConnection connection)
    {
        EnsureSupported(connection);
        using var command = CreateProgressCommand(connection);
        using var reader = command.ExecuteReader();
        var progress = new List<BlueTuskRepackProgress>();
        while (reader.Read())
        {
            progress.Add(ReadProgress(reader));
        }

        return progress;
    }

    /// <summary>Reads currently visible PostgreSQL 19 <c>REPACK</c> operations.</summary>
    public static async Task<IReadOnlyList<BlueTuskRepackProgress>> GetRepackProgressAsync(
        this BlueTuskConnection connection,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported(connection);
        await using var command = CreateProgressCommand(connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var progress = new List<BlueTuskRepackProgress>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            progress.Add(ReadProgress(reader));
        }

        return progress;
    }

    internal static string BuildCommandText(BlueTuskRepackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var sql = new StringBuilder("REPACK");
        var options = new List<string>(3);
        if (request.Verbose)
        {
            options.Add("VERBOSE");
        }

        if (request.Analyze)
        {
            options.Add("ANALYZE");
        }

        if (request.Concurrently)
        {
            options.Add("CONCURRENTLY");
        }

        if (options.Count > 0)
        {
            sql.Append(" (").AppendJoin(", ", options).Append(')');
        }

        if (request.TableName is { } tableName)
        {
            sql.Append(' ');
            if (request.SchemaName is { } schemaName)
            {
                sql.Append(BlueTuskSql.QuoteIdentifier(schemaName)).Append('.');
            }

            sql.Append(BlueTuskSql.QuoteIdentifier(tableName));
            if (request.AnalyzeColumns.Count > 0)
            {
                sql.Append(" (");
                for (var index = 0; index < request.AnalyzeColumns.Count; index++)
                {
                    if (index > 0)
                    {
                        sql.Append(", ");
                    }

                    sql.Append(BlueTuskSql.QuoteIdentifier(request.AnalyzeColumns[index]));
                }

                sql.Append(')');
            }
        }

        if (request.UseIndex || request.IndexName is not null)
        {
            sql.Append(" USING INDEX");
            if (request.IndexName is { } indexName)
            {
                sql.Append(' ').Append(BlueTuskSql.QuoteIdentifier(indexName));
            }
        }

        return sql.ToString();
    }

    private static BlueTuskCommand CreateRepackCommand(
        BlueTuskConnection connection,
        BlueTuskRepackRequest request) =>
        new(BuildCommandText(request), connection)
        {
            CommandTimeout = request.CommandTimeoutSeconds,
            ExecutionMode = BlueTuskCommandExecutionMode.Simple,
            MultiplexingMode = BlueTuskMultiplexingMode.Disable,
            Transaction = connection.CurrentTransaction,
        };

    private static BlueTuskCommand CreateProgressCommand(BlueTuskConnection connection) =>
        new(ProgressSql, connection)
        {
            Transaction = connection.CurrentTransaction,
        };

    private static BlueTuskRepackProgress ReadProgress(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13));

    private static void EnsureSupported(BlueTuskConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open before running REPACK.");
        }

        if (connection.ServerCapabilities is not { SupportsRepack: true })
        {
            throw new NotSupportedException(
                "Native REPACK requires PostgreSQL 19 or later; the connected server does not report that capability.");
        }
    }

    private static void ValidateTransaction(
        BlueTuskConnection connection,
        BlueTuskRepackRequest request)
    {
        if (connection.CurrentTransaction is not null &&
            (request.TableName is null || request.Concurrently))
        {
            throw new InvalidOperationException(
                "Database-wide and concurrent REPACK operations cannot run inside a transaction block.");
        }
    }

    private static void Validate(BlueTuskRepackRequest request)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(request.CommandTimeoutSeconds);
        if (request.AnalyzeColumns is null)
        {
            throw new ArgumentException("AnalyzeColumns cannot be null.", nameof(request));
        }

        if (request.TableName is null)
        {
            if (request.SchemaName is not null || request.IndexName is not null ||
                request.Analyze || request.AnalyzeColumns.Count > 0 || request.Concurrently)
            {
                throw new ArgumentException(
                    "Database-wide REPACK supports VERBOSE and USING INDEX, but not a schema, named index, ANALYZE, columns, or CONCURRENTLY.",
                    nameof(request));
            }

            return;
        }

        _ = BlueTuskSql.QuoteIdentifier(request.TableName);
        if (request.SchemaName is { } schemaName)
        {
            _ = BlueTuskSql.QuoteIdentifier(schemaName);
        }

        if (request.IndexName is { } indexName)
        {
            _ = BlueTuskSql.QuoteIdentifier(indexName);
        }

        if (request.AnalyzeColumns.Count > 0 && !request.Analyze)
        {
            throw new ArgumentException(
                "AnalyzeColumns requires Analyze=true.",
                nameof(request));
        }

        foreach (var column in request.AnalyzeColumns)
        {
            _ = BlueTuskSql.QuoteIdentifier(column);
        }
    }
}
