using System.Globalization;
using BenchmarkDotNet.Attributes;
using BlueTusk.Data;
using BlueTusk.Data.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;

namespace BlueTusk.Benchmarks;

#pragma warning disable CS0618 // Npgsql's only like-for-like large-object stream API is obsolete.

public partial class ProviderComparisonBenchmarks
{
    private const int CopyRowCount = 1_000;
    private const int BatchCommandCount = 16;
    private const int EfMaterializedRowCount = 100;
    private static readonly int[] TypedIntegers = [1, 2, 3, 5, 8, 13];
    private static readonly Guid BenchmarkGuid =
        Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private readonly byte[] _blueTuskLargeObjectBuffer = new byte[128 * 1024];
    private readonly byte[] _npgsqlLargeObjectBuffer = new byte[128 * 1024];
    private readonly byte[] _largeObjectPayload = Enumerable.Range(0, 1_048_576)
        .Select(static value => (byte)(value & byte.MaxValue))
        .ToArray();
    private readonly string _bulkTableName = $"bluetusk_benchmark_bulk_{Guid.NewGuid():N}";
    private const string EfTableName = "bluetusk_benchmark_provider_ef";
    private readonly string _blueTuskNotificationChannel =
        $"bluetusk_benchmark_bt_{Guid.NewGuid():N}";
    private readonly string _npgsqlNotificationChannel =
        $"bluetusk_benchmark_np_{Guid.NewGuid():N}";
    private BlueTuskCommand _blueTuskTypedCommand = null!;
    private NpgsqlCommand _npgsqlTypedCommand = null!;
    private BlueTuskConnection _blueTuskNotificationConnection = null!;
    private NpgsqlConnection _npgsqlNotificationConnection = null!;
    private IAsyncEnumerator<BlueTuskNotification> _blueTuskNotificationEnumerator = null!;
    private TaskCompletionSource<NpgsqlNotificationEventArgs>? _npgsqlNotificationCompletion;
    private BlueTuskCommand _blueTuskNotifyCommand = null!;
    private NpgsqlCommand _npgsqlNotifyCommand = null!;
    private NpgsqlLargeObjectManager _npgsqlLargeObjectManager = null!;
    private uint _blueTuskLargeObjectId;
    private uint _npgsqlLargeObjectId;
    private DbContextOptions<ProviderBenchmarkContext> _blueTuskEfOptions = null!;
    private DbContextOptions<ProviderBenchmarkContext> _npgsqlEfOptions = null!;
    private Func<ProviderBenchmarkContext, int, IAsyncEnumerable<int>> _blueTuskCompiledQuery = null!;
    private Func<ProviderBenchmarkContext, int, IAsyncEnumerable<int>> _npgsqlCompiledQuery = null!;
    private int _efWriteSequence;

