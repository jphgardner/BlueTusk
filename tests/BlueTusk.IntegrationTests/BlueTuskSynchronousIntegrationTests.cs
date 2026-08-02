using BlueTusk.Client;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskSynchronousIntegrationTests
{
    [Fact]
    public void AdoNet_sync_paths_cover_pooling_types_commands_transactions_and_batches()
    {
        var settings = CreateSettings();
        settings.MinimumPoolSize = 1;
        settings.MaximumPoolSize = 2;
        using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);

        dataSource.WarmUp();
        Assert.Equal(1, dataSource.GetPoolStatistics().Idle);
        using (var connection = dataSource.OpenConnection())
        {
            var capabilities = Assert.IsType<BlueTuskServerCapabilities>(connection.ServerCapabilities);
            Assert.True(capabilities.ServerVersion.Major >= 14);
            Assert.Equal(capabilities.ServerVersion.Major >= 14, capabilities.SupportsPipelineMode);
            Assert.Equal(capabilities.ServerVersion.Major >= 15, capabilities.SupportsMerge);
            Assert.Equal(capabilities.ServerVersion.Major >= 14, capabilities.SupportsMultiranges);
            Assert.Equal(capabilities.ServerVersion.Major >= 18, capabilities.SupportsVirtualGeneratedColumns);
            Assert.Equal(capabilities.ServerVersion.Major >= 19, capabilities.SupportsSqlPgq);
            Assert.False(capabilities.SupportsOAuthBearer);

            using var prepared = new BlueTuskCommand(
                "SELECT @value::int4 + 1",
                connection);
            prepared.Parameters.Add(
                new BlueTuskParameter<int>(41) { ParameterName = "value" });
            prepared.Prepare();
            Assert.Equal(42, prepared.ExecuteScalar());

            using (var readerCommand = new BlueTuskCommand(
                "SELECT 42::int4 AS answer, 'sync'::text AS mode",
                connection))
            using (var reader = readerCommand.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal(42, reader.GetInt32(0));
                Assert.Equal("sync", reader.GetString(1));
                Assert.False(reader.Read());
            }

            using (var setup = new BlueTuskCommand(
                "CREATE TEMP TABLE bluetusk_sync_transactions (value int4 NOT NULL)",
                connection))
            {
                _ = setup.ExecuteNonQuery();
            }

            using (var transaction = connection.BeginTransaction())
            {
                using var insert = new BlueTuskCommand(
                    "INSERT INTO bluetusk_sync_transactions VALUES (42)",
                    connection)
                {
                    Transaction = (BlueTuskTransaction)transaction,
                };
                Assert.Equal(1, insert.ExecuteNonQuery());
                transaction.Rollback();
            }

            using (var count = new BlueTuskCommand(
                "SELECT count(*) FROM bluetusk_sync_transactions",
                connection))
            {
                Assert.Equal(0L, count.ExecuteScalar());
            }

            using var batch = connection.CreateBatch();
            batch.BatchCommands.Add("SELECT 41::int4");
            batch.BatchCommands.Add("SELECT 42::int4");
            batch.Prepare();
            using var batchReader = batch.ExecuteReader();
            Assert.True(batchReader.Read());
            Assert.Equal(41, batchReader.GetInt32(0));
            Assert.True(batchReader.NextResult());
            Assert.True(batchReader.Read());
            Assert.Equal(42, batchReader.GetInt32(0));
        }

        using (var owned = dataSource.CreateCommand("SELECT 42::int4"))
        {
            Assert.Equal(42, owned.ExecuteScalar());
        }

        var statistics = dataSource.GetPoolStatistics();
        Assert.Equal(0, statistics.Busy);
        Assert.True(statistics.Reused >= 2);
    }

    [Fact]
    public void Sync_command_timeout_cancels_and_recovers_the_connection()
    {
        using var connection = new BlueTuskConnection(CreateSettings().ConnectionString);
        connection.Open();
        using (var timed = new BlueTuskCommand("SELECT pg_sleep(10)", connection)
        {
            CommandTimeout = 1,
        })
        {
            _ = Assert.Throws<TimeoutException>(() => timed.ExecuteScalar());
        }

        using var valid = new BlueTuskCommand("SELECT 42::int4", connection);
        Assert.Equal(42, valid.ExecuteScalar());
    }

    [Fact]
    public async Task Sync_pool_waits_for_capacity_and_multi_host_open_fails_over()
    {
        var poolSettings = CreateSettings();
        poolSettings.MaximumPoolSize = 1;
        using (var dataSource = BlueTuskDataSource.Create(poolSettings.ConnectionString))
        {
            var first = dataSource.OpenConnection();
            var waiting = Task.Run(() =>
            {
                using var connection = dataSource.OpenConnection();
                using var command = new BlueTuskCommand("SELECT 42::int4", connection);
                return command.ExecuteScalar();
            });
            Assert.True(
                SpinWait.SpinUntil(
                    () => dataSource.GetPoolStatistics().Waiting == 1,
                    TimeSpan.FromSeconds(5)));
            Assert.False(waiting.IsCompleted);
            first.Dispose();
            Assert.Equal(42, await waiting);
        }

        var failoverSettings = CreateSettings();
        var configured = failoverSettings.HostEndpoints[0];
        failoverSettings.Host = $"{configured.Host},{configured.Host}";
        failoverSettings.Ports = $"1,{configured.Port}";
        failoverSettings.Pooling = false;
        failoverSettings.TargetSessionAttributes = BlueTuskTargetSessionAttributes.Primary;
        using var failover = new BlueTuskConnection(failoverSettings.ConnectionString);
        failover.Open();
        Assert.Equal(configured, failover.ConnectedEndpoint);
    }

    private static BlueTuskConnectionStringBuilder CreateSettings()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
    }
}
