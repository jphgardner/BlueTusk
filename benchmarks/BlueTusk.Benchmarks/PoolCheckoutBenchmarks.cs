using BenchmarkDotNet.Attributes;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Protocol;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class PoolCheckoutBenchmarks : IAsyncDisposable
{
    private BlueTuskConnectionPool _pool = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var settings = new BlueTuskConnectionStringBuilder
        {
            MaximumPoolSize = 100,
            ConnectionIdleLifetime = TimeSpan.Zero,
            ConnectionLifetime = TimeSpan.Zero,
        };
        _pool = new BlueTuskConnectionPool(
            settings,
            _ => ValueTask.FromResult<IBlueTuskPhysicalSession>(new BenchmarkPhysicalSession()));
        var session = await _pool.RentAsync(CancellationToken.None);
        _pool.Return(session);
    }

    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    [Benchmark]
    public async ValueTask CheckoutWarmSession()
    {
        var session = await _pool.RentAsync(CancellationToken.None);
        _pool.Return(session);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pool is not null)
        {
            await _pool.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private sealed class BenchmarkPhysicalSession : IBlueTuskPhysicalSession
    {
        private static readonly BlueTuskQueryResult EmptyResult = new([]);

        public bool IsOpen { get; private set; } = true;

        public IReadOnlyDictionary<string, string> Parameters { get; } =
            new Dictionary<string, string>();

        public BlueTuskTransactionStatus TransactionStatus => BlueTuskTransactionStatus.Idle;

        public ValueTask<BlueTuskQueryResult> ExecuteSimpleQueryAsync(
            string sql,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(EmptyResult);
        }

        public ValueTask<BlueTuskQueryResult> ExecuteExtendedQueryAsync(
            string sql,
            IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Cancel()
        {
        }

        public ValueTask CancelAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public void Dispose() => IsOpen = false;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
