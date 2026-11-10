using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using BlueTusk.ContinuousGraph;
using BlueTusk.Data;
using BlueTusk.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlueTusk.Benchmarks;

public partial class ContinuousGraphBenchmarks
{
    private const string CaptureRole = "bluetusk_benchmark_scoped_reader";

    internal static async Task CaptureScopedReadAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        foreach (var top in new[] { 10, 1_000 })
        {
            await using var benchmark = new ContinuousGraphBenchmarks { EdgeCount = 1_000, TopN = top };
            var observer = new ScopedReadObserver();
            await benchmark.SetupAsync(observer);
            observer.CaptureCommand = true;
            var expected = await benchmark.ReadScopedProposalAsync();
            observer.CaptureCommand = false;

            await using var lease = benchmark.CreateCaptureConnection();
            await lease.OpenAsync();
            await AuthorizeCaptureAsync(lease, CancellationToken.None);
            await benchmark.VerifyCaptureRoleAsync(lease);
            await using var prepared = observer.CreateCommand(lease);
            await prepared.PrepareAsync();
            await using (var explain = observer.CreateCommand(lease, "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "))
            {
                var plan = (string)(await explain.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("The affected-key execution plan is missing."));
                await File.WriteAllTextAsync(Path.Combine(directory, $"affected-key-top-{top}.json"), plan);
            }

            var samples = new List<ScopedReadSample>();
            using var counters = observer.Listen();
            var variants = new[] { "scoped-ef", "pooled-sql", "prepared-sql", "pooled-buffered-sql" };
            const int warmup = 32;
            const int iterations = 256;
            for (var iteration = -warmup; iteration < iterations; iteration++)
            {
                // Rotate the starting arm so each receives the same mixture of pool and host conditions.
                for (var offset = 0; offset < variants.Length; offset++)
                {
                    var variant = variants[(iteration + warmup + offset) % variants.Length];
                    observer.Reset();
                    var allocated = GC.GetTotalAllocatedBytes(precise: true);
                    var started = Stopwatch.GetTimestamp();
                    var result = variant switch
                    {
                        "scoped-ef" => await benchmark.ReadScopedProposalAsync(),
                        "pooled-sql" => await benchmark.ReadPooledCaptureAsync(observer, buffered: false),
                        "prepared-sql" => await ReadCaptureRowAsync(prepared, observer),
                        "pooled-buffered-sql" => await benchmark.ReadPooledCaptureAsync(observer, buffered: true),
                        _ => throw new UnreachableException(),
                    };
                    var finished = Stopwatch.GetTimestamp();
                    var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                    if (!GraphPathComparer.Instance.Equals(expected, result))
                    {
                        throw new InvalidOperationException("The authorized affected-key variants returned different rows.");
                    }

                    if (iteration >= 0)
                    {
                        samples.Add(observer.Sample(top, iteration, variant, started, finished, bytes));
                    }
                }
            }

            await File.WriteAllTextAsync(Path.Combine(directory, $"top-{top}.json"),
                JsonSerializer.Serialize(new { top, observer.Sql, samples }));
        }

