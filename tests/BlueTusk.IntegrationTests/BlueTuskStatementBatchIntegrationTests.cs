using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskStatementBatchIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_statement_batches_rebind_parameters_and_keep_order(bool synchronous)
    {
        await using var connection = await OpenAsync();
        await using var command = new BlueTuskCommand(
            "SELECT @right::int4; SELECT @left::int4 + @right::int4; SELECT ';'::text, $$a;b$$::text; -- tail ;", connection);
        command.Parameters.Add(new BlueTuskParameter<int>(11) { ParameterName = "left" });
        command.Parameters.Add(new BlueTuskParameter<int>(7) { ParameterName = "right" });
        command.Parameters.Add(new BlueTuskParameter<string>("ignored") { ParameterName = "unused" });
        for (var iteration = 0; iteration < 8; iteration++)
        {
            command.Parameters["right"].Value = 7 + iteration;
            await using var reader = synchronous ? command.ExecuteReader() : await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(7 + iteration, reader.GetInt32(0));
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal(18 + iteration, reader.GetInt32(0));
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal(";", reader.GetString(0));
            Assert.Equal("a;b", reader.GetString(1));
            Assert.False(await reader.NextResultAsync());
        }
        Assert.Equal(14, synchronous ? command.ExecuteScalar() : await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Positional_statement_batches_preserve_global_ordinals(bool synchronous)
    {
        await using var connection = await OpenAsync();
        await using var command = new BlueTuskCommand("SELECT $2::int4; SELECT $1::text;", connection);
        command.Parameters.Add(new BlueTuskParameter<string>("kept"));
        command.Parameters.Add(new BlueTuskParameter<int>(42));
        await using var reader = synchronous ? command.ExecuteReader() : await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(42, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal("kept", reader.GetString(0));
        Assert.False(await reader.NextResultAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Statement_batch_errors_are_atomic_and_leave_the_connection_reusable(bool synchronous)
    {
        await using var connection = await OpenAsync();
        await using (var create = new BlueTuskCommand("CREATE TEMP TABLE bt_statement_atomic (id int CHECK (id > 0))", connection))
        {
            await create.ExecuteNonQueryAsync();
        }
        await using var command = new BlueTuskCommand(
            "INSERT INTO bt_statement_atomic VALUES (@ok); INSERT INTO bt_statement_atomic VALUES (@bad)", connection);
        command.Parameters.Add(new BlueTuskParameter<int>(1) { ParameterName = "ok" });
        command.Parameters.Add(new BlueTuskParameter<int>(-1) { ParameterName = "bad" });
        var error = await Assert.ThrowsAsync<BlueTuskException>(() => synchronous
            ? Task.FromResult(command.ExecuteNonQuery()) : command.ExecuteNonQueryAsync());
        Assert.Equal("23514", error.SqlState);
        await using var count = new BlueTuskCommand("SELECT count(*)::int8 FROM bt_statement_atomic", connection);
        Assert.Equal(0, await count.ExecuteScalarAsync<long>());
        command.Parameters["bad"].Value = 2;
        Assert.Equal(2, synchronous ? command.ExecuteNonQuery() : await command.ExecuteNonQueryAsync());
        Assert.Equal(2, await count.ExecuteScalarAsync<long>());
    }

    [Fact]
    public async Task Statement_batch_cancellation_rolls_back_and_drains_remaining_responses()
    {
        await using var connection = await OpenAsync();
        await using (var create = new BlueTuskCommand("CREATE TEMP TABLE bt_statement_cancel (id int)", connection))
        {
            await create.ExecuteNonQueryAsync();
        }
        await using var command = new BlueTuskCommand(
            "INSERT INTO bt_statement_cancel VALUES (@value); SELECT pg_sleep(@delay); SELECT @value::int4", connection);
        command.Parameters.Add(new BlueTuskParameter<int>(42) { ParameterName = "value" });
        command.Parameters.Add(new BlueTuskParameter<double>(5) { ParameterName = "delay" });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteNonQueryAsync(cancellation.Token));
        await using var count = new BlueTuskCommand("SELECT count(*)::int8 FROM bt_statement_cancel", connection);
        Assert.Equal(0, await count.ExecuteScalarAsync<long>());
    }

    private static async Task<BlueTuskConnection> OpenAsync()
    {
        var value = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(value)) { throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured."); }
        var connection = new BlueTuskConnection(value);
        try { await connection.OpenAsync(); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
