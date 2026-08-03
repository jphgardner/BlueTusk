using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using BlueTusk.Data;
using Npgsql;

namespace BlueTusk.Benchmarks;

/// <summary>Equivalent bounded concurrent scalar bursts through BlueTusk and Npgsql multiplexing.</summary>
[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.Declared)]
public class MultiplexingComparisonBenchmarks : IAsyncDisposable
{
    private const int BurstSize = 64;
    private BlueTuskDataSource _blueTusk = null!;
    private NpgsqlDataSource _npgsql = null!;
    private BlueTuskCommand[] _blueTuskCommands = null!;
    private NpgsqlCommand[] _npgsqlCommands = null!;
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

        var npgsqlSettings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 4,
            Multiplexing = true,
        };
        _npgsql = NpgsqlDataSource.Create(npgsqlSettings.ConnectionString);

        _blueTuskCommands = new BlueTuskCommand[BurstSize];
        _npgsqlCommands = new NpgsqlCommand[BurstSize];
        for (var index = 0; index < BurstSize; index++)
        {
            var blueTuskCommand = _blueTusk.CreateCommand("SELECT $1::int4");
            blueTuskCommand.CommandTimeout = 0;
            blueTuskCommand.Parameters.Add(new BlueTuskParameter<int>(index));
            _blueTuskCommands[index] = blueTuskCommand;

            var npgsqlCommand = _npgsql.CreateCommand("SELECT $1::int4");
            npgsqlCommand.CommandTimeout = 0;
            npgsqlCommand.Parameters.Add(new NpgsqlParameter<int> { TypedValue = index });
            _npgsqlCommands[index] = npgsqlCommand;
        }

        _ = await BlueTuskConcurrentScalarBurstAsync();
        _ = await NpgsqlConcurrentScalarBurstAsync();
        _ = await BlueTuskReusedScalarBurstAsync();
        _ = await NpgsqlReusedScalarBurstAsync();
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

        foreach (var command in _npgsqlCommands)
        {
            await command.DisposeAsync();
        }

        await _blueTusk.DisposeAsync();
        await _npgsql.DisposeAsync();
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
}