    private async Task SetupExtendedAsync(string connectionString)
    {
        var quotedBulkTable = $"\"{_bulkTableName}\"";
        var quotedEfTable = $"\"{EfTableName}\"";
        await ExecuteBlueTuskNonQueryAsync($"DROP TABLE IF EXISTS {quotedBulkTable}");
        await ExecuteBlueTuskNonQueryAsync($"DROP TABLE IF EXISTS {quotedEfTable}");
        await ExecuteBlueTuskNonQueryAsync(
            $"CREATE UNLOGGED TABLE {quotedBulkTable} " +
            "(id int4 NOT NULL, name text NOT NULL, active bool NOT NULL, token uuid NOT NULL)");
        await ExecuteBlueTuskNonQueryAsync(
            $"CREATE UNLOGGED TABLE {quotedEfTable} (" +
            "id int4 GENERATED ALWAYS AS IDENTITY PRIMARY KEY, " +
            "customer text NOT NULL, total numeric(12,2) NOT NULL, " +
            "updated_at timestamptz NOT NULL)");
        await ExecuteBlueTuskNonQueryAsync(
            $"INSERT INTO {quotedEfTable} (customer, total, updated_at) " +
            "SELECT 'customer-' || value::text, value::numeric / 10, " +
            "'2026-01-01 00:00:00+00'::timestamptz + value * interval '1 minute' " +
            "FROM generate_series(1, 1000) AS value");

        _blueTuskTypedCommand = new BlueTuskCommand(
            "SELECT $1::int4, $2::text, $3::uuid, $4::numeric, " +
            "$5::timestamptz, $6::int4[], $7::text::jsonb",
            _blueTuskConnection);
        _blueTuskTypedCommand.Parameters.Add(new BlueTuskParameter<int>(42));
        _blueTuskTypedCommand.Parameters.Add(new BlueTuskParameter<string>("BlueTusk 🐘"));
        _blueTuskTypedCommand.Parameters.Add(
            new BlueTuskParameter<Guid>(BenchmarkGuid));
        _blueTuskTypedCommand.Parameters.Add(new BlueTuskParameter<decimal>(12345.67m));
        _blueTuskTypedCommand.Parameters.Add(
            new BlueTuskParameter<DateTime>(new DateTime(2026, 8, 23, 12, 34, 56, DateTimeKind.Utc)));
        _blueTuskTypedCommand.Parameters.Add(new BlueTuskParameter<int[]>(TypedIntegers));
        _blueTuskTypedCommand.Parameters.Add(
            new BlueTuskParameter<string>("{\"enabled\":true,\"count\":42}"));
        await _blueTuskTypedCommand.PrepareAsync();

        _npgsqlTypedCommand = new NpgsqlCommand(
            _blueTuskTypedCommand.CommandText,
            _npgsqlConnection);
        _npgsqlTypedCommand.Parameters.AddWithValue(42);
        _npgsqlTypedCommand.Parameters.AddWithValue("BlueTusk 🐘");
        _npgsqlTypedCommand.Parameters.AddWithValue(
            BenchmarkGuid);
        _npgsqlTypedCommand.Parameters.AddWithValue(12345.67m);
        _npgsqlTypedCommand.Parameters.AddWithValue(
            new DateTime(2026, 8, 23, 12, 34, 56, DateTimeKind.Utc));
        _npgsqlTypedCommand.Parameters.AddWithValue(TypedIntegers);
        _npgsqlTypedCommand.Parameters.AddWithValue("{\"enabled\":true,\"count\":42}");
        await _npgsqlTypedCommand.PrepareAsync();

        _blueTuskNotificationConnection = await _blueTuskDataSource.OpenConnectionAsync();
        await _blueTuskNotificationConnection.ListenAsync(_blueTuskNotificationChannel);
        _blueTuskNotificationEnumerator =
            _blueTuskNotificationConnection.Notifications.GetAsyncEnumerator();
        _blueTuskNotifyCommand = new BlueTuskCommand(
            $"SELECT pg_notify('{_blueTuskNotificationChannel}', 'ready')",
            _blueTuskConnection);

        _npgsqlNotificationConnection = await _npgsqlDataSource.OpenConnectionAsync();
        _npgsqlNotificationConnection.Notification += OnNpgsqlNotification;
        await using (var listen = new NpgsqlCommand(
                         $"LISTEN \"{_npgsqlNotificationChannel}\"",
                         _npgsqlNotificationConnection))
        {
            _ = await listen.ExecuteNonQueryAsync();
        }

        _npgsqlNotifyCommand = new NpgsqlCommand(
            $"SELECT pg_notify('{_npgsqlNotificationChannel}', 'ready')",
            _npgsqlConnection);

        _blueTuskLargeObjectId = await _blueTuskConnection.CreateLargeObjectAsync();
        await using (var stream = await _blueTuskConnection.OpenLargeObjectAsync(
                         _blueTuskLargeObjectId,
                         FileAccess.Write))
        {
            await stream.WriteAsync(_largeObjectPayload);
        }

        _npgsqlLargeObjectManager = new NpgsqlLargeObjectManager(_npgsqlConnection);
        await using (var transaction = await _npgsqlConnection.BeginTransactionAsync())
        {
            _npgsqlLargeObjectId = await _npgsqlLargeObjectManager.CreateAsync(
                preferredOid: 0,
                CancellationToken.None);
            await using (var stream = await _npgsqlLargeObjectManager.OpenReadWriteAsync(
                             _npgsqlLargeObjectId))
            {
                await stream.WriteAsync(_largeObjectPayload);
            }

            await transaction.CommitAsync();
        }

        _blueTuskEfOptions = new DbContextOptionsBuilder<ProviderBenchmarkContext>()
            .UseBlueTusk(_blueTuskDataSource)
            .Options;
        _npgsqlEfOptions = new DbContextOptionsBuilder<ProviderBenchmarkContext>()
            .UseNpgsql(_npgsqlDataSource)
            .Options;
        _blueTuskCompiledQuery = CreateCompiledQuery();
        _npgsqlCompiledQuery = CreateCompiledQuery();

        _ = await BlueTuskPreparedTypedRowRoundTripAsync();
        _ = await NpgsqlPreparedTypedRowRoundTripAsync();
        _ = await BlueTuskEfCompiledQueryAsync();
        _ = await NpgsqlEfCompiledQueryAsync();
    }

