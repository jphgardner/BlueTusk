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
        if (!dropAfter.IsFinite)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dropAfter),
                "A TimescaleDB retention interval must be finite.");
        }

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

    private static BlueTuskTimescaleDbFeature GetFeature(BlueTuskDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return dataSource.Features.GetRequired<BlueTuskTimescaleDbFeature>(
            BlueTuskTimescaleDbFeature.RegistryName);
    }

    private static string DelimitIdentifier(string identifier) =>
        new StringBuilder(identifier.Length + 2)
            .Append('"')
            .Append(identifier.Replace("\"", "\"\"", StringComparison.Ordinal))
            .Append('"')
            .ToString();
}
