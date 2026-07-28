using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskPoolingIntegrationTests
{
    [Fact]
    public async Task Sequential_connections_reuse_one_backend_and_report_it()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 2);
        int firstBackend;
        await using (var connection = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            firstBackend = await GetBackendProcessIdAsync(connection);
        }

        await using (var connection = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Equal(firstBackend, await GetBackendProcessIdAsync(connection));
        }

        var statistics = dataSource.GetPoolStatistics();
        Assert.Equal(1, statistics.Total);
        Assert.Equal(1, statistics.Idle);
        Assert.Equal(1, statistics.Opened);
        Assert.Equal(1, statistics.Reused);
    }

    [Fact]
    public async Task Checkout_resets_transactions_temporary_objects_and_session_settings()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 1);
        await using (var connection = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            await using (var command = new BlueTuskCommand(
                             "CREATE TEMP TABLE bluetusk_pool_session_leak (value int4); " +
                             "SET application_name = 'bluetusk-leaked-setting'",
                             connection))
            {
                _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
            }

            var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
            await using var insert = new BlueTuskCommand(
                "INSERT INTO bluetusk_pool_session_leak VALUES (1)",
                connection)
            {
                Transaction = transaction,
            };
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(CancellationToken.None));
            await connection.CloseAsync();
            await transaction.DisposeAsync();
        }

        await using var cleanConnection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var verification = new BlueTuskCommand(
            "SELECT to_regclass('pg_temp.bluetusk_pool_session_leak') IS NULL " +
            "AND current_setting('application_name') <> 'bluetusk-leaked-setting'",
            cleanConnection);

        Assert.True(await verification.ExecuteScalarAsync<bool>(CancellationToken.None));
        Assert.Equal(1, dataSource.GetPoolStatistics().Reused);
    }

    [Fact]
    public async Task Maximum_size_queues_until_a_connection_is_returned()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 1);
        var first = await dataSource.OpenConnectionAsync(CancellationToken.None);
        var firstBackend = await GetBackendProcessIdAsync(first);
        var waiting = dataSource.OpenConnectionAsync(CancellationToken.None).AsTask();
        await WaitUntilAsync(() => dataSource.GetPoolStatistics().Waiting == 1);

        Assert.False(waiting.IsCompleted);
        await first.DisposeAsync();
        await using var second = await waiting;

        Assert.Equal(firstBackend, await GetBackendProcessIdAsync(second));
        Assert.Equal(1, dataSource.GetPoolStatistics().Total);
        Assert.Equal(1, dataSource.GetPoolStatistics().Busy);
    }

    [Fact]
    public async Task Waiting_for_capacity_honours_cancellation()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 1);
        await using var first = await dataSource.OpenConnectionAsync(CancellationToken.None);
        using var cancellationSource = new CancellationTokenSource();
        var waiting = dataSource.OpenConnectionAsync(cancellationSource.Token).AsTask();
        await WaitUntilAsync(() => dataSource.GetPoolStatistics().Waiting == 1);

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, dataSource.GetPoolStatistics().Waiting);
        Assert.Equal(1, dataSource.GetPoolStatistics().Busy);
    }

    [Fact]
    public async Task Clearing_a_pool_rotates_active_connections_when_they_return()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 1);
        var first = await dataSource.OpenConnectionAsync(CancellationToken.None);
        var firstBackend = await GetBackendProcessIdAsync(first);

        await dataSource.ClearPoolAsync();
        await first.DisposeAsync();
        await using var second = await dataSource.OpenConnectionAsync(CancellationToken.None);

        Assert.NotEqual(firstBackend, await GetBackendProcessIdAsync(second));
        Assert.Equal(2, dataSource.GetPoolStatistics().Opened);
        Assert.Equal(1, dataSource.GetPoolStatistics().Discarded);
    }

    [Fact]
    public async Task Warm_up_opens_the_minimum_number_of_physical_connections()
    {
        await using var dataSource = CreateDataSource(minimumPoolSize: 2, maximumPoolSize: 3);

        await dataSource.WarmUpAsync(CancellationToken.None);

        var statistics = dataSource.GetPoolStatistics();
        Assert.Equal(2, statistics.Total);
        Assert.Equal(2, statistics.Idle);
        Assert.Equal(2, statistics.Opened);
    }

    [Fact]
    public async Task Pooling_can_be_disabled_per_data_source()
    {
        await using var dataSource = CreateDataSource(pooling: false);
        int firstBackend;
        await using (var first = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            firstBackend = await GetBackendProcessIdAsync(first);
        }

        await using var second = await dataSource.OpenConnectionAsync(CancellationToken.None);

        Assert.NotEqual(firstBackend, await GetBackendProcessIdAsync(second));
        Assert.False(dataSource.GetPoolStatistics().PoolingEnabled);
    }

    private static BlueTuskDataSource CreateDataSource(
        bool pooling = true,
        int minimumPoolSize = 0,
        int maximumPoolSize = 10)
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            Pooling = pooling,
            MinimumPoolSize = minimumPoolSize,
            MaximumPoolSize = maximumPoolSize,
        };
        return BlueTuskDataSource.Create(settings.ConnectionString);
    }

    private static async Task<int> GetBackendProcessIdAsync(BlueTuskConnection connection)
    {
        await using var command = new BlueTuskCommand("SELECT pg_backend_pid()::int4", connection);
        return await command.ExecuteScalarAsync<int>(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
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
            SslMode = BlueTusk.Client.BlueTuskSslMode.Disable,
            ChannelBinding = BlueTusk.Client.BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