    private async Task CleanupExtendedAsync()
    {
        if (_blueTuskNotificationConnection is not null)
        {
            await _blueTuskNotificationConnection.UnlistenAllAsync();
            await _blueTuskNotificationEnumerator.DisposeAsync();
        }

        if (_npgsqlNotificationConnection is not null)
        {
            _npgsqlNotificationConnection.Notification -= OnNpgsqlNotification;
        }

        if (_blueTuskLargeObjectId != 0)
        {
            await _blueTuskConnection.DeleteLargeObjectAsync(_blueTuskLargeObjectId);
        }

        if (_npgsqlLargeObjectId != 0)
        {
            await using var transaction = await _npgsqlConnection.BeginTransactionAsync();
            await _npgsqlLargeObjectManager.UnlinkAsync(_npgsqlLargeObjectId);
            await transaction.CommitAsync();
        }

        await ExecuteBlueTuskNonQueryAsync($"DROP TABLE IF EXISTS \"{_bulkTableName}\"");
        await ExecuteBlueTuskNonQueryAsync($"DROP TABLE IF EXISTS \"{EfTableName}\"");
        await _blueTuskTypedCommand.DisposeAsync();
        await _npgsqlTypedCommand.DisposeAsync();
        await _blueTuskNotifyCommand.DisposeAsync();
        await _npgsqlNotifyCommand.DisposeAsync();
        await _blueTuskNotificationConnection!.DisposeAsync();
        await _npgsqlNotificationConnection!.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("BeginRollbackTransaction")]
    public async Task<int> BlueTuskBeginRollbackTransactionAsync()
    {
        await using var transaction = await _blueTuskConnection.BeginTransactionAsync();
        await transaction.RollbackAsync();
        return 1;
    }

