using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlueTusk.Benchmarks;

/// <summary>Representative live EF Core compilation, materialization, and write workloads.</summary>
[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
[Orderer(SummaryOrderPolicy.Declared)]
public class EntityFrameworkCoreBenchmarks : IAsyncDisposable
{
    private const int WriteOperations = 16;
    private BlueTuskDataSource _dataSource = null!;
    private DbContextOptions<BenchmarkContext> _options = null!;
    private int _writeSequence;
    private int _disposed;

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = GetConnectionString();
        _dataSource = BlueTuskDataSource.Create(connectionString);
        _options = new DbContextOptionsBuilder<BenchmarkContext>()
            .UseBlueTusk(_dataSource)
            .Options;

        await ExecuteAsync("DROP TABLE IF EXISTS bluetusk_benchmark_ef_orders");
        await ExecuteAsync(
            """
            CREATE TABLE bluetusk_benchmark_ef_orders (
                id int4 GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                customer text NOT NULL,
                total numeric(12,2) NOT NULL,
                updated_at timestamptz NOT NULL)
            """);
        await ExecuteAsync(
            """
            INSERT INTO bluetusk_benchmark_ef_orders (customer, total, updated_at)
            SELECT
                'customer-' || value::text,
                value::numeric / 10,
                '2026-01-01 00:00:00+00'::timestamptz + value * interval '1 minute'
            FROM generate_series(1, 1000) AS value
            """);

        await using var context = CreateContext();
        _ = await context.Orders.AsNoTracking().Where(order => order.Id <= 100).ToListAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    [Benchmark]
    public async Task<int> CompileAndExecuteParameterizedQueryAsync()
    {
        var compiled = EF.CompileAsyncQuery(
            (BenchmarkContext context, int minimumId) => context.Orders
                .AsNoTracking()
                .Where(order => order.Id >= minimumId)
                .OrderBy(order => order.Id)
                .Select(order => order.Id)
                .Take(1));
        await using var context = CreateContext();
        var count = 0;
        await foreach (var value in compiled(context, 450))
        {
            count += value > 0 ? 1 : 0;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> MaterializeOneHundredOrdersAsync()
    {
        await using var context = CreateContext();
        var orders = await context.Orders
            .AsNoTracking()
            .Where(order => order.Id >= 450 && order.Id < 550)
            .OrderBy(order => order.Id)
            .ToListAsync();
        return orders.Count;
    }

    [Benchmark(OperationsPerInvoke = WriteOperations)]
    public async Task<int> InsertOrdersAsync()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var affected = 0;
        for (var index = 0; index < WriteOperations; index++)
        {
            var sequence = Interlocked.Increment(ref _writeSequence);
            context.Orders.Add(
                new BenchmarkOrder
                {
                    Customer = $"insert-{sequence.ToString(CultureInfo.InvariantCulture)}",
                    Total = 42.50m,
                    UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                });
            affected += await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        await transaction.RollbackAsync();
        return affected;
    }

    [Benchmark(OperationsPerInvoke = WriteOperations)]
    public async Task<int> LoadAndUpdateOrdersAsync()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var affected = 0;
        for (var index = 0; index < WriteOperations; index++)
        {
            var sequence = Interlocked.Increment(ref _writeSequence);
            var id = 1 + sequence % 1000;
            var order = await context.Orders.SingleAsync(candidate => candidate.Id == id);
            order.Customer = sequence % 2 == 0 ? "updated-even" : "updated-odd";
            affected += await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        await transaction.RollbackAsync();
        return affected;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_dataSource is not null)
        {
            try
            {
                await ExecuteAsync("DROP TABLE IF EXISTS bluetusk_benchmark_ef_orders");
            }
            finally
            {
                await _dataSource.DisposeAsync();
            }
        }

        GC.SuppressFinalize(this);
    }

    private BenchmarkContext CreateContext() => new(_options);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new BlueTuskCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ProviderComparisonBenchmarks.ConnectionStringEnvironmentVariable);
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException(
                $"{ProviderComparisonBenchmarks.ConnectionStringEnvironmentVariable} must be configured.")
            : connectionString;
    }

    private sealed class BenchmarkContext(DbContextOptions<BenchmarkContext> options) : DbContext(options)
    {
        public DbSet<BenchmarkOrder> Orders => Set<BenchmarkOrder>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var order = modelBuilder.Entity<BenchmarkOrder>();
            ConfigureOrder(order);
        }

        private static void ConfigureOrder(EntityTypeBuilder<BenchmarkOrder> order)
        {
            order.ToTable("bluetusk_benchmark_ef_orders");
            order.HasKey(entity => entity.Id);
            order.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedOnAdd();
            order.Property(entity => entity.Customer).HasColumnName("customer");
            order.Property(entity => entity.Total).HasColumnName("total").HasPrecision(12, 2);
            order.Property(entity => entity.UpdatedAt).HasColumnName("updated_at");
        }
    }

    private sealed class BenchmarkOrder
    {
        public int Id { get; set; }

        public string Customer { get; set; } = string.Empty;

        public decimal Total { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
