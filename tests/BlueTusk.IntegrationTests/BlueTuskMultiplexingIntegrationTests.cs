using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using BlueTusk.Data;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskMultiplexingIntegrationTests
{
    [Fact]
    public async Task Concurrent_commands_share_a_bounded_statement_lane()
    {
        await using var dataSource = CreateDataSource();
        var tasks = Enumerable.Range(0, 128)
            .Select(async value =>
            {
                await using var command = dataSource.CreateCommand("SELECT $1::int4");
                command.Parameters.Add(new BlueTuskParameter<int>(value));
                return await command.ExecuteScalarAsync<int>();
            })
            .ToArray();

        var values = await Task.WhenAll(tasks);

        Assert.Equal(Enumerable.Range(0, 128), values);
        var statistics = dataSource.GetMultiplexingStatistics();
        Assert.True(statistics.Enabled);
        Assert.Equal(1, statistics.Workers);
        Assert.Equal(128, statistics.Accepted);
        Assert.Equal(128, statistics.Completed);
        Assert.Equal(0, statistics.Faulted);
        Assert.Equal(128, statistics.PipelinedCommands);
        Assert.InRange(statistics.PipelineFlushes, 1, 127);
        Assert.InRange(dataSource.GetPoolStatistics().Opened, 1, 2);
    }

    [Fact]
    public async Task Exhausted_pool_preserves_bounded_admission_and_cancellation()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            MaximumPoolSize = 1,
        };
        await using var dataSource = new BlueTuskDataSourceBuilder(settings.ConnectionString)
            .EnableMultiplexing(options =>
            {
                options.WorkerCount = 1;
                options.QueueCapacity = 1;
                options.MaxCommandsPerLease = 1;
                options.MaxPipelineCommands = 1;
            })
            .Build();
        await using var held = await dataSource.OpenConnectionAsync();
        await using var first = dataSource.CreateCommand("SELECT 40::int4");
        await using var second = dataSource.CreateCommand("SELECT 41::int4");
        await using var canceled = dataSource.CreateCommand("SELECT 99::int4");

        var firstTask = first.ExecuteScalarAsync<int>();
        await WaitUntilAsync(() => dataSource.GetPoolStatistics().Waiting == 1);
        var secondTask = second.ExecuteScalarAsync<int>();
        await WaitUntilAsync(() => dataSource.GetMultiplexingStatistics().Queued >= 1);
        using var admissionCancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.ExecuteScalarAsync<int>(admissionCancellation.Token));

        await held.DisposeAsync();
        Assert.Equal(40, await firstTask);
        Assert.Equal(41, await secondTask);
        await WaitUntilAsync(
            () => dataSource.GetMultiplexingStatistics() is { Queued: 0, Executing: 0 });
        var statistics = dataSource.GetMultiplexingStatistics();
        Assert.Equal(2, statistics.Accepted);
        Assert.Equal(2, statistics.Completed);
        Assert.Equal(0, statistics.Queued);
        Assert.Equal(0, statistics.Executing);
    }

    [Fact]
    public async Task Single_lane_services_accepted_commands_in_fifo_order_without_starvation()
    {
        var table = $"bluetusk_multiplexing_fairness_{Guid.NewGuid():N}";
        await using var dataSource = CreateDataSource();
        await using var setupConnection = await dataSource.OpenConnectionAsync();
        await ExecuteNonQueryAsync(
            setupConnection,
            $"CREATE UNLOGGED TABLE {table} (" +
            "position bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, " +
            "submitted int4 NOT NULL)");

        try
        {
            await using var blocker = dataSource.CreateCommand(
                "SELECT 1::int4 FROM pg_sleep(0.20)");
            var blockerTask = blocker.ExecuteScalarAsync<int>();
            await WaitUntilAsync(
                () => dataSource.GetMultiplexingStatistics().Executing == 1);

            var commands = new List<BlueTuskCommand>();
            var tasks = new List<Task<int>>();
            try
            {
                for (var value = 0; value < 32; value++)
                {
                    var command = dataSource.CreateCommand(
                        $"INSERT INTO {table} (submitted) VALUES ($1) RETURNING submitted");
                    command.Parameters.Add(new BlueTuskParameter<int>(value));
                    commands.Add(command);
                    tasks.Add(command.ExecuteScalarAsync<int>());
                }

                Assert.Equal(1, await blockerTask);
                Assert.Equal(Enumerable.Range(0, 32), await Task.WhenAll(tasks));
            }
            finally
            {
                foreach (var command in commands)
                {
                    await command.DisposeAsync();
                }
            }

            await using var order = new BlueTuskCommand(
                $"SELECT string_agg(submitted::text, ',' ORDER BY position) FROM {table}",
                setupConnection);
            Assert.Equal(
                string.Join(',', Enumerable.Range(0, 32)),
                await order.ExecuteScalarAsync<string>());
        }
        finally
        {
            await ExecuteNonQueryAsync(setupConnection, $"DROP TABLE IF EXISTS {table}");
        }
    }

    [Fact]
    public async Task Lease_rotation_allows_affine_waiters_to_make_progress()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            MaximumPoolSize = 1,
        };
        await using var dataSource = new BlueTuskDataSourceBuilder(settings.ConnectionString)
            .EnableMultiplexing(options =>
            {
                options.WorkerCount = 1;
                options.QueueCapacity = 32;
                options.MaxCommandsPerLease = 2;
                options.MaxPipelineCommands = 1;
            })
            .Build();
        var commands = new List<BlueTuskCommand>();
        var tasks = new List<Task<int>>();
        try
        {
            for (var value = 0; value < 12; value++)
            {
                var command = dataSource.CreateCommand(
                    "SELECT $1::int4 FROM pg_sleep(0.03)");
                command.Parameters.Add(new BlueTuskParameter<int>(value));
                commands.Add(command);
                tasks.Add(command.ExecuteScalarAsync<int>());
            }

            await WaitUntilAsync(
                () => dataSource.GetMultiplexingStatistics().Executing == 1);
            await using (var affine = await dataSource.OpenConnectionAsync()
                             .AsTask()
                             .WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.True(
                    dataSource.GetMultiplexingStatistics().Completed < commands.Count,
                    "The affine waiter was not admitted until the multiplexing backlog drained.");
            }

            Assert.Equal(Enumerable.Range(0, 12), await Task.WhenAll(tasks));
        }
        finally
        {
            foreach (var command in commands)
            {
                await command.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Lease_rotation_keeps_the_lane_when_no_affine_work_is_waiting()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            MaximumPoolSize = 1,
        };
        await using var dataSource = new BlueTuskDataSourceBuilder(settings.ConnectionString)
            .EnableMultiplexing(options =>
            {
                options.WorkerCount = 1;
                options.QueueCapacity = 32;
                options.MaxCommandsPerLease = 2;
                options.MaxPipelineCommands = 1;
            })
            .Build();

        for (var value = 0; value < 12; value++)
        {
            await using var command = dataSource.CreateCommand("SELECT $1::int4");
            command.Parameters.Add(new BlueTuskParameter<int>(value));
            Assert.Equal(value, await command.ExecuteScalarAsync<int>());
        }

        var pool = dataSource.GetPoolStatistics();
        Assert.Equal(1, pool.Opened);
        Assert.Equal(0, pool.Reused);
        Assert.Equal(12, dataSource.GetMultiplexingStatistics().Completed);
    }

    [Fact]
    public async Task Cancellation_recovers_the_lane_for_the_next_command()
    {
        await using var dataSource = CreateDataSource();
        await using var blocked = dataSource.CreateCommand(
            "SELECT pg_sleep(10), 1::int4");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blocked.ExecuteScalarAsync<int>(cancellation.Token));

        await using var next = dataSource.CreateCommand("SELECT 42::int4");
        Assert.Equal(42, await next.ExecuteScalarAsync<int>());
        Assert.Equal(1, dataSource.GetMultiplexingStatistics().Canceled);
    }

    [Fact]
    public async Task Pipeline_groups_isolate_errors_and_cancellation()
    {
        await using var dataSource = CreateDataSource();
        await using var first = dataSource.CreateCommand(
            "SELECT 40::int4 FROM pg_sleep(0.10)");
        await using var invalid = dataSource.CreateCommand(
            "SELECT 1::int4 / 0::int4");
        await using var canceled = dataSource.CreateCommand(
            "SELECT 41::int4 FROM pg_sleep(10)");
        await using var last = dataSource.CreateCommand(
            "SELECT 42::int4");
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(300));

        var firstTask = first.ExecuteScalarAsync<int>();
        var invalidTask = invalid.ExecuteScalarAsync<int>();
        var canceledTask = canceled.ExecuteScalarAsync<int>(cancellation.Token);
        var lastTask = last.ExecuteScalarAsync<int>();

        Assert.Equal(40, await firstTask);
        var serverError = await Assert.ThrowsAsync<BlueTuskException>(
            () => invalidTask);
        Assert.Equal("22012", serverError.SqlState);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledTask);
        Assert.Equal(42, await lastTask);
        Assert.True(dataSource.GetMultiplexingStatistics().PipelineFlushes > 0);
    }

    [Fact]
    public async Task Per_command_timeout_cancels_only_its_pipeline_group()
    {
        await using var dataSource = CreateDataSource();
        await using var timedOut = dataSource.CreateCommand(
            "SELECT 1::int4 FROM pg_sleep(10)");
        timedOut.CommandTimeout = 1;
        await using var next = dataSource.CreateCommand("SELECT 43::int4");

        var timeoutTask = timedOut.ExecuteScalarAsync<int>();
        var nextTask = next.ExecuteScalarAsync<int>();

        await Assert.ThrowsAsync<TimeoutException>(() => timeoutTask);
        Assert.Equal(43, await nextTask);
    }

    [Fact]
    public async Task Session_state_uses_an_affine_lease_and_is_reset_before_reuse()
    {
        await using var dataSource = CreateDataSource();
        await using (var set = dataSource.CreateCommand(
                         "SET application_name = 'leaked-name'"))
        {
            _ = await set.ExecuteNonQueryAsync();
        }

        await using (var current = dataSource.CreateCommand(
                         "SELECT setting FROM pg_settings WHERE name = 'application_name'"))
        {
            current.MultiplexingMode = BlueTuskMultiplexingMode.Require;
            Assert.Equal(
                "multiplexing-integration",
                await current.ExecuteScalarAsync<string>());
        }

        await using var required = dataSource.CreateCommand(
            "SELECT set_config('application_name', 'forbidden', false)");
        required.MultiplexingMode = BlueTuskMultiplexingMode.Require;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => required.ExecuteScalarAsync<string>());
        Assert.Contains("session", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sequential_readers_use_an_affine_lease_and_leave_the_scheduler_idle()
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand(
            "SELECT value::int4 FROM generate_series(1, 4) AS value");
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess);
        var values = new List<int>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt32(0));
        }

        Assert.Equal([1, 2, 3, 4], values);
        Assert.Equal(0, dataSource.GetMultiplexingStatistics().Accepted);
    }

    [Fact]
    public async Task Scheduler_emits_queue_pipeline_and_outcome_metrics()
    {
        var doubleMeasurements = new ConcurrentQueue<MetricMeasurement<double>>();
        var longMeasurements = new ConcurrentQueue<MetricMeasurement<long>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "BlueTusk.Diagnostics" &&
                instrument.Name.StartsWith("bluetusk.multiplexing.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) =>
                doubleMeasurements.Enqueue(
                    new MetricMeasurement<double>(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) =>
                longMeasurements.Enqueue(
                    new MetricMeasurement<long>(instrument.Name, value, tags.ToArray())));
        listener.Start();

        await using var dataSource = CreateDataSource();
        var tasks = Enumerable.Range(0, 8)
            .Select(async value =>
            {
                await using var command = dataSource.CreateCommand("SELECT $1::int4");
                command.Parameters.Add(new BlueTuskParameter<int>(value));
                return await command.ExecuteScalarAsync<int>();
            })
            .ToArray();

        Assert.Equal(Enumerable.Range(0, 8), await Task.WhenAll(tasks));
        Assert.Contains(
            doubleMeasurements,
            measurement => measurement.Name == "bluetusk.multiplexing.queue.wait.duration" &&
                measurement.Value >= 0);
        Assert.Contains(
            longMeasurements,
            measurement => measurement.Name == "bluetusk.multiplexing.pipeline.size" &&
                measurement.Value > 0);
        Assert.Equal(
            8,
            longMeasurements.Count(
                measurement => measurement.Name == "bluetusk.multiplexing.commands" &&
                    measurement.Tags.Any(
                        tag => tag.Key == "bluetusk.multiplexing.command.outcome" &&
                            Equals(tag.Value, "completed"))));
    }

    [Fact]
    public async Task Forced_shutdown_aborts_a_non_cancelable_worker_lane()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            MaximumPoolSize = 2,
        };
        var dataSource = new BlueTuskDataSourceBuilder(settings.ConnectionString)
            .EnableMultiplexing(options =>
            {
                options.WorkerCount = 1;
                options.ShutdownTimeout = TimeSpan.FromMilliseconds(100);
            })
            .Build();
        await using var command = dataSource.CreateCommand(
            "SELECT 1::int4 FROM pg_sleep(10)");
        command.CommandTimeout = 0;
        var execution = command.ExecuteScalarAsync<int>();

        using var dispatchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dataSource.GetMultiplexingStatistics().Executing == 0)
        {
            await Task.Delay(10, dispatchTimeout.Token);
        }

        var started = Stopwatch.GetTimestamp();
        await dataSource.DisposeAsync();

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => execution);
    }

    private static BlueTuskDataSource CreateDataSource()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString())
        {
            ApplicationName = "multiplexing-integration",
            MaximumPoolSize = 2,
        };
        return new BlueTuskDataSourceBuilder(settings.ConnectionString)
            .EnableMultiplexing(options =>
            {
                options.WorkerCount = 1;
                options.QueueCapacity = 256;
                options.MaxCommandsPerLease = 128;
            })
            .Build();
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return connectionString;
    }

    private static async Task ExecuteNonQueryAsync(
        BlueTuskConnection connection,
        string sql)
    {
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        int timeoutSeconds = 5)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(timeoutSeconds));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed record MetricMeasurement<T>(
        string Name,
        T Value,
        KeyValuePair<string, object?>[] Tags);
}
