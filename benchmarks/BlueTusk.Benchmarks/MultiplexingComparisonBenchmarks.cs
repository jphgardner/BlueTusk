using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using BlueTusk.Data;
using Npgsql;

namespace BlueTusk.Benchmarks;

/// <summary>
/// Equivalent bounded concurrent scalar bursts through BlueTusk and Npgsql, with and without
/// statement multiplexing.
/// </summary>
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.Declared)]
public class MultiplexingComparisonBenchmarks : IAsyncDisposable
{
    private const int BurstSize = 64;
    private BlueTuskDataSource _blueTusk = null!;
    private BlueTuskDataSource _blueTuskPooled = null!;
    private NpgsqlDataSource _npgsql = null!;
    private NpgsqlDataSource _npgsqlPooled = null!;
    private BlueTuskCommand[] _blueTuskCommands = null!;
    private BlueTuskCommand[] _blueTuskPooledCommands = null!;
    private NpgsqlCommand[] _npgsqlCommands = null!;
    private NpgsqlCommand[] _npgsqlPooledCommands = null!;
    private int _disposed;

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ProviderComparisonBenchmarks.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{ProviderComparisonBenchmarks.ConnectionStringEnvironmentVariable} must be configured.");
        }

        var blueTuskSettings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            MaximumPoolSize = 4,
        };
        _blueTusk = new BlueTuskDataSourceBuilder(blueTuskSettings.ConnectionString)
            .EnableMultiplexing(options =>
            {
                options.WorkerCount = 4;
                options.QueueCapacity = 256;
                options.MaxCommandsPerLease = 65_536;
                options.MaxPipelineCommands = BurstSize;
            })
            .Build();
        blueTuskSettings.Multiplexing = false;
        _blueTuskPooled = BlueTuskDataSource.Create(blueTuskSettings.ConnectionString);

        var npgsqlSettings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 4,
            Multiplexing = true,
        };
        _npgsql = NpgsqlDataSource.Create(npgsqlSettings.ConnectionString);
        npgsqlSettings.Multiplexing = false;
        _npgsqlPooled = NpgsqlDataSource.Create(npgsqlSettings.ConnectionString);

        _blueTuskCommands = new BlueTuskCommand[BurstSize];
        _blueTuskPooledCommands = new BlueTuskCommand[BurstSize];
        _npgsqlCommands = new NpgsqlCommand[BurstSize];
        _npgsqlPooledCommands = new NpgsqlCommand[BurstSize];
        for (var index = 0; index < BurstSize; index++)
        {
            var blueTuskCommand = _blueTusk.CreateCommand("SELECT $1::int4");
            blueTuskCommand.CommandTimeout = 0;
            blueTuskCommand.Parameters.Add(new BlueTuskParameter<int>(index));
            _blueTuskCommands[index] = blueTuskCommand;

            var blueTuskPooledCommand = _blueTuskPooled.CreateCommand("SELECT $1::int4");
            blueTuskPooledCommand.CommandTimeout = 0;
            blueTuskPooledCommand.Parameters.Add(new BlueTuskParameter<int>(index));
            _blueTuskPooledCommands[index] = blueTuskPooledCommand;

            var npgsqlCommand = _npgsql.CreateCommand("SELECT $1::int4");
            npgsqlCommand.CommandTimeout = 0;
            npgsqlCommand.Parameters.Add(new NpgsqlParameter<int> { TypedValue = index });
            _npgsqlCommands[index] = npgsqlCommand;

            var npgsqlPooledCommand = _npgsqlPooled.CreateCommand("SELECT $1::int4");
            npgsqlPooledCommand.CommandTimeout = 0;
            npgsqlPooledCommand.Parameters.Add(new NpgsqlParameter<int> { TypedValue = index });
            _npgsqlPooledCommands[index] = npgsqlPooledCommand;
        }

        _ = await BlueTuskConcurrentScalarBurstAsync();
        _ = await BlueTuskPooledConcurrentScalarBurstAsync();
        _ = await NpgsqlConcurrentScalarBurstAsync();
        _ = await NpgsqlPooledConcurrentScalarBurstAsync();
        _ = await BlueTuskReusedScalarBurstAsync();
        _ = await BlueTuskPooledReusedScalarBurstAsync();
        _ = await NpgsqlReusedScalarBurstAsync();
        _ = await NpgsqlPooledReusedScalarBurstAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    [Benchmark(Baseline = true, OperationsPerInvoke = BurstSize)]
    [BenchmarkCategory("ConcurrentMultiplexedScalar")]
    public async Task<int> BlueTuskConcurrentScalarBurstAsync()
    {
        var tasks = new Task<int>[BurstSize];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = ExecuteBlueTuskScalarAsync(index);
        }

        await Task.WhenAll(tasks);
        var sum = 0;
        foreach (var task in tasks)
        {
            sum += task.Result;
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = BurstSize)]
    [BenchmarkCategory("ConcurrentMultiplexedScalar")]
    public async Task<int> NpgsqlConcurrentScalarBurstAsync()
    {
        var tasks = new Task<int>[BurstSize];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = ExecuteNpgsqlScalarAsync(index);
        }

        await Task.WhenAll(tasks);
        var sum = 0;
        foreach (var task in tasks)
        {
            sum += task.Result;
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = BurstSize)]
    [BenchmarkCategory("ConcurrentMultiplexedScalar")]
    public Task<int> BlueTuskPooledConcurrentScalarBurstAsync() =>
        ExecuteBlueTuskBurstAsync(_blueTuskPooled);

    [Benchmark(OperationsPerInvoke = BurstSize)]
    [BenchmarkCategory("ConcurrentMultiplexedScalar")]
    public Task<int> NpgsqlPooledConcurrentScalarBurstAsync() =>
        ExecuteNpgsqlBurstAsync(_npgsqlPooled);

    [Benchmark(Baseline = true, OperationsPerInvoke = BurstSize)]
    [BenchmarkCategory("ReusedMultiplexedScalar")]
    public async Task<int> BlueTuskReusedScalarBurstAsync()
    {
        var tasks = new Task<int>[BurstSize];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = _blueTuskCommands[index].ExecuteScalarAsync<int>();
        }

        await Task.WhenAll(tasks);
        var sum = 0;
        foreach (var task in tasks)
        {
            sum += task.Result;
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = BurstSize)]
    [BenchmarkCategory("ReusedMultiplexedScalar")]
    public async Task<int> NpgsqlReusedScalarBurstAsync()
    {
        var tasks = new Task<int>[BurstSize];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = ExecuteNpgsqlScalarAsync(_npgsqlCommands[index]);
        }

        await Task.WhenAll(tasks);
        var sum = 0;
        foreach (var task in tasks)
        {
            sum += task.Result;
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = BurstSize)]
    [BenchmarkCategory("ReusedMultiplexedScalar")]
    public Task<int> BlueTuskPooledReusedScalarBurstAsync() =>
        ExecuteBlueTuskCommandsAsync(_blueTuskPooledCommands);

    [Benchmark(OperationsPerInvoke = BurstSize)]
    [BenchmarkCategory("ReusedMultiplexedScalar")]
    public Task<int> NpgsqlPooledReusedScalarBurstAsync() =>
        ExecuteNpgsqlCommandsAsync(_npgsqlPooledCommands);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var command in _blueTuskCommands)
        {
            await command.DisposeAsync();
        }

        foreach (var command in _blueTuskPooledCommands)
        {
            await command.DisposeAsync();
        }

        foreach (var command in _npgsqlCommands)
        {
            await command.DisposeAsync();
        }

        foreach (var command in _npgsqlPooledCommands)
        {
            await command.DisposeAsync();
        }

        await _blueTusk.DisposeAsync();
        await _blueTuskPooled.DisposeAsync();
        await _npgsql.DisposeAsync();
        await _npgsqlPooled.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<int> ExecuteBlueTuskScalarAsync(int value)
    {
        await using var command = _blueTusk.CreateCommand("SELECT $1::int4");
        command.CommandTimeout = 0;
        command.Parameters.Add(new BlueTuskParameter<int>(value));
        return await command.ExecuteScalarAsync<int>();
    }

    private async Task<int> ExecuteNpgsqlScalarAsync(int value)
    {
        await using var command = _npgsql.CreateCommand("SELECT $1::int4");
        command.CommandTimeout = 0;
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = value });
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ExecuteNpgsqlScalarAsync(NpgsqlCommand command) =>
        (int)(await command.ExecuteScalarAsync())!;

    private static async Task<int> ExecuteBlueTuskCommandsAsync(
        BlueTuskCommand[] commands)
    {
        var tasks = new Task<int>[commands.Length];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = commands[index].ExecuteScalarAsync<int>();
        }

        await Task.WhenAll(tasks);
        return tasks.Sum(static task => task.Result);
    }

    private static async Task<int> ExecuteNpgsqlCommandsAsync(
        NpgsqlCommand[] commands)
    {
        var tasks = new Task<int>[commands.Length];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = ExecuteNpgsqlScalarAsync(commands[index]);
        }

        await Task.WhenAll(tasks);
        return tasks.Sum(static task => task.Result);
    }

    private static async Task<int> ExecuteBlueTuskBurstAsync(BlueTuskDataSource dataSource)
    {
        var tasks = new Task<int>[BurstSize];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = ExecuteBlueTuskScalarAsync(dataSource, index);
        }

        await Task.WhenAll(tasks);
        return tasks.Sum(static task => task.Result);
    }

    private static async Task<int> ExecuteNpgsqlBurstAsync(NpgsqlDataSource dataSource)
    {
        var tasks = new Task<int>[BurstSize];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = ExecuteNpgsqlScalarAsync(dataSource, index);
        }

        await Task.WhenAll(tasks);
        return tasks.Sum(static task => task.Result);
    }

    private static async Task<int> ExecuteBlueTuskScalarAsync(
        BlueTuskDataSource dataSource,
        int value)
    {
        await using var command = dataSource.CreateCommand("SELECT $1::int4");
        command.CommandTimeout = 0;
        command.Parameters.Add(new BlueTuskParameter<int>(value));
        return await command.ExecuteScalarAsync<int>();
    }

    private static async Task<int> ExecuteNpgsqlScalarAsync(
        NpgsqlDataSource dataSource,
        int value)
    {
        await using var command = dataSource.CreateCommand("SELECT $1::int4");
        command.CommandTimeout = 0;
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = value });
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
