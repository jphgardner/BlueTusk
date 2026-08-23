using System.Data;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using BlueTusk.Data;
using Npgsql;

namespace BlueTusk.Benchmarks;

/// <summary>
/// Equivalent live PostgreSQL hot paths through BlueTusk and Npgsql. PostgreSQL is the
/// correctness authority; Npgsql is a mature-provider performance reference only.
/// </summary>
[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.Declared)]
public class ProviderComparisonBenchmarks : IAsyncDisposable
{
    public const string ConnectionStringEnvironmentVariable =
        "BLUETUSK_BENCHMARK_CONNECTION_STRING";

    private readonly byte[] _blueTuskBuffer = new byte[128 * 1024];
    private readonly byte[] _npgsqlBuffer = new byte[128 * 1024];
    private BlueTuskDataSource _blueTuskDataSource = null!;
    private NpgsqlDataSource _npgsqlDataSource = null!;
    private BlueTuskConnection _blueTuskConnection = null!;
    private NpgsqlConnection _npgsqlConnection = null!;
    private BlueTuskCommand _blueTuskPreparedCommand = null!;
    private NpgsqlCommand _npgsqlPreparedCommand = null!;
    private int _disposed;

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringEnvironmentVariable} must be configured.");
        }

        _blueTuskDataSource = BlueTuskDataSource.Create(connectionString);
        _npgsqlDataSource = NpgsqlDataSource.Create(connectionString);
        _blueTuskConnection = await _blueTuskDataSource.OpenConnectionAsync();
        _npgsqlConnection = await _npgsqlDataSource.OpenConnectionAsync();

        const string createPayloadTable =
            "CREATE TEMP TABLE bluetusk_benchmark_payload ON COMMIT PRESERVE ROWS " +
            "AS SELECT decode(repeat('ab', 1048576), 'hex') AS payload";
        await using (var command = new BlueTuskCommand(createPayloadTable, _blueTuskConnection))
        {
            _ = await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(createPayloadTable, _npgsqlConnection))
        {
            _ = await command.ExecuteNonQueryAsync();
        }

        _blueTuskPreparedCommand = new BlueTuskCommand(
            "SELECT @value::int4 + 1",
            _blueTuskConnection);
        _blueTuskPreparedCommand.CommandTimeout = 0;
        _blueTuskPreparedCommand.Parameters.Add(
            new BlueTuskParameter<int>(41) { ParameterName = "value" });
        await _blueTuskPreparedCommand.PrepareAsync();

        _npgsqlPreparedCommand = new NpgsqlCommand(
            "SELECT @value::int4 + 1",
            _npgsqlConnection);
        _npgsqlPreparedCommand.CommandTimeout = 0;
        _npgsqlPreparedCommand.Parameters.AddWithValue("value", 41);
        await _npgsqlPreparedCommand.PrepareAsync();

        _ = await _blueTuskPreparedCommand.ExecuteScalarAsync<int>();
        _ = await _npgsqlPreparedCommand.ExecuteScalarAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _blueTuskPreparedCommand.DisposeAsync();
        await _npgsqlPreparedCommand.DisposeAsync();
        await _blueTuskConnection.DisposeAsync();
        await _npgsqlConnection.DisposeAsync();
        await _blueTuskDataSource.DisposeAsync();
        await _npgsqlDataSource.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("PoolCheckout")]
    public async Task BlueTuskPoolCheckoutAsync()
    {
        await using var connection = await _blueTuskDataSource.OpenConnectionAsync();
    }

    [Benchmark]
    [BenchmarkCategory("PoolCheckout")]
    public async Task NpgsqlPoolCheckoutAsync()
    {
        await using var connection = await _npgsqlDataSource.OpenConnectionAsync();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ParameterizedScalar")]
    public async Task<int> BlueTuskParameterizedScalarAsync()
    {
        await using var command = new BlueTuskCommand(
            "SELECT @value::int4 + 1",
            _blueTuskConnection);
        command.Parameters.Add(new BlueTuskParameter<int>(41) { ParameterName = "value" });
        return await command.ExecuteScalarAsync<int>();
    }

    [Benchmark]
    [BenchmarkCategory("ParameterizedScalar")]
    public async Task<int> NpgsqlParameterizedScalarAsync()
    {
        await using var command = new NpgsqlCommand(
            "SELECT @value::int4 + 1",
            _npgsqlConnection);
        command.Parameters.AddWithValue("value", 41);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("PreparedScalar")]
    public Task<int> BlueTuskPreparedScalarAsync() =>
        _blueTuskPreparedCommand.ExecuteScalarAsync<int>();

    [Benchmark]
    [BenchmarkCategory("PreparedScalar")]
    public async Task<int> NpgsqlPreparedScalarAsync() =>
        (int)(await _npgsqlPreparedCommand.ExecuteScalarAsync())!;

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Sequential1000Rows")]
    public async Task<long> BlueTuskSequential1000RowsAsync()
    {
        await using var command = new BlueTuskCommand(
            "SELECT value FROM generate_series(1, 1000) AS value",
            _blueTuskConnection);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess);
        long sum = 0;
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt32(0);
        }

        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("Sequential1000Rows")]
    public async Task<long> NpgsqlSequential1000RowsAsync()
    {
        await using var command = new NpgsqlCommand(
            "SELECT value FROM generate_series(1, 1000) AS value",
            _npgsqlConnection);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess);
        long sum = 0;
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt32(0);
        }

        return sum;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("SequentialOneMegabyteBytea")]
    public async Task<long> BlueTuskSequentialOneMegabyteByteaAsync()
    {
        await using var command = new BlueTuskCommand(
            "SELECT payload FROM bluetusk_benchmark_payload",
            _blueTuskConnection);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess);
        _ = await reader.ReadAsync();
        await using var stream = reader.GetStream(0);
        return await DrainAsync(stream, _blueTuskBuffer);
    }

    [Benchmark]
    [BenchmarkCategory("SequentialOneMegabyteBytea")]
    public async Task<long> NpgsqlSequentialOneMegabyteByteaAsync()
    {
        await using var command = new NpgsqlCommand(
            "SELECT payload FROM bluetusk_benchmark_payload",
            _npgsqlConnection);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess);
        _ = await reader.ReadAsync();
        await using var stream = reader.GetStream(0);
        return await DrainAsync(stream, _npgsqlBuffer);
    }

    private static async Task<long> DrainAsync(Stream stream, byte[] buffer)
    {
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) != 0)
        {
            total += read;
        }

        return total;
    }
}
