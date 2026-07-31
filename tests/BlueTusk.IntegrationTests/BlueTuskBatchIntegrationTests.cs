using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskBatchIntegrationTests
{
    [Fact]
    public async Task Batch_executes_named_parameters_multiple_results_and_affected_rows()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using (var setup = new BlueTuskCommand(
            "CREATE TEMP TABLE bluetusk_batch_values (value int4 NOT NULL)",
            connection))
        {
            _ = await setup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var batch = connection.CreateBatch();
        var insert = batch.BatchCommands.Add(
            "INSERT INTO bluetusk_batch_values (value) VALUES (@value)");
        insert.Parameters.Add(new BlueTuskParameter<int>(40) { ParameterName = "value" });
        var update = batch.BatchCommands.Add(
            "UPDATE bluetusk_batch_values SET value = value + $1");
        update.Parameters.Add(new BlueTuskParameter<int>(2));
        var select = batch.BatchCommands.Add(
            "SELECT value FROM bluetusk_batch_values ORDER BY value");

        await using (var reader = await batch.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.False(await reader.ReadAsync(CancellationToken.None));
            Assert.True(await reader.NextResultAsync(CancellationToken.None));
            Assert.False(await reader.ReadAsync(CancellationToken.None));
            Assert.True(await reader.NextResultAsync(CancellationToken.None));
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(42, reader.GetInt32(0));
            Assert.False(await reader.NextResultAsync(CancellationToken.None));
        }

        Assert.Equal(1, insert.RecordsAffected);
        Assert.Equal(1, update.RecordsAffected);
        Assert.Equal(-1, select.RecordsAffected);

        await using var nonQuery = connection.CreateBatch();
        nonQuery.BatchCommands.Add(
            "INSERT INTO bluetusk_batch_values (value) VALUES (7)");
        nonQuery.BatchCommands.Add(
            "DELETE FROM bluetusk_batch_values WHERE value = 7");
        Assert.Equal(2, await nonQuery.ExecuteNonQueryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Prepared_batch_reuses_and_rebuilds_its_server_statements()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = connection.CreateBatch();
        var first = batch.BatchCommands.Add("SELECT @value::int4");
        first.Parameters.Add(new BlueTuskParameter<int>(42) { ParameterName = "value" });
        var second = batch.BatchCommands.Add("SELECT $1::text");
        second.Parameters.Add(new BlueTuskParameter<string>("prepared"));

        await batch.PrepareAsync(CancellationToken.None);
        await AssertBatchValuesAsync(batch, 42, "prepared");
        Assert.Equal(2L, await CountPreparedBatchStatementsAsync(connection));

        first.CommandText = "SELECT @value::int4 + 1";
        ((BlueTuskParameter)first.Parameters[0]).Value = 41;
        await AssertBatchValuesAsync(batch, 42, "prepared");
        Assert.Equal(2L, await CountPreparedBatchStatementsAsync(connection));
    }

    [Fact]
    public async Task Batch_honours_transactions_timeouts_cancellation_and_data_source_ownership()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using (var setup = new BlueTuskCommand(
            "CREATE TEMP TABLE bluetusk_batch_transactions (value int4 NOT NULL)",
            connection))
        {
            _ = await setup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using (var transaction = await connection.BeginTransactionAsync(CancellationToken.None))
        {
            await using var transactional = connection.CreateBatch();
            transactional.Transaction = transaction;
            transactional.BatchCommands.Add(
                "INSERT INTO bluetusk_batch_transactions (value) VALUES (42)");
            Assert.Equal(1, await transactional.ExecuteNonQueryAsync(CancellationToken.None));
            await transaction.RollbackAsync(CancellationToken.None);
        }

        await using (var count = new BlueTuskCommand(
            "SELECT count(*) FROM bluetusk_batch_transactions",
            connection))
        {
            Assert.Equal(0L, await count.ExecuteScalarAsync<long>(CancellationToken.None));
        }

        await using (var timed = connection.CreateBatch())
        {
            timed.Timeout = 1;
            timed.BatchCommands.Add("SELECT pg_sleep(10)");
            _ = await Assert.ThrowsAsync<TimeoutException>(
                () => timed.ExecuteNonQueryAsync(CancellationToken.None));
        }

        await using (var cancelled = connection.CreateBatch())
        {
            cancelled.Timeout = 0;
            cancelled.BatchCommands.Add("SELECT pg_sleep(10)");
            var execution = cancelled.ExecuteNonQueryAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            await cancelled.CancelAsync(CancellationToken.None);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }

        await using (var valid = new BlueTuskCommand("SELECT 42::int4", connection))
        {
            Assert.Equal(42, await valid.ExecuteScalarAsync<int>(CancellationToken.None));
        }

        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var owned = dataSource.CreateBatch();
        owned.BatchCommands.Add("SELECT 42::int4");
        Assert.Equal(42, await owned.ExecuteScalarAsync(CancellationToken.None));
        Assert.Equal(0, dataSource.GetPoolStatistics().Busy);
    }

    private static async Task AssertBatchValuesAsync(
        BlueTuskBatch batch,
        int expectedNumber,
        string expectedText)
    {
        await using var reader = await batch.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(expectedNumber, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync(CancellationToken.None));
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(expectedText, reader.GetString(0));
        Assert.False(await reader.NextResultAsync(CancellationToken.None));
    }

    private static async Task<long> CountPreparedBatchStatementsAsync(
        BlueTuskConnection connection)
    {
        await using var count = new BlueTuskCommand(
            "SELECT count(*) FROM pg_prepared_statements WHERE name LIKE 'bluetusk_batch_%'",
            connection);
        return await count.ExecuteScalarAsync<long>(CancellationToken.None);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
