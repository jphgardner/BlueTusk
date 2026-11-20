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
internal static class StreamsCommitDeliveryCapture
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
        if (options.Trial is < 0 or >= 50 || options.ChangesPerTransaction is not (1 or 1_000) ||
            options.WarmupTransactions is < 1 or > 10_000 || options.MeasurementTransactions is < 1 or > 16_000 ||
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
        var wallTicks = new long[options.MeasurementTransactions];
        var samples = new long[checked(options.MeasurementTransactions * options.ChangesPerTransaction)];
        await using var clockConnection = new BlueTuskConnection(connectionString);
        await clockConnection.OpenAsync(cancellationToken);
        await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(connectionString, cancellationToken);
        var identity = await replication.IdentifySystemAsync(cancellationToken);
        _ = await replication.CreateReplicationSlotAsync(options.Slot, temporary: true, cancellationToken: cancellationToken);
        var source = new ChangeSourceIdentity(identity.SystemIdentifier, identity.DatabaseName!, options.Slot, options.Publication);
        var envelopes = replication.StartReplicationAsync(options.Slot, options.Publication, cancellationToken)
            .DecodePgOutputAsync(cancellationToken: cancellationToken);
        var stream = new PgOutputChangeStream(envelopes, source);
        await using var transactions = stream.ReadTransactionsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        BlueTuskLogSequenceNumber? previous = null;
        Signal(options.ReadyPath);

        for (var index = 0; index < options.WarmupTransactions; index++)
        {
            _ = await ReadDeliveryAsync();
        }

        var clockBefore = await SampleClockOffsetAsync(clockConnection, cancellationToken);
        using var process = Process.GetCurrentProcess();
        Signal(options.MeasurementReadyPath);

        var cpuBefore = process.TotalProcessorTime;
        var gcBefore = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < options.MeasurementTransactions; index++)
        {
            wallTicks[index] = await ReadDeliveryAsync();
        }

        var elapsed = Stopwatch.GetElapsedTime(started).Ticks;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var gc = new[] { GC.CollectionCount(0) - gcBefore[0], GC.CollectionCount(1) - gcBefore[1], GC.CollectionCount(2) - gcBefore[2] };
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var peakRss = process.PeakWorkingSet64;

        // Bracket clock offset on either side of the window. Retain the original wall-clock
        // observations alongside upper-bound diagnostic latency, with no dropped samples.
        var clockAfter = await SampleClockOffsetAsync(clockConnection, cancellationToken);
        var upperOffset = Math.Max(clockBefore.LatestOffsetTicks, clockAfter.LatestOffsetTicks);
        for (var index = 0; index < wallTicks.Length; index++)
        {
            var latencyUpperBound = wallTicks[index] + upperOffset;
            if (latencyUpperBound <= 0)
            {
                throw new InvalidDataException("The sampled clock bounds do not explain the WAL and delivery timestamps.");
            }

            samples.AsSpan(index * options.ChangesPerTransaction, options.ChangesPerTransaction).Fill(latencyUpperBound);
        }

        var measurement = new ProviderRequestCapture.Window(
            elapsed, samples.Length, allocated, cpu, peakRss, gc, [new(samples, samples.Length)]);
        var report = new PerformanceEvidenceGenerator.TrialFile(
            1, "bluetusk-performance-trial", "Streams",
            $"{os}|Streams|changes={options.ChangesPerTransaction}|scenario=commit-to-delivery",
            "candidate", options.Trial, options.SourceCommit, Diagnostic: true, Synthetic: false, $"BlueTusk.Streams {implementation}",
            JsonSerializer.SerializeToElement(new
            {
                os,
                architecture = "x64",
                runtime = RuntimeInformation.FrameworkDescription,
                postgreSqlImage = options.PostgreSqlImage,
                latency = "WAL commit to delivery UTC ticks plus the largest sampled source-clock offset bound",
                clockBefore,
                clockAfter,
                sourceClockUpperOffsetTicks = upperOffset,
                changesPerTransaction = options.ChangesPerTransaction,
                rawWallTicksByTransaction = wallTicks,
                clockOffsetAssumption = "Source-to-client offset remains within the endpoint bounds during measurement",
                sourceWriter = "separate process",
                allocation = "process-wide managed allocation counter delta",
                gc = "managed collection count delta by generation",
                peakRss = "candidate process lifetime through measurement end",
            }, JsonOptions),
            TimeSpan.TicksPerSecond, measurement);
        await using var output = new FileStream(options.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, report, JsonOptions, cancellationToken);

        async ValueTask<long> ReadDeliveryAsync()
        {
            if (!await transactions.MoveNextAsync())
            {
                throw new EndOfStreamException("Replication ended before all expected transactions were delivered.");
            }

            var delivered = DateTimeOffset.UtcNow;
            await using var delivery = transactions.Current;
            var transaction = delivery.Transaction;
            if (transaction.Changes.Count != options.ChangesPerTransaction ||
                previous is { } position && transaction.CommitPosition.CompareTo(position) <= 0)
            {
                throw new InvalidDataException("Replication delivered an unexpected change count or a repeated/out-of-order commit.");
            }

            previous = transaction.CommitPosition;
            await delivery.AcknowledgeAsync(cancellationToken);
            return (delivered - transaction.CommitTimestamp).Ticks;
        }
    }

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
