using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Benchmarks;

/// <summary>Captures one diagnostic candidate process against a dedicated PostgreSQL publication.</summary>
internal static class StreamsDeliveryCapture
{
    internal const string ConnectionVariable = "BLUETUSK_BENCHMARK_CONNECTION_STRING";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        WriteIndented = true,
    };

    private sealed record Options(
        string SourceCommit,
        string PostgreSqlImage,
        string Scenario,
        int Trial,
        int ChangesPerTransaction,
        int WarmupTransactions,
        int MeasurementTransactions,
        string Publication,
        string Slot,
        string ReadyPath,
        string MeasurementReadyPath,
        string OutputPath);

    public static async Task RunAsync(string optionsPath)
    {
        var options = JsonSerializer.Deserialize<Options>(await File.ReadAllBytesAsync(optionsPath), JsonOptions)
            ?? throw new InvalidDataException("Streams capture options are empty.");
        var liveDelivery = options.Scenario == "commit-to-delivery";
        var snapshot = options.Scenario == "snapshot";
        if (options.Scenario is not ("commit-to-delivery" or "catch-up" or "snapshot") ||
            options.Trial is < 0 or >= 50 || options.ChangesPerTransaction is not (1 or 1_000) ||
            (liveDelivery ? options.WarmupTransactions is < 1 or > 10_000 : options.WarmupTransactions != 0) ||
            options.MeasurementTransactions is < 1 or > 16_000 ||
            !Regex.IsMatch(options.SourceCommit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(options.PostgreSqlImage, "^postgres:[^@\\s]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("Streams capture requires bounded transactions, a valid workload, a pinned PostgreSQL image, and an exact source commit.");
        }

        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" :
            throw new PlatformNotSupportedException("Streams capture targets Windows or Linux.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("Streams capture targets x64.");
        }
        var implementation = typeof(PgOutputChangeStream).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        if (!implementation.EndsWith($"+{options.SourceCommit}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Streams assembly was not built from the requested source commit.");
        }

        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{ConnectionVariable} must target a dedicated benchmark database.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cancellationToken = timeout.Token;
        var transactionTicks = snapshot ? Array.Empty<long>() : new long[options.MeasurementTransactions];
        var samples = new long[checked(options.MeasurementTransactions * options.ChangesPerTransaction)];
        using var process = Process.GetCurrentProcess();
        var cpuBefore = TimeSpan.Zero;
        int[] gcBefore = [];
        long allocatedBefore = 0;
        long started = 0;
        if (!liveDelivery)
        {
            StartMeasurement();
        }

        await using var clockConnection = new BlueTuskConnection(connectionString);
        ClockOffset? clockBefore = null;
        var counters = snapshot ? await CaptureSnapshotAsync() : await CaptureTransactionsAsync();

        // Live delivery brackets clock offset on either side of the window and retains the
        // original wall-clock observations. Backlog latency uses the local monotonic clock.
        var clockAfter = liveDelivery ? await SampleClockOffsetAsync(clockConnection, cancellationToken) : null;
        var upperOffset = clockBefore is null ? 0 : Math.Max(clockBefore.LatestOffsetTicks, clockAfter!.LatestOffsetTicks);
        for (var index = 0; index < transactionTicks.Length; index++)
        {
            var latency = transactionTicks[index] + upperOffset;
            if (latency <= 0)
            {
                throw new InvalidDataException("The Streams delivery latency must be positive.");
            }

            samples.AsSpan(index * options.ChangesPerTransaction, options.ChangesPerTransaction).Fill(latency);
        }

        var measurement = new ProviderRequestCapture.Window(
            counters.Elapsed, samples.Length, counters.Allocated, counters.Cpu, counters.PeakRss, counters.Gc, [new(samples, samples.Length)]);
        var report = new PerformanceEvidenceGenerator.TrialFile(
            1, "bluetusk-performance-trial", "Streams",
            $"{os}|Streams|changes={options.ChangesPerTransaction}|scenario={options.Scenario}",
            "candidate", options.Trial, options.SourceCommit, Diagnostic: true, Synthetic: false, $"BlueTusk.Streams {implementation}",
            JsonSerializer.SerializeToElement(new
            {
                os,
                architecture = "x64",
                runtime = RuntimeInformation.FrameworkDescription,
                postgreSqlImage = options.PostgreSqlImage,
                latency = options.Scenario switch
                {
                    "snapshot" => "Time from starting the consumer to delivering each row of the consistent snapshot",
                    "catch-up" => "Time from starting the consumer to delivering each row of an already committed backlog",
                    _ => "WAL commit to delivery UTC ticks plus the largest sampled source-clock offset bound",
                },
                clockBefore,
                clockAfter,
                sourceClockUpperOffsetTicks = liveDelivery ? upperOffset : (long?)null,
                changesPerTransaction = options.ChangesPerTransaction,
                rawWallTicksByTransaction = liveDelivery ? transactionTicks : null,
                clockOffsetAssumption = liveDelivery ? "Source-to-client offset remains within the endpoint bounds during measurement" : null,
                sourceWriter = "separate process",
                allocation = "process-wide managed allocation counter delta",
                gc = "managed collection count delta by generation",
                peakRss = "candidate process lifetime through measurement end",
            }, JsonOptions),
            TimeSpan.TicksPerSecond, measurement);
        await using var output = new FileStream(options.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, report, JsonOptions, cancellationToken);

        void StartMeasurement()
        {
            cpuBefore = process.TotalProcessorTime;
            gcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            started = Stopwatch.GetTimestamp();
        }

        MeasurementCounters StopMeasurement()
        {
            var elapsed = Stopwatch.GetElapsedTime(started).Ticks;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var gc = new[] { GC.CollectionCount(0) - gcBefore[0], GC.CollectionCount(1) - gcBefore[1], GC.CollectionCount(2) - gcBefore[2] };
            process.Refresh();
            return new(elapsed, allocated, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds, process.PeakWorkingSet64, gc);
        }

        async Task<MeasurementCounters> CaptureSnapshotAsync()
        {
            await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
            ChangeSourceIdentity source;
            await using (var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString, cancellationToken))
            {
                source = await ReadIdentityAsync(replication);
            }
            var table = new ChangeTable(0, "public", "streams_capture", 'd', [new ChangeColumn(0, "id", 20, -1, IsKey: true)]);
            var snapshotSource = new PostgreSqlConsistentSnapshotSource(dataSource, new()
            {
                Source = source,
                PublicationNames = [options.Publication],
                Tables = [new PostgreSqlSnapshotTable(table, [0])],
            });
            await using var attempt = await snapshotSource.BeginAttemptAsync(null, cancellationToken);
            var rows = 0;
            await foreach (var batch in attempt.ReadSnapshotAsync(cancellationToken))
            {
                var deliveredAfterStart = Stopwatch.GetElapsedTime(started).Ticks;
                if (batch.Epoch != attempt.Epoch || batch.Table is not { Schema: "public", Name: "streams_capture" } || deliveredAfterStart <= 0)
                {
                    throw new InvalidDataException("Replication delivered an unexpected snapshot batch.");
                }
                foreach (var snapshotRow in batch.Rows)
                {
                    var id = snapshotRow.Row["id"];
                    if (id is not { State: ChangeColumnState.Value, Encoding: ChangeValueEncoding.Binary } || id.Data.Length != sizeof(long))
                    {
                        throw new InvalidDataException("The snapshot row did not contain a valid bigint key.");
                    }
                    var row = BinaryPrimitives.ReadInt64BigEndian(id.Data.Span);
                    if (row < 1 || row > samples.Length || samples[(int)row - 1] != 0)
                    {
                        throw new InvalidDataException("Replication delivered an unexpected or repeated snapshot row.");
                    }
                    samples[(int)row - 1] = deliveredAfterStart;
                    rows++;
                }
            }
            if (rows != samples.Length)
            {
                throw new EndOfStreamException("The consistent snapshot ended before delivering every expected row.");
            }
            return StopMeasurement();
        }

        async Task<MeasurementCounters> CaptureTransactionsAsync()
        {
            await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString, cancellationToken);
            var source = await ReadIdentityAsync(replication);
            if (liveDelivery)
            {
                await clockConnection.OpenAsync(cancellationToken);
                _ = await replication.CreateReplicationSlotAsync(options.Slot, temporary: true, cancellationToken: cancellationToken);
            }
            var envelopes = replication.StartReplicationAsync(options.Slot, options.Publication, cancellationToken)
                .DecodePgOutputAsync(cancellationToken: cancellationToken);
            var stream = new PgOutputChangeStream(envelopes, source);
            await using var transactions = stream.ReadTransactionsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

            BlueTuskLogSequenceNumber? previous = null;
            long nextRow = 1;
            if (liveDelivery)
            {
                Signal(options.ReadyPath);
                for (var index = 0; index < options.WarmupTransactions; index++)
                {
                    _ = await ReadDeliveryAsync();
                }
                clockBefore = await SampleClockOffsetAsync(clockConnection, cancellationToken);
                StartMeasurement();
                Signal(options.MeasurementReadyPath);
            }
            for (var index = 0; index < options.MeasurementTransactions; index++)
            {
                transactionTicks[index] = await ReadDeliveryAsync();
            }
            return StopMeasurement();

            async ValueTask<long> ReadDeliveryAsync()
            {
                if (!await transactions.MoveNextAsync())
                {
                    throw new EndOfStreamException("Replication ended before all expected transactions were delivered.");
                }

                var delivered = liveDelivery ? DateTimeOffset.UtcNow : default;
                var deliveredAfterStart = liveDelivery ? 0 : Stopwatch.GetElapsedTime(started).Ticks;
                await using var delivery = transactions.Current;
                var transaction = delivery.Transaction;
                if (transaction.Changes.Count != options.ChangesPerTransaction ||
                    previous is { } position && transaction.CommitPosition.CompareTo(position) <= 0)
                {
                    throw new InvalidDataException("Replication delivered an unexpected change count or a repeated/out-of-order commit.");
                }

                var expectedNextTransaction = nextRow + options.ChangesPerTransaction;
                await foreach (var change in transaction.Changes.WithCancellation(cancellationToken))
                {
                    if (change is not InsertChange insert || insert.NewRow.Table is not { Schema: "public", Name: "streams_capture" })
                    {
                        throw new InvalidDataException("Replication delivered an unexpected operation or table.");
                    }

                    var id = insert.NewRow["id"];
                    if (id is not { State: ChangeColumnState.Value, Encoding: ChangeValueEncoding.Text } ||
                        !Utf8Parser.TryParse(id.Data.Span, out long row, out var consumed) || consumed != id.Data.Length || row != nextRow)
                    {
                        throw new InvalidDataException("Replication delivered a missing, repeated, or out-of-order row.");
                    }
                    nextRow++;
                }
                if (nextRow != expectedNextTransaction)
                {
                    throw new InvalidDataException("Replication did not enumerate every change in the transaction.");
                }

                previous = transaction.CommitPosition;
                await delivery.AcknowledgeAsync(cancellationToken);
                return liveDelivery ? (delivered - transaction.CommitTimestamp).Ticks : deliveredAfterStart;
            }
        }

        async ValueTask<ChangeSourceIdentity> ReadIdentityAsync(BlueTuskLogicalReplicationConnection replication)
        {
            var identity = await replication.IdentifySystemAsync(cancellationToken);
            return new ChangeSourceIdentity(identity.SystemIdentifier, identity.DatabaseName!, options.Slot, options.Publication);
        }
    }

    private readonly record struct MeasurementCounters(long Elapsed, long Allocated, double Cpu, long PeakRss, int[] Gc);

    private sealed record ClockOffset(long EarliestOffsetTicks, long LatestOffsetTicks);

    private static async Task<ClockOffset> SampleClockOffsetAsync(
        BlueTuskConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (extract(epoch FROM clock_timestamp()) * 10000000)::bigint";
        ClockOffset? narrowest = null;
        for (var sample = 0; sample < 12; sample++)
        {
            var before = DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
            var server = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            var after = DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
            if (after < before)
            {
                throw new InvalidDataException("The client wall clock moved backwards during clock sampling.");
            }

            // The server timestamp must have been sampled between the two client observations.
            var interval = new ClockOffset(server - after, server - before);
            if (narrowest is null ||
                after - before < narrowest.LatestOffsetTicks - narrowest.EarliestOffsetTicks)
            {
                narrowest = interval;
            }
        }

        return narrowest!;
    }

    private static void Signal(string path)
    {
        using var marker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }
}
