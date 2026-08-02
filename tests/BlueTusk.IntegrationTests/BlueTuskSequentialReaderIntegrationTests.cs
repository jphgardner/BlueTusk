using System.Data;
using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskSequentialReaderIntegrationTests
{
    [Fact]
    public async Task Default_sequential_reader_streams_an_unlimited_execute_and_reuses_the_connection()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using (var command = (BlueTuskCommand)connection.CreateCommand())
        {
            Assert.Equal(0, command.SequentialFetchSize);
            command.CommandText =
                "SELECT value FROM generate_series(1, 1000) AS value ORDER BY value";

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess,
                CancellationToken.None);
            Assert.True(reader.HasRows);
            long sum = 0;
            var expected = 1;
            while (await reader.ReadAsync(CancellationToken.None))
            {
                var value = reader.GetInt32(0);
                Assert.Equal(expected++, value);
                sum += value;
            }

            Assert.Equal(1001, expected);
            Assert.Equal(500500, sum);
        }

        await using var reuse = connection.CreateCommand();
        reuse.CommandText = "SELECT 41";
        Assert.Equal(41, await reuse.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public void Sequential_reader_streams_binary_and_text_fields_and_reuses_the_connection()
    {
        using var connection = new BlueTuskConnection(GetConnectionString());
        connection.Open();
        using (var command = (BlueTuskCommand)connection.CreateCommand())
        {
            command.SequentialFetchSize = 2;
            command.CommandText =
                "SELECT value, decode(repeat('ab', 50000), 'hex') AS payload, " +
                "repeat(value::text, 20000)::text AS text_value, " +
                "json_build_object('value', value)::json AS json_value, " +
                "jsonb_build_object('value', value) AS jsonb_value " +
                "FROM generate_series(1, 7) AS value ORDER BY value";

            using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
            Assert.True(reader.HasRows);
            var rowCount = 0;
            var buffer = new byte[4096];
            while (reader.Read())
            {
                rowCount++;
                Assert.Equal(rowCount, reader.GetInt32(0));

                using (var stream = reader.GetStream(1))
                {
                    Assert.Equal(buffer.Length, stream.Read(buffer));
                    Assert.All(buffer, value => Assert.Equal((byte)0xab, value));
                }

                using (var text = reader.GetTextReader(2))
                {
                    var characters = new char[32];
                    Assert.Equal(characters.Length, text.Read(characters));
                    Assert.All(characters, value => Assert.Equal((char)('0' + rowCount), value));
                }

                using (var json = reader.GetTextReader(3))
                {
                    Assert.Contains($"\"value\" : {rowCount}", json.ReadToEnd(), StringComparison.Ordinal);
                }

                using (var jsonb = reader.GetTextReader(4))
                {
                    Assert.Contains($"\"value\": {rowCount}", jsonb.ReadToEnd(), StringComparison.Ordinal);
                }
            }

            Assert.Equal(7, rowCount);
        }

        using var reuse = connection.CreateCommand();
        reuse.CommandText = "SELECT 42";
        Assert.Equal(42, reuse.ExecuteScalar());
    }

    [Fact]
    public async Task Sequential_reader_supports_prepared_parameters_and_early_async_disposal()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT value, decode(repeat('7a', 250000), 'hex') " +
                "FROM generate_series(1, @maximum) AS value ORDER BY value";
            command.Parameters.Add(new BlueTuskParameter<int>(20) { ParameterName = "maximum" });
            await command.PrepareAsync(CancellationToken.None);

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess,
                CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(1, reader.GetInt32(0));
            await using var textBytes = reader.GetStream(1);
            var buffer = new byte[8192];
            Assert.Equal(buffer.Length, await textBytes.ReadAsync(buffer, CancellationToken.None));
            Assert.All(buffer, value => Assert.Equal((byte)'z', value));
        }

        await using var reuse = connection.CreateCommand();
        reuse.CommandText = "SELECT 43";
        Assert.Equal(43, await reuse.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Sequential_reader_cancellation_recovers_the_connection()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT pg_sleep(10), 1";
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess,
                CancellationToken.None);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => reader.ReadAsync(cancellation.Token));
        }

        await using var reuse = connection.CreateCommand();
        reuse.CommandText = "SELECT 44";
        Assert.Equal(44, await reuse.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public void Sequential_reader_timeout_recovers_the_connection()
    {
        using var connection = new BlueTuskConnection(GetConnectionString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT pg_sleep(10), 1";
            command.CommandTimeout = 1;
            using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);

            Assert.Throws<TimeoutException>(() => reader.Read());
        }

        using var reuse = connection.CreateCommand();
        reuse.CommandText = "SELECT 45";
        Assert.Equal(45, reuse.ExecuteScalar());
    }

    [Fact]
    public async Task Data_source_owned_sequential_reader_returns_its_session_to_the_pool()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString(pooling: true));
        await using (var command = dataSource.CreateCommand(
                         "SELECT value, repeat('pool', 10000) " +
                         "FROM generate_series(1, 50) AS value"))
        await using (var reader = await command.ExecuteReaderAsync(
                         CommandBehavior.SequentialAccess,
                         CancellationToken.None))
        {
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(1, reader.GetInt32(0));
        }

        Assert.Equal(0, dataSource.GetPoolStatistics().Busy);
        await using var reuse = dataSource.CreateCommand("SELECT 46");
        Assert.Equal(46, await reuse.ExecuteScalarAsync(CancellationToken.None));
    }

    private static string GetConnectionString(bool pooling = false)
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = pooling,
            MaximumPoolSize = 1,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
