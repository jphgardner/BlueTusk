using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{ConnectionVariable} must target a dedicated benchmark database.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cancellationToken = timeout.Token;
        var samples = new long[checked(options.MeasurementTransactions * options.ChangesPerTransaction)];
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

        using var process = Process.GetCurrentProcess();
        Signal(options.MeasurementReadyPath);

        var cpuBefore = process.TotalProcessorTime;
        var gcBefore = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < options.MeasurementTransactions; index++)
        {
            var latency = await ReadDeliveryAsync();
            samples.AsSpan(index * options.ChangesPerTransaction, options.ChangesPerTransaction).Fill(latency);
        }

        var elapsed = Stopwatch.GetElapsedTime(started).Ticks;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var gc = new[] { GC.CollectionCount(0) - gcBefore[0], GC.CollectionCount(1) - gcBefore[1], GC.CollectionCount(2) - gcBefore[2] };
        process.Refresh();
        var measurement = new ProviderRequestCapture.Window(
            elapsed, samples.Length, allocated, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            process.PeakWorkingSet64, gc, [new(samples, samples.Length)]);
        var report = new PerformanceEvidenceGenerator.TrialFile(
            1, "bluetusk-performance-trial", "Streams",
            $"{os}|Streams|changes={options.ChangesPerTransaction}|scenario=commit-to-delivery",
            "candidate", options.Trial, options.SourceCommit, Diagnostic: true, Synthetic: false, "BlueTusk.Streams",
            JsonSerializer.SerializeToElement(new
            {
                os,
                architecture = "x64",
                runtime = RuntimeInformation.FrameworkDescription,
                postgreSqlImage = options.PostgreSqlImage,
                latency = "PostgreSQL WAL commit timestamp to candidate delivery, in UTC ticks",
                sourceWriter = "separate process",
                allocation = "process-wide managed allocation counter delta",
                gc = "managed collection count delta by generation",
                peakRss = "candidate process lifetime through measurement end",
            }),
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

            var latency = (delivered - transaction.CommitTimestamp).Ticks;
            if (latency <= 0)
            {
                throw new InvalidDataException("Delivery time did not follow the WAL commit timestamp. Check PostgreSQL and client clock synchronization.");
            }

            previous = transaction.CommitPosition;
            await delivery.AcknowledgeAsync(cancellationToken);
            return latency;
        }
    }

    private static void Signal(string path)
    {
        using var marker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }
}
