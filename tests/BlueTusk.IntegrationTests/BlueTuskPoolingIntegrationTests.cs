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
    public async Task Data_source_scalar_prepends_pool_reset_without_leaking_session_state()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 1);
        await using (var warmup = dataSource.CreateCommand("SELECT $1::int4"))
        {
            warmup.Parameters.Add(new BlueTuskParameter<int>(42));
            Assert.Equal(42, await warmup.ExecuteScalarAsync<int>(CancellationToken.None));
        }

        await using (var connection = await dataSource.OpenConnectionAsync(CancellationToken.None))
        await using (var dirty = new BlueTuskCommand(
                         "CREATE TEMP TABLE bluetusk_deferred_reset_leak (value int4); " +
                         "SET application_name = 'bluetusk-deferred-reset-leak'",
                         connection))
        {
            _ = await dirty.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var verification = dataSource.CreateCommand(
            "SELECT to_regclass('pg_temp.bluetusk_deferred_reset_leak') IS NULL " +
            "AND current_setting('application_name') <> $1::text");
        verification.Parameters.Add(new BlueTuskParameter<string>("bluetusk-deferred-reset-leak"));

        Assert.True(await verification.ExecuteScalarAsync<bool>(CancellationToken.None));
        Assert.Equal(1, dataSource.GetPoolStatistics().Total);
        Assert.True(dataSource.GetPoolStatistics().Reused >= 2);
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
    public async Task Exhausted_pool_wait_times_out_after_the_connection_timeout()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 1, timeout: TimeSpan.FromSeconds(1));
        await using var first = await dataSource.OpenConnectionAsync(CancellationToken.None);
        var firstBackend = await GetBackendProcessIdAsync(first);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var asynchronous = await Assert.ThrowsAnyAsync<TimeoutException>(
            () => dataSource.OpenConnectionAsync(CancellationToken.None).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(30)));
        var asynchronousElapsed = started.Elapsed;
        var synchronous = await Task.Run(
            () => Assert.ThrowsAny<TimeoutException>(() => dataSource.OpenConnection()))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.InRange(asynchronousElapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(10));
        foreach (var failure in new[] { asynchronous, synchronous })
        {
            Assert.Contains("exhausted", failure.Message, StringComparison.Ordinal);
            Assert.Contains("'Maximum Pool Size' (currently 1)", failure.Message, StringComparison.Ordinal);
            Assert.Contains("1-second Timeout", failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, dataSource.GetPoolStatistics().Waiting);
        Assert.Equal(1, dataSource.GetPoolStatistics().Busy);
        await first.DisposeAsync();
        await using var reused = await dataSource.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(firstBackend, await GetBackendProcessIdAsync(reused));
    }

    [Fact]
    public async Task Exhausted_pool_wait_honours_cancellation_before_the_connection_timeout()
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 1, timeout: TimeSpan.FromSeconds(30));
        await using var first = await dataSource.OpenConnectionAsync(CancellationToken.None);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dataSource.OpenConnectionAsync(cancellationSource.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10)));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposing_a_data_source_releases_both_checkout_apis(bool asynchronousDisposal)
    {
        await using var dataSource = CreateDataSource(maximumPoolSize: 1);
        await using var held = await dataSource.OpenConnectionAsync(CancellationToken.None);
        var backend = await GetBackendProcessIdAsync(held);
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronousCaller = new Thread(() =>
        {
            completed.TrySetResult(Record.Exception(() =>
            {
                using var unexpectedConnection = dataSource.OpenConnection();
            }));
        })
        { IsBackground = true };
        var asynchronousCaller = dataSource.OpenConnectionAsync(CancellationToken.None).AsTask();
        try
        {
            synchronousCaller.Start();
            await WaitUntilAsync(() => dataSource.GetPoolStatistics().Waiting == 2 &&
                (synchronousCaller.ThreadState & ThreadState.WaitSleepJoin) != 0);

            if (asynchronousDisposal) { await dataSource.DisposeAsync(); }
            else { dataSource.Dispose(); }

            Assert.IsType<ObjectDisposedException>(
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => asynchronousCaller.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, dataSource.GetPoolStatistics().Waiting);
            Assert.Equal(1, dataSource.GetPoolStatistics().Busy);
            // Disposing the source rejects new leases but must not close a
            // connection still owned by a caller; it is discarded on return.
            Assert.Equal(backend, await GetBackendProcessIdAsync(held));
        }
        finally
        {
            if (synchronousCaller.IsAlive)
            {
                try { synchronousCaller.Interrupt(); }
                catch (ThreadStateException) { }
            }
            if ((synchronousCaller.ThreadState & ThreadState.Unstarted) == 0)
            {
                Assert.True(synchronousCaller.Join(TimeSpan.FromSeconds(5)));
            }
            await held.CloseAsync();
        }
        Assert.Equal(0, dataSource.GetPoolStatistics().Total);
        Assert.Equal(0, dataSource.GetPoolStatistics().Busy);
        Assert.Equal(0, dataSource.GetPoolStatistics().Idle);
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
        int maximumPoolSize = 10,
        TimeSpan? timeout = null)
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            Pooling = pooling,
            MinimumPoolSize = minimumPoolSize,
            MaximumPoolSize = maximumPoolSize,
        };
        if (timeout is { } connectionTimeout)
        {
            settings.Timeout = connectionTimeout;
        }

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