    [Benchmark]
    [BenchmarkCategory("BeginRollbackTransaction")]
    public async Task<int> NpgsqlBeginRollbackTransactionAsync()
    {
        await using var transaction = await _npgsqlConnection.BeginTransactionAsync();
        await transaction.RollbackAsync();
        return 1;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Batch16ParameterizedScalars")]
    public async Task<int> BlueTuskBatch16ParameterizedScalarsAsync()
    {
        await using var batch = _blueTuskConnection.CreateBatch();
        for (var index = 0; index < BatchCommandCount; index++)
        {
            var command = batch.BatchCommands.Add("SELECT $1::int4 + 1");
            command.Parameters.Add(new BlueTuskParameter<int>(index));
        }

        await using var reader = await batch.ExecuteReaderAsync();
        return await SumBatchAsync(reader);
    }

    [Benchmark]
    [BenchmarkCategory("Batch16ParameterizedScalars")]
    public async Task<int> NpgsqlBatch16ParameterizedScalarsAsync()
    {
        await using var batch = _npgsqlConnection.CreateBatch();
        for (var index = 0; index < BatchCommandCount; index++)
        {
            var command = new NpgsqlBatchCommand("SELECT $1::int4 + 1");
            command.Parameters.Add(new NpgsqlParameter { Value = index });
            batch.BatchCommands.Add(command);
        }

        await using var reader = await batch.ExecuteReaderAsync();
        return await SumBatchAsync(reader);
    }

    internal async Task ProfileBatchPhasesAsync(int iterations)
    {
        await ProfileBlueTuskBatchPhasesAsync(iterations);
        await ProfileNpgsqlBatchPhasesAsync(iterations);
    }

    private async Task ProfileBlueTuskBatchPhasesAsync(int iterations)
    {
        long buildTicks = 0;
        long executeTicks = 0;
        long consumeTicks = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await using var batch = _blueTuskConnection.CreateBatch();
            for (var index = 0; index < BatchCommandCount; index++)
            {
                var command = batch.BatchCommands.Add("SELECT $1::int4 + 1");
                command.Parameters.Add(new BlueTuskParameter<int>(index));
            }

            var built = System.Diagnostics.Stopwatch.GetTimestamp();
            await using var reader = await batch.ExecuteReaderAsync();
            var executed = System.Diagnostics.Stopwatch.GetTimestamp();
            _ = await SumBatchAsync(reader);
            var consumed = System.Diagnostics.Stopwatch.GetTimestamp();
            buildTicks += built - started;
            executeTicks += executed - built;
            consumeTicks += consumed - executed;
        }

        PrintBatchPhases("bluetusk", iterations, buildTicks, executeTicks, consumeTicks);
    }

    private async Task ProfileNpgsqlBatchPhasesAsync(int iterations)
    {
        long buildTicks = 0;
        long executeTicks = 0;
        long consumeTicks = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await using var batch = _npgsqlConnection.CreateBatch();
            for (var index = 0; index < BatchCommandCount; index++)
            {
                var command = new NpgsqlBatchCommand("SELECT $1::int4 + 1");
                command.Parameters.Add(new NpgsqlParameter { Value = index });
                batch.BatchCommands.Add(command);
            }

            var built = System.Diagnostics.Stopwatch.GetTimestamp();
            await using var reader = await batch.ExecuteReaderAsync();
            var executed = System.Diagnostics.Stopwatch.GetTimestamp();
            _ = await SumBatchAsync(reader);
            var consumed = System.Diagnostics.Stopwatch.GetTimestamp();
            buildTicks += built - started;
            executeTicks += executed - built;
            consumeTicks += consumed - executed;
        }

