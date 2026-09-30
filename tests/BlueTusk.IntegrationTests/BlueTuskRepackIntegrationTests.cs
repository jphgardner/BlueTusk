using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Data.Maintenance;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskRepackIntegrationTests
{
    [Fact]
    public async Task PrePostgreSql19_repack_is_rejected_before_sql_is_sent()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        if (connection.ServerCapabilities is { ServerVersion.Major: >= 19 })
        {
            throw SkipException.ForSkip("This guard test requires PostgreSQL 18 or earlier.");
        }

        Assert.False(connection.SupportsRepack);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            connection.RepackAsync(BlueTuskRepackRequest.ForDatabase()));
    }

    [Fact]
    public async Task PostgreSql19_repack_rejects_database_and_concurrent_forms_in_a_transaction()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        if (connection.SupportsRepack is not true)
        {
            throw SkipException.ForSkip(
                $"Native REPACK requires PostgreSQL 19; connected to {connection.ServerVersion}.");
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.RepackAsync(BlueTuskRepackRequest.ForDatabase()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.RepackAsync(
                BlueTuskRepackRequest.ForTable("any_table") with
                {
                    Concurrently = true,
                }));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task PostgreSql19_repack_executes_analyzes_and_exposes_progress_view()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        if (connection.SupportsRepack is not true)
        {
            throw SkipException.ForSkip(
                $"Native REPACK requires PostgreSQL 19; connected to {connection.ServerVersion}.");
        }

        const string table = "bluetusk_repack_acceptance";
        const string index = "bluetusk_repack_acceptance_value_idx";
        await Execute(connection, $"DROP TABLE IF EXISTS {table}");
        try
        {
            await Execute(
                connection,
                $"""
                CREATE TABLE {table} (
                    id bigint PRIMARY KEY,
                    value text NOT NULL
                );
                INSERT INTO {table}
                SELECT value, repeat('x', 128)
                FROM generate_series(1, 1000) AS value;
                DELETE FROM {table} WHERE id % 2 = 0;
                CREATE INDEX {index} ON {table} (value);
                """);

            await connection.RepackAsync(
                BlueTuskRepackRequest.ForTable(table, "public") with
                {
                    Analyze = true,
                    AnalyzeColumns = ["value"],
                });
            await connection.RepackAsync(
                BlueTuskRepackRequest.ForTable(table) with
                {
                    IndexName = index,
                });
            await connection.RepackAsync(
                BlueTuskRepackRequest.ForTable(table) with
                {
                    Concurrently = true,
                    UseIndex = true,
                });

            await using var count = new BlueTuskCommand(
                $"SELECT count(*)::int8 FROM {table}",
                connection);
            Assert.Equal(500L, await count.ExecuteScalarAsync<long>());
            Assert.Empty(await connection.GetRepackProgressAsync());
        }
        finally
        {
            await Execute(connection, $"DROP TABLE IF EXISTS {table}");
        }
    }

    private static async Task Execute(BlueTuskConnection connection, string sql)
    {
        await using var command = new BlueTuskCommand(sql, connection)
        {
            ExecutionMode = BlueTuskCommandExecutionMode.Simple,
        };
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