        process.Refresh();
        await File.WriteAllTextAsync(Path.Combine(directory, "resources.json"), JsonSerializer.Serialize(new
        {
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
            peakRssBytes = process.PeakWorkingSet64,
        }));
    }

    private async Task InstallCapturePolicyAsync()
    {
        await ExecuteAsync($"""
            CREATE ROLE {CaptureRole} NOLOGIN;
            INSERT INTO {PeopleTable} VALUES ({EdgeCount + 2}, 'restricted');
            INSERT INTO {FriendshipsTable} VALUES ({EdgeCount + 3}, 1, {EdgeCount + 2});
            ALTER TABLE {PeopleTable} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {FriendshipsTable} ENABLE ROW LEVEL SECURITY;
            CREATE POLICY scoped_reader ON {PeopleTable} TO {CaptureRole} USING (id <= {EdgeCount + 1});
            CREATE POLICY scoped_reader ON {FriendshipsTable} TO {CaptureRole} USING (to_id <= {EdgeCount + 1});
            GRANT SELECT ON {PeopleTable}, {FriendshipsTable} TO {CaptureRole};
            """);
        _capturePolicyInstalled = true;
    }

    private bool _capturePolicyInstalled;

    private BlueTuskConnection CreateCaptureConnection()
    {
        var connection = _dataSource.CreateConnection();
        connection.AllowPendingPoolResetOnOpen();
        connection.PreferExtendedProtocol();
        connection.PreferBinaryResults();
        return connection;
    }

    private static async Task AuthorizeCaptureAsync(BlueTuskConnection connection, CancellationToken token)
    {
        await using var command = new BlueTuskCommand($"SET ROLE {CaptureRole}", connection);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task VerifyCaptureRoleAsync(BlueTuskConnection connection)
    {
        await using var command = new BlueTuskCommand(
            $"SELECT current_user = '{CaptureRole}' AND NOT EXISTS " +
            $"(SELECT FROM {PeopleTable} WHERE id = {EdgeCount + 2}) AND NOT EXISTS " +
            $"(SELECT FROM {FriendshipsTable} WHERE to_id = {EdgeCount + 2})", connection);
        if (await command.ExecuteScalarAsync() is not true)
        {
            throw new InvalidOperationException("The scoped-read capture must enforce the restricted PostgreSQL role.");
        }
    }

    private async Task<GraphPath> ReadScopedProposalAsync()
    {
        await using var proposal = await _scoped.PrepareTransactionAsync(_update.Transaction);
        if (proposal.Evaluation.MaintenanceTier != ContinuousGraphMaintenanceTier.AuthoritativeDelta)
        {
            throw new InvalidOperationException("The capture did not exercise authoritative scoped maintenance.");
        }

        return proposal.Evaluation.Batch?.Events.Single().Row
            ?? throw new InvalidOperationException("The scoped update did not produce the expected replacement.");
    }

    private async Task<GraphPath> ReadPooledCaptureAsync(ScopedReadObserver observer, bool buffered)
    {
        await using var connection = CreateCaptureConnection();
        if (buffered)
        {
            connection.UseBufferedDataReaders();
        }

        observer.ContextCreated = Stopwatch.GetTimestamp();
        await connection.OpenAsync();
        observer.ConnectionOpened = Stopwatch.GetTimestamp();
        await AuthorizeCaptureAsync(connection, CancellationToken.None);
        observer.Authorized = Stopwatch.GetTimestamp();
        await using var command = observer.CreateCommand(connection);
        return await ReadCaptureRowAsync(command, observer);
    }

    private static async Task<GraphPath> ReadCaptureRowAsync(BlueTuskCommand command, ScopedReadObserver observer)
    {
        observer.CommandStarted = Stopwatch.GetTimestamp();
        await using var reader = await command.ExecuteReaderAsync();
        observer.ReaderOpened = Stopwatch.GetTimestamp();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The authorized affected-key read returned no row.");
        }

        observer.FirstRow = Stopwatch.GetTimestamp();
        var result = new GraphPath
        {
            RelationshipId = reader.GetInt32(reader.GetOrdinal(nameof(GraphPath.RelationshipId))),
            DestinationId = reader.GetInt32(reader.GetOrdinal(nameof(GraphPath.DestinationId))),
            DestinationName = reader.GetString(reader.GetOrdinal(nameof(GraphPath.DestinationName))),
        };
        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("The authorized affected-key read returned a duplicate row.");
        }

        observer.ReaderFinished = Stopwatch.GetTimestamp();
        return result;
    }

    private sealed record ScopedReadSample(
        int Top, int Iteration, string Variant, double TotalUs, long AllocatedBytes,
        double? SetupUs, double? CheckoutUs, double? AuthorizeUs, double? BeforeCommandUs,
        double? ReaderOpenUs, double? FirstRowUs, double? ReadAndMaterializeUs, double? AfterReaderUs,
        long Commands, long PoolResets, long ProtocolMessages);

    private sealed class ScopedReadObserver : DbCommandInterceptor
    {
        private BlueTuskParameter[] _parameters = [];
        private int _timeout;
        private long _commands;
        private long _resets;
        private long _messages;
        public bool CaptureCommand { get; set; }
        public string? Sql { get; private set; }
        public long ContextCreated;
        public long ConnectionOpened;
        public long Authorized;
        public long CommandStarted;
        public long ReaderOpened;
        public long FirstRow;
        public long ReaderFinished;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandStarted = Stopwatch.GetTimestamp();
            if (CaptureCommand)
            {
                if (!command.CommandText.Contains("LIMIT 2", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Expected the compiled two-row affected-key query.");
                }

                Sql = command.CommandText;
                _timeout = command.CommandTimeout;
                _parameters = command.Parameters.Cast<BlueTuskParameter>().Select(Copy).ToArray();
            }

            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            ReaderOpened = Stopwatch.GetTimestamp();
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult DataReaderDisposing(
            DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            ReaderFinished = Stopwatch.GetTimestamp();
            return result;
        }

        public BlueTuskCommand CreateCommand(BlueTuskConnection connection, string prefix = "")
        {
            var sql = Sql ?? throw new InvalidOperationException("The scoped EF command was not captured.");
            var command = new BlueTuskCommand(prefix + sql, connection) { CommandTimeout = _timeout };
            foreach (var parameter in _parameters)
            {
                command.Parameters.Add(Copy(parameter));
            }

            return command;
        }

        public MeterListener Listen()
        {
            var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument == BlueTuskDiagnostics.CommandsExecuted ||
                    instrument == BlueTuskDiagnostics.PoolResets ||
                    instrument == BlueTuskDiagnostics.ProtocolMessageSize)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, count, _, _) =>
            {
                if (instrument == BlueTuskDiagnostics.CommandsExecuted)
                {
                    _commands += count;
                }
                else if (instrument == BlueTuskDiagnostics.PoolResets)
                {
                    _resets += count;
                }
                else
                {
                    _messages++;
                }
            });
            listener.Start();
            return listener;
        }

        public void Reset()
        {
            ContextCreated = ConnectionOpened = Authorized = CommandStarted = ReaderOpened = FirstRow = ReaderFinished = 0;
            _commands = _resets = _messages = 0;
        }

        public ScopedReadSample Sample(int top, int iteration, string variant, long started, long finished, long bytes) =>
            new(top, iteration, variant, Micros(started, finished)!.Value, bytes,
                Micros(started, ContextCreated), Micros(ContextCreated, ConnectionOpened),
                Micros(ConnectionOpened, Authorized), Micros(Authorized, CommandStarted),
                Micros(CommandStarted, ReaderOpened), Micros(CommandStarted, FirstRow),
                Micros(ReaderOpened, ReaderFinished), Micros(ReaderFinished, finished),
                _commands, _resets, _messages);

        private static double? Micros(long start, long end) =>
            start == 0 || end < start ? null : Stopwatch.GetElapsedTime(start, end).TotalMicroseconds;

        private static BlueTuskParameter Copy(BlueTuskParameter parameter) => new(parameter.Value)
        {
            DbType = parameter.DbType,
            ParameterName = parameter.ParameterName,
            PostgreSqlTypeOid = parameter.PostgreSqlTypeOid,
            PostgreSqlTypeName = parameter.PostgreSqlTypeName,
            Size = parameter.Size,
        };
    }
}
