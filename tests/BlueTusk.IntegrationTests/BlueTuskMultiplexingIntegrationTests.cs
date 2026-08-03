using System.Diagnostics;
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
                         "SELECT current_setting('application_name')"))
        {
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
}
