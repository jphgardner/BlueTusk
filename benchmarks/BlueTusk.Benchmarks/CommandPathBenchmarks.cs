using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Protocol;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class CommandPathBenchmarks : IDisposable
{
    private BlueTuskConnectionPool _pool = null!;
    private BlueTuskConnection _connection = null!;
    private BlueTuskCommand _int32Command = null!;
    private BlueTuskCommand _textCommand = null!;
    private BlueTuskCommand _readerCommand = null!;
    private BlueTuskCommand _asyncInt32Command = null!;

    [GlobalSetup]
    public void Setup()
    {
        var settings = new BlueTuskConnectionStringBuilder
        {
            Pooling = true,
            MaximumPoolSize = 1,
            ConnectionIdleLifetime = TimeSpan.Zero,
            ConnectionLifetime = TimeSpan.Zero,
        };
        _pool = new BlueTuskConnectionPool(
            settings,
            _ => ValueTask.FromResult<IBlueTuskPhysicalSession>(new BenchmarkPhysicalSession()),
            synchronousSessionFactory: static () => new BenchmarkPhysicalSession());
        _connection = new BlueTuskConnection(
            settings.ConnectionString,
            _pool,
            new BlueTuskTypeMetadataCache());
        _connection.Open();

        _int32Command = CreateCommand(
            "SELECT @value::int4",
            new BlueTuskParameter<int>(42) { ParameterName = "value" });
        _textCommand = CreateCommand(
            "SELECT @value::text",
            new BlueTuskParameter<string>(new string('x', 128)) { ParameterName = "value" });
        _readerCommand = new BlueTuskCommand("SELECT value FROM benchmark_rows", _connection)
        {
            CommandTimeout = 0,
        };
        _asyncInt32Command = CreateCommand(
            "SELECT @value::int4",
            new BlueTuskParameter<int>(42) { ParameterName = "value" });
    }

    [Benchmark(Baseline = true)]
    public object? ExecuteInt32ParameterAndScalar() => _int32Command.ExecuteScalar();

    [Benchmark]
    public object? ExecuteTextParameterAndScalar() => _textCommand.ExecuteScalar();

    [Benchmark]
    public int ExecuteReaderAndReadOneHundredInt32Values()
    {
        using var reader = _readerCommand.ExecuteReader();
        var sum = 0;
        while (reader.Read())
        {
            sum += reader.GetInt32(0);
        }

        return sum;
    }

    [Benchmark]
    public Task<object?> ExecuteInt32ParameterAndScalarAsync() =>
        _asyncInt32Command.ExecuteScalarAsync(CancellationToken.None);

    [GlobalCleanup]
    public void Dispose()
    {
        _int32Command?.Dispose();
        _textCommand?.Dispose();
        _readerCommand?.Dispose();
        _asyncInt32Command?.Dispose();
        _connection?.Dispose();
        _pool?.Dispose();
        GC.SuppressFinalize(this);
    }

    private BlueTuskCommand CreateCommand(string sql, BlueTuskParameter parameter)
    {
        var command = new BlueTuskCommand(sql, _connection);
        command.Parameters.Add(parameter);
        return command;
    }

    private sealed class BenchmarkPhysicalSession : IBlueTuskPhysicalSession
    {
        private static readonly BlueTuskQueryResult CatalogueResult = CreateCatalogueResult();
        private static readonly BlueTuskQueryResult Int32Result = CreateInt32Result(42);
        private static readonly BlueTuskQueryResult TextResult = CreateTextResult(new string('x', 128));
        private static readonly BlueTuskQueryResult RowsResult = CreateRowsResult();

        public bool IsOpen { get; private set; } = true;

        public BlueTuskHostEndpoint Endpoint { get; } = new("localhost", 5432);

        public bool? IsPrimary => true;

        public bool? IsReadOnly => false;

        public IReadOnlyDictionary<string, string> Parameters { get; } =
            new Dictionary<string, string> { ["server_version"] = "18.0" };

        public BlueTuskTransactionStatus TransactionStatus => BlueTuskTransactionStatus.Idle;

        public void RefreshHostState()
        {
        }

        public ValueTask RefreshHostStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public BlueTuskQueryResult ExecuteSimpleQuery(string sql) =>
            sql.StartsWith("SELECT t.oid::text", StringComparison.Ordinal)
                ? CatalogueResult
                : RowsResult;

        public ValueTask<BlueTuskQueryResult> ExecuteSimpleQueryAsync(
            string sql,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ExecuteSimpleQuery(sql));
        }

        public BlueTuskQueryResult ExecuteExtendedQuery(
            string sql,
            IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
            bool useBinaryResults) =>
            sql.Contains("::text", StringComparison.Ordinal) ? TextResult : Int32Result;

        public ValueTask<BlueTuskQueryResult> ExecuteExtendedQueryAsync(
            string sql,
            IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
            bool useBinaryResults,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ExecuteExtendedQuery(sql, parameters, useBinaryResults));
        }

        public ValueTask PrepareStatementAsync(
            string statementName,
            string sql,
            IReadOnlyList<uint> parameterTypeOids,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<BlueTuskQueryResult> ExecutePreparedStatementAsync(
            string statementName,
            IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
            bool useBinaryResults,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask ClosePreparedStatementAsync(
            string statementName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<BlueTuskQueryResult> ExecuteBatchAsync(
            IReadOnlyList<BlueTuskBatchQuery> queries,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<BlueTuskQueryResult> ExecutePreparedBatchAsync(
            IReadOnlyList<BlueTuskPreparedBatchQuery> queries,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<BlueTuskCopyResult> CopyInAsync(
            string sql,
            Stream source,
            Action<BlueTuskCopyResponse>? copyStarted,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<BlueTuskCopyResult> CopyOutAsync(
            string sql,
            Stream destination,
            Action<BlueTuskCopyResponse>? copyStarted,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<BlueTuskNotificationResponse> WaitForNotificationAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Cancel()
        {
        }

        public ValueTask CancelAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void Dispose() => IsOpen = false;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private static BlueTuskQueryResult CreateCatalogueResult() => new(
        [
            new BlueTuskResultSet(CreateFields(12), [], "SELECT 0"),
            new BlueTuskResultSet(CreateFields(2), [], "SELECT 0"),
            new BlueTuskResultSet(CreateFields(4), [], "SELECT 0"),
            new BlueTuskResultSet(
                CreateFields(2),
                [new BlueTuskDataRow([Text("en_GB.UTF-8"), Text("2")])],
                "SELECT 1"),
        ]);

        private static BlueTuskQueryResult CreateInt32Result(int value)
        {
            var bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return new BlueTuskQueryResult(
            [
                new BlueTuskResultSet(
                    [new BlueTuskFieldDescription("value", 0, 0, 23, sizeof(int), -1, 1)],
                    [new BlueTuskDataRow([bytes])],
                    "SELECT 1"),
            ]);
        }

        private static BlueTuskQueryResult CreateTextResult(string value) => new(
        [
            new BlueTuskResultSet(
                [new BlueTuskFieldDescription("value", 0, 0, 25, -1, -1, 0)],
                [new BlueTuskDataRow([Text(value)])],
                "SELECT 1"),
        ]);

        private static BlueTuskQueryResult CreateRowsResult() => new(
        [
            new BlueTuskResultSet(
                [new BlueTuskFieldDescription("value", 0, 0, 23, sizeof(int), -1, 1)],
                Enumerable.Range(0, 100)
                    .Select(static value =>
                    {
                        var bytes = new byte[sizeof(int)];
                        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
                        return new BlueTuskDataRow([bytes]);
                    })
                    .ToArray(),
                "SELECT 100"),
        ]);

        private static BlueTuskFieldDescription[] CreateFields(int count) =>
            Enumerable.Range(0, count)
                .Select(static index =>
                    new BlueTuskFieldDescription($"field_{index}", 0, 0, 25, -1, -1, 0))
                .ToArray();

        private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
    }
}