        PrintBatchPhases("npgsql", iterations, buildTicks, executeTicks, consumeTicks);
    }

    private static void PrintBatchPhases(
        string provider,
        int iterations,
        long buildTicks,
        long executeTicks,
        long consumeTicks)
    {
        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
        Console.WriteLine(
            $"provider={provider}; iterations={iterations}; " +
            $"build-us={buildTicks * 1_000_000d / frequency / iterations:F3}; " +
            $"execute-us={executeTicks * 1_000_000d / frequency / iterations:F3}; " +
            $"consume-us={consumeTicks * 1_000_000d / frequency / iterations:F3}");
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("BinaryCopyImport1000Rows")]
    public async Task<long> BlueTuskBinaryCopyImport1000RowsAsync()
    {
        await using var transaction = await _blueTuskConnection.BeginTransactionAsync();
        long imported;
        await using (var importer = await _blueTuskConnection.BeginBinaryImportAsync(
                         $"COPY \"{_bulkTableName}\" (id, name, active, token) " +
                         "FROM STDIN WITH (FORMAT BINARY)"))
        {
            for (var index = 0; index < CopyRowCount; index++)
            {
                await importer.StartRowAsync();
                await importer.WriteAsync(index);
                await importer.WriteAsync("benchmark-row");
                await importer.WriteAsync((index & 1) == 0);
                await importer.WriteAsync(BenchmarkGuid);
            }

            imported = await importer.CompleteAsync();
        }

        await transaction.RollbackAsync();
        return imported;
    }

    [Benchmark]
    [BenchmarkCategory("BinaryCopyImport1000Rows")]
    public async Task<long> NpgsqlBinaryCopyImport1000RowsAsync()
    {
        await using var transaction = await _npgsqlConnection.BeginTransactionAsync();
        ulong imported;
        await using (var importer = await _npgsqlConnection.BeginBinaryImportAsync(
                         $"COPY \"{_bulkTableName}\" (id, name, active, token) " +
                         "FROM STDIN WITH (FORMAT BINARY)"))
        {
            for (var index = 0; index < CopyRowCount; index++)
            {
                await importer.StartRowAsync();
                await importer.WriteAsync(index);
                await importer.WriteAsync("benchmark-row");
                await importer.WriteAsync((index & 1) == 0);
                await importer.WriteAsync(BenchmarkGuid);
            }

            imported = await importer.CompleteAsync();
        }

        await transaction.RollbackAsync();
        return checked((long)imported);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("BinaryCopyExport1000Rows")]
    public async Task<long> BlueTuskBinaryCopyExport1000RowsAsync()
    {
        await using var exporter = await _blueTuskConnection.BeginBinaryExportAsync(
            CopyExportSql());
        long sum = 0;
        while (await exporter.StartRowAsync() != -1)
        {
            sum += await exporter.ReadAsync<int>();
            _ = await exporter.ReadAsync<string>();
            _ = await exporter.ReadAsync<bool>();
            _ = await exporter.ReadAsync<Guid>();
        }

        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("BinaryCopyExport1000Rows")]
    public async Task<long> NpgsqlBinaryCopyExport1000RowsAsync()
    {
        await using var exporter = await _npgsqlConnection.BeginBinaryExportAsync(CopyExportSql());
        long sum = 0;
        while (await exporter.StartRowAsync() != -1)
        {
            sum += await exporter.ReadAsync<int>();
            _ = await exporter.ReadAsync<string>();
            _ = await exporter.ReadAsync<bool>();
            _ = await exporter.ReadAsync<Guid>();
        }

        return sum;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("PreparedTypedRowRoundTrip")]
    public Task<int> BlueTuskPreparedTypedRowRoundTripAsync() =>
        ReadTypedRowAsync(_blueTuskTypedCommand);

    [Benchmark]
    [BenchmarkCategory("PreparedTypedRowRoundTrip")]
    public Task<int> NpgsqlPreparedTypedRowRoundTripAsync() =>
        ReadTypedRowAsync(_npgsqlTypedCommand);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("NotificationDelivery")]
    public async Task<int> BlueTuskNotificationDeliveryAsync()
    {
        var pending = _blueTuskNotificationEnumerator.MoveNextAsync().AsTask();
        _ = await _blueTuskNotifyCommand.ExecuteNonQueryAsync();
        if (!await pending)
        {
            throw new EndOfStreamException("BlueTusk notification stream ended unexpectedly.");
        }

        return _blueTuskNotificationEnumerator.Current.Payload.Length;
    }

    [Benchmark]
    [BenchmarkCategory("NotificationDelivery")]
    public async Task<int> NpgsqlNotificationDeliveryAsync()
    {
        var completion = new TaskCompletionSource<NpgsqlNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _npgsqlNotificationCompletion, completion);
        var pending = _npgsqlNotificationConnection.WaitAsync();
        _ = await _npgsqlNotifyCommand.ExecuteNonQueryAsync();
        await pending;
        var notification = await completion.Task;
        Volatile.Write(ref _npgsqlNotificationCompletion, null);
        return notification.Payload.Length;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("LargeObjectReadOneMegabyte")]
    public async Task<long> BlueTuskLargeObjectReadOneMegabyteAsync()
    {
        await using var transaction = await _blueTuskConnection.BeginTransactionAsync();
        long result;
        await using (var stream = await _blueTuskConnection.OpenLargeObjectAsync(
                         _blueTuskLargeObjectId,
                         FileAccess.Read))
        {
            result = await DrainAsync(stream, _blueTuskLargeObjectBuffer);
        }

        await transaction.RollbackAsync();
        return result;
    }

    [Benchmark]
    [BenchmarkCategory("LargeObjectReadOneMegabyte")]
    public async Task<long> NpgsqlLargeObjectReadOneMegabyteAsync()
    {
        await using var transaction = await _npgsqlConnection.BeginTransactionAsync();
        long result;
        await using (var stream = await _npgsqlLargeObjectManager.OpenReadAsync(
                         _npgsqlLargeObjectId))
        {
            result = await DrainAsync(stream, _npgsqlLargeObjectBuffer);
        }

        await transaction.RollbackAsync();
        return result;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("EfCompiledQuery")]
    public Task<int> BlueTuskEfCompiledQueryAsync() =>
        ExecuteCompiledQueryAsync(_blueTuskEfOptions, _blueTuskCompiledQuery);

    [Benchmark]
    [BenchmarkCategory("EfCompiledQuery")]
    public Task<int> NpgsqlEfCompiledQueryAsync() =>
        ExecuteCompiledQueryAsync(_npgsqlEfOptions, _npgsqlCompiledQuery);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("EfMaterialize100Rows")]
    public Task<int> BlueTuskEfMaterialize100RowsAsync() =>
        MaterializeEfRowsAsync(_blueTuskEfOptions);

    [Benchmark]
    [BenchmarkCategory("EfMaterialize100Rows")]
    public Task<int> NpgsqlEfMaterialize100RowsAsync() =>
        MaterializeEfRowsAsync(_npgsqlEfOptions);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("EfInsertOne")]
    public Task<int> BlueTuskEfInsertOneAsync() => InsertEfRowAsync(_blueTuskEfOptions);

    [Benchmark]
    [BenchmarkCategory("EfInsertOne")]
    public Task<int> NpgsqlEfInsertOneAsync() => InsertEfRowAsync(_npgsqlEfOptions);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("EfUpdateOne")]
    public Task<int> BlueTuskEfUpdateOneAsync() => UpdateEfRowAsync(_blueTuskEfOptions);

    [Benchmark]
    [BenchmarkCategory("EfUpdateOne")]
    public Task<int> NpgsqlEfUpdateOneAsync() => UpdateEfRowAsync(_npgsqlEfOptions);

    private static async Task<int> SumBatchAsync(System.Data.Common.DbDataReader reader)
    {
        var sum = 0;
        for (var index = 0; index < BatchCommandCount; index++)
        {
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException($"Batch result {index} did not contain a row.");
            }

            sum += reader.GetInt32(0);
            if (index + 1 < BatchCommandCount && !await reader.NextResultAsync())
            {
                throw new InvalidOperationException($"Batch result {index + 1} was missing.");
            }
        }

        return sum;
    }

    private static async Task<int> ReadTypedRowAsync(System.Data.Common.DbCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The typed-row query returned no data.");
        }

        var integers = reader.GetFieldValue<int[]>(5);
        return reader.GetInt32(0) +
            reader.GetString(1).Length +
            reader.GetGuid(2).ToByteArray()[0] +
            decimal.ToInt32(reader.GetDecimal(3)) +
            reader.GetDateTime(4).Day +
            integers.Sum() +
            reader.GetString(6).Length;
    }

    private async Task ExecuteBlueTuskNonQueryAsync(string sql)
    {
        await using var command = new BlueTuskCommand(sql, _blueTuskConnection);
        _ = await command.ExecuteNonQueryAsync();
    }

    private void OnNpgsqlNotification(object sender, NpgsqlNotificationEventArgs args) =>
        Volatile.Read(ref _npgsqlNotificationCompletion)?.TrySetResult(args);

    private static string CopyExportSql() =>
        "COPY (SELECT value::int4, 'benchmark-row'::text, " +
        "(value & 1) = 0, '00112233-4455-6677-8899-aabbccddeeff'::uuid " +
        "FROM generate_series(0, 999) AS value) " +
        "TO STDOUT WITH (FORMAT BINARY)";

    private static Func<ProviderBenchmarkContext, int, IAsyncEnumerable<int>> CreateCompiledQuery() =>
        EF.CompileAsyncQuery(
            (ProviderBenchmarkContext context, int minimumId) => context.Orders
                .AsNoTracking()
                .Where(order => order.Id >= minimumId)
                .OrderBy(order => order.Id)
                .Select(order => order.Id)
                .Take(1));

    private static ProviderBenchmarkContext CreateEfContext(
        DbContextOptions<ProviderBenchmarkContext> options) =>
        new(options, EfTableName);

    private static async Task<int> ExecuteCompiledQueryAsync(
        DbContextOptions<ProviderBenchmarkContext> options,
        Func<ProviderBenchmarkContext, int, IAsyncEnumerable<int>> query)
    {
        await using var context = CreateEfContext(options);
        var result = 0;
        await foreach (var id in query(context, 450))
        {
            result = id;
        }

        return result;
    }

    private static async Task<int> MaterializeEfRowsAsync(
        DbContextOptions<ProviderBenchmarkContext> options)
    {
        await using var context = CreateEfContext(options);
        var rows = await context.Orders
            .AsNoTracking()
            .Where(order => order.Id >= 450 && order.Id < 550)
            .OrderBy(order => order.Id)
            .ToListAsync();
        return rows.Count;
    }

    private async Task<int> InsertEfRowAsync(DbContextOptions<ProviderBenchmarkContext> options)
    {
        await using var context = CreateEfContext(options);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var sequence = Interlocked.Increment(ref _efWriteSequence);
        context.Orders.Add(
            new ProviderBenchmarkOrder
            {
                Customer = $"insert-{sequence.ToString(CultureInfo.InvariantCulture)}",
                Total = 42.50m,
                UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        var affected = await context.SaveChangesAsync();
        await transaction.RollbackAsync();
        return affected;
    }

    private static async Task<int> UpdateEfRowAsync(
        DbContextOptions<ProviderBenchmarkContext> options)
    {
        await using var context = CreateEfContext(options);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var order = await context.Orders.SingleAsync(candidate => candidate.Id == 1);
        order.Customer = "updated";
        var affected = await context.SaveChangesAsync();
        await transaction.RollbackAsync();
        return affected;
    }

    private sealed class ProviderBenchmarkContext(
        DbContextOptions<ProviderBenchmarkContext> options,
        string tableName) : DbContext(options)
    {
        public DbSet<ProviderBenchmarkOrder> Orders => Set<ProviderBenchmarkOrder>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var order = modelBuilder.Entity<ProviderBenchmarkOrder>();
            ConfigureOrder(order, tableName);
        }

        private static void ConfigureOrder(
            EntityTypeBuilder<ProviderBenchmarkOrder> order,
            string tableName)
        {
            order.ToTable(tableName);
            order.HasKey(entity => entity.Id);
            order.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedOnAdd();
            order.Property(entity => entity.Customer).HasColumnName("customer");
            order.Property(entity => entity.Total).HasColumnName("total").HasPrecision(12, 2);
            order.Property(entity => entity.UpdatedAt).HasColumnName("updated_at");
        }
    }

    private sealed class ProviderBenchmarkOrder
    {
        public int Id { get; set; }

        public string Customer { get; set; } = string.Empty;

        public decimal Total { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}

#pragma warning restore CS0618
