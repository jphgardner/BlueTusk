using System.Text;
using BlueTusk.Data;
using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.TimescaleDB;

/// <summary>The result of converting a PostgreSQL table to a TimescaleDB hypertable.</summary>
public sealed record BlueTuskHypertableResult(int HypertableId, bool Created);

public static class BlueTuskTimescaleDbDataSourceExtensions
{
    /// <summary>Returns the installed TimescaleDB extension version.</summary>
    public static async ValueTask<string> GetTimescaleDbVersionAsync(
        this BlueTuskDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        _ = GetFeature(dataSource);
        await using var command = dataSource.CreateCommand(
            "SELECT extversion FROM pg_catalog.pg_extension WHERE extname = 'timescaledb'");
        var version = await command.ExecuteScalarAsync<string>(cancellationToken).ConfigureAwait(false);
        return version ?? throw new InvalidOperationException("The TimescaleDB extension is not installed.");
    }

    /// <summary>Converts an existing PostgreSQL table into a range-partitioned hypertable.</summary>
    public static async ValueTask<BlueTuskHypertableResult> CreateHypertableAsync(
        this BlueTuskDataSource dataSource,
        string relation,
        string timeColumn,
        bool ifNotExists = true,
        bool migrateData = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeColumn);
        var schema = DelimitIdentifier(GetFeature(dataSource).Schema);
        var sql = $"""
            SELECT hypertable_id, created
            FROM {schema}."create_hypertable"(
                $1::regclass,
                {schema}."by_range"($2::name),
                if_not_exists => $3,
                migrate_data => $4)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new BlueTuskParameter<string>(relation));
        command.Parameters.Add(new BlueTuskParameter<string>(timeColumn));
        command.Parameters.Add(new BlueTuskParameter<bool>(ifNotExists));
        command.Parameters.Add(new BlueTuskParameter<bool>(migrateData));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("TimescaleDB create_hypertable returned no row.");
        }

        return new BlueTuskHypertableResult(reader.GetInt32(0), reader.GetBoolean(1));
    }

    /// <summary>Adds an interval-based data retention policy and returns its background job ID.</summary>
    public static async ValueTask<int> AddRetentionPolicyAsync(
        this BlueTuskDataSource dataSource,
        string relation,
        BlueTuskInterval dropAfter,
        bool ifNotExists = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        ValidateFiniteInterval(dropAfter, nameof(dropAfter));

        var schema = DelimitIdentifier(GetFeature(dataSource).Schema);
        var sql = $"""
            SELECT {schema}."add_retention_policy"(
                $1::regclass,
                drop_after => $2::interval,
                if_not_exists => $3)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new BlueTuskParameter<string>(relation));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskInterval>(dropAfter));
        command.Parameters.Add(new BlueTuskParameter<bool>(ifNotExists));
        return await command.ExecuteScalarAsync<int>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a hypertable's data retention policy.</summary>
    public static async ValueTask RemoveRetentionPolicyAsync(
        this BlueTuskDataSource dataSource,
        string relation,
        bool ifExists = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        var schema = DelimitIdentifier(GetFeature(dataSource).Schema);
        var sql = $"""
            SELECT {schema}."remove_retention_policy"(
                $1::regclass,
                if_exists => $2)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new BlueTuskParameter<string>(relation));
        command.Parameters.Add(new BlueTuskParameter<bool>(ifExists));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns TimescaleDB's approximate row count for a table or continuous aggregate.</summary>
    public static async ValueTask<long> GetApproximateRowCountAsync(
        this BlueTuskDataSource dataSource,
        string relation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        var schema = DelimitIdentifier(GetFeature(dataSource).Schema);
        await using var command = dataSource.CreateCommand(
            $"SELECT {schema}.\"approximate_row_count\"($1::regclass)");
        command.Parameters.Add(new BlueTuskParameter<string>(relation));
        return await command.ExecuteScalarAsync<long>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds the current Hypercore columnstore policy to a hypertable.</summary>
    public static async ValueTask AddColumnstorePolicyAsync(
        this BlueTuskDataSource dataSource,
        string relation,
        BlueTuskInterval after,
        bool ifNotExists = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        ValidateFiniteInterval(after, nameof(after));
        var schema = DelimitIdentifier(GetFeature(dataSource).Schema);
        var sql = $"""
            CALL {schema}."add_columnstore_policy"(
                $1::regclass,
                after => $2::interval,
                if_not_exists => $3)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new BlueTuskParameter<string>(relation));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskInterval>(after));
        command.Parameters.Add(new BlueTuskParameter<bool>(ifNotExists));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a hypertable's Hypercore columnstore policy.</summary>
    public static async ValueTask RemoveColumnstorePolicyAsync(
        this BlueTuskDataSource dataSource,
        string relation,
        bool ifExists = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        var schema = DelimitIdentifier(GetFeature(dataSource).Schema);
        await using var command = dataSource.CreateCommand(
            $"CALL {schema}.\"remove_columnstore_policy\"($1::regclass, if_exists => $2)");
        command.Parameters.Add(new BlueTuskParameter<string>(relation));
        command.Parameters.Add(new BlueTuskParameter<bool>(ifExists));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds a refresh policy to a continuous aggregate and returns its background job ID.</summary>
    public static async ValueTask<int> AddContinuousAggregatePolicyAsync(
        this BlueTuskDataSource dataSource,
        string relation,
        BlueTuskInterval startOffset,
        BlueTuskInterval endOffset,
        BlueTuskInterval scheduleInterval,
        bool ifNotExists = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        ValidateFiniteInterval(startOffset, nameof(startOffset));
        ValidateFiniteInterval(endOffset, nameof(endOffset));
        ValidateFiniteInterval(scheduleInterval, nameof(scheduleInterval));
        var schema = DelimitIdentifier(GetFeature(dataSource).Schema);
        var sql = $"""
            SELECT {schema}."add_continuous_aggregate_policy"(
                $1::regclass,
                start_offset => $2::interval,
                end_offset => $3::interval,
                schedule_interval => $4::interval,
                if_not_exists => $5)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new BlueTuskParameter<string>(relation));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskInterval>(startOffset));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskInterval>(endOffset));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskInterval>(scheduleInterval));
        command.Parameters.Add(new BlueTuskParameter<bool>(ifNotExists));
        return await command.ExecuteScalarAsync<int>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a continuous aggregate refresh policy.</summary>
    public static async ValueTask RemoveContinuousAggregatePolicyAsync(
        this BlueTuskDataSource dataSource,
        string relation,
        bool ifExists = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        var schema = DelimitIdentifier(GetFeature(dataSource).Schema);
        await using var command = dataSource.CreateCommand(
            $"SELECT {schema}.\"remove_continuous_aggregate_policy\"($1::regclass, if_exists => $2)");
        command.Parameters.Add(new BlueTuskParameter<string>(relation));
        command.Parameters.Add(new BlueTuskParameter<bool>(ifExists));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refreshes a bounded timestamp-with-time-zone window of a continuous aggregate.</summary>
    public static async ValueTask RefreshContinuousAggregateAsync(
        this BlueTuskDataSource dataSource,
        string relation,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        if (windowStart >= windowEnd)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowEnd),
                "A continuous aggregate refresh window must end after it starts.");
        }

        var schema = DelimitIdentifier(GetFeature(dataSource).Schema);
        var sql = $"""
            CALL {schema}."refresh_continuous_aggregate"(
                $1::regclass,
                $2::timestamptz,
                $3::timestamptz,
                force => $4)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new BlueTuskParameter<string>(relation));
        command.Parameters.Add(new BlueTuskParameter<DateTimeOffset>(windowStart));
        command.Parameters.Add(new BlueTuskParameter<DateTimeOffset>(windowEnd));
        command.Parameters.Add(new BlueTuskParameter<bool>(force));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static BlueTuskTimescaleDbFeature GetFeature(BlueTuskDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return dataSource.Features.GetRequired<BlueTuskTimescaleDbFeature>(
            BlueTuskTimescaleDbFeature.RegistryName);
    }

    private static void ValidateFiniteInterval(BlueTuskInterval interval, string parameterName)
    {
        if (!interval.IsFinite)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A TimescaleDB interval must be finite.");
        }
    }

    private static string DelimitIdentifier(string identifier) =>
        new StringBuilder(identifier.Length + 2)
            .Append('"')
            .Append(identifier.Replace("\"", "\"\"", StringComparison.Ordinal))
            .Append('"')
            .ToString();
}
