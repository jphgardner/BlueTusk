using System.Diagnostics;
using System.Text.Json;

namespace BlueTusk.Benchmarks;

internal static class MultiplexingPairedEvidenceWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private const int ExpectedBurstResult = 2_016;
    private const int OperationsPerBurst = 64;
    private const int WarmupBurstsPerProvider = 64;
    private const int TrialCount = 5;
    private const int BlocksPerTrial = 501;
    private const int BurstsPerBlock = 4;

    public static async Task CaptureAsync(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var benchmark = new MultiplexingComparisonBenchmarks();
        await benchmark.Setup();
        try
        {
            await WarmUpAsync(
                benchmark.BlueTuskConcurrentScalarBurstAsync,
                benchmark.NpgsqlConcurrentScalarBurstAsync);
            await WarmUpAsync(
                benchmark.BlueTuskReusedScalarBurstAsync,
                benchmark.NpgsqlReusedScalarBurstAsync);
            await WarmUpAsync(
                benchmark.BlueTuskPooledConcurrentScalarBurstAsync,
                benchmark.NpgsqlPooledConcurrentScalarBurstAsync);
            await WarmUpAsync(
                benchmark.BlueTuskPooledReusedScalarBurstAsync,
                benchmark.NpgsqlPooledReusedScalarBurstAsync);

            var workloads = new[]
            {
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskConcurrentScalarBurstAsync),
                    benchmark.BlueTuskConcurrentScalarBurstAsync,
                    nameof(benchmark.NpgsqlConcurrentScalarBurstAsync),
                    benchmark.NpgsqlConcurrentScalarBurstAsync),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskReusedScalarBurstAsync),
                    benchmark.BlueTuskReusedScalarBurstAsync,
                    nameof(benchmark.NpgsqlReusedScalarBurstAsync),
                    benchmark.NpgsqlReusedScalarBurstAsync),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskPooledConcurrentScalarBurstAsync),
                    benchmark.BlueTuskPooledConcurrentScalarBurstAsync,
                    nameof(benchmark.NpgsqlPooledConcurrentScalarBurstAsync),
                    benchmark.NpgsqlPooledConcurrentScalarBurstAsync),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskPooledReusedScalarBurstAsync),
                    benchmark.BlueTuskPooledReusedScalarBurstAsync,
                    nameof(benchmark.NpgsqlPooledReusedScalarBurstAsync),
                    benchmark.NpgsqlPooledReusedScalarBurstAsync),
            };

            var report = new PairedEvidenceReport(
                SchemaVersion: 1,
                Method: "alternating-provider-blocks",
                CapturedUtc: DateTimeOffset.UtcNow,
                StopwatchFrequency: Stopwatch.Frequency,
                OperationsPerBurst,
                WarmupBurstsPerProvider,
                TrialCount,
                BlocksPerTrial,
                BurstsPerBlock,
                Workloads: workloads);

            var fullOutputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
            await using var stream = File.Create(fullOutputPath);
            await JsonSerializer.SerializeAsync(
                stream,
                report,
                JsonOptions);
            await stream.WriteAsync("\n"u8.ToArray());

            Console.WriteLine(
                $"Captured {TrialCount} paired trials for {workloads.Length} multiplexing " +
                $"workloads at '{fullOutputPath}'.");
        }
        finally
        {
            await benchmark.DisposeAsync();
        }
    }

    private static async Task WarmUpAsync(
        Func<Task<int>> candidate,
        Func<Task<int>> reference)
    {
        for (var index = 0; index < WarmupBurstsPerProvider; index++)
        {
            if ((index & 1) == 0)
            {
                await InvokeCheckedAsync(candidate);
                await InvokeCheckedAsync(reference);
            }
            else
            {
                await InvokeCheckedAsync(reference);
                await InvokeCheckedAsync(candidate);
            }
        }
    }

    private static async Task<PairedWorkloadEvidence> CaptureWorkloadAsync(
        string candidateName,
        Func<Task<int>> candidate,
        string referenceName,
        Func<Task<int>> reference)
    {
        var trials = new PairedTrialEvidence[TrialCount];
        for (var trialIndex = 0; trialIndex < trials.Length; trialIndex++)
        {
            var candidateSamples = new double[BlocksPerTrial];
            var referenceSamples = new double[BlocksPerTrial];
            var candidateFirst = (trialIndex & 1) == 0;
            for (var blockIndex = 0; blockIndex < BlocksPerTrial; blockIndex++)
            {
                var runCandidateFirst = candidateFirst == ((blockIndex & 1) == 0);
                if (runCandidateFirst)
                {
                    candidateSamples[blockIndex] = await MeasureBlockAsync(candidate);
                    referenceSamples[blockIndex] = await MeasureBlockAsync(reference);
                }
                else
                {
                    referenceSamples[blockIndex] = await MeasureBlockAsync(reference);
                    candidateSamples[blockIndex] = await MeasureBlockAsync(candidate);
                }
            }

            trials[trialIndex] = new PairedTrialEvidence(
                CandidateFirst: candidateFirst,
                CandidateNanosecondsPerOperation: candidateSamples,
                ReferenceNanosecondsPerOperation: referenceSamples);
        }

        return new PairedWorkloadEvidence(candidateName, referenceName, trials);
    }

    private static async Task<double> MeasureBlockAsync(Func<Task<int>> operation)
    {
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < BurstsPerBlock; index++)
        {
            await InvokeCheckedAsync(operation);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        return elapsed.TotalNanoseconds / (BurstsPerBlock * OperationsPerBurst);
    }

    private static async Task InvokeCheckedAsync(Func<Task<int>> operation)
    {
        var result = await operation();
        if (result != ExpectedBurstResult)
        {
            throw new InvalidOperationException(
                $"The multiplexing burst returned {result}; expected {ExpectedBurstResult}.");
        }
    }

    private sealed record PairedEvidenceReport(
        int SchemaVersion,
        string Method,
        DateTimeOffset CapturedUtc,
        long StopwatchFrequency,
        int OperationsPerBurst,
        int WarmupBurstsPerProvider,
        int TrialCount,
        int BlocksPerTrial,
        int BurstsPerBlock,
        IReadOnlyList<PairedWorkloadEvidence> Workloads);

    private sealed record PairedWorkloadEvidence(
        string Candidate,
        string Reference,
        IReadOnlyList<PairedTrialEvidence> Trials);

    private sealed record PairedTrialEvidence(
        bool CandidateFirst,
        IReadOnlyList<double> CandidateNanosecondsPerOperation,
        IReadOnlyList<double> ReferenceNanosecondsPerOperation);
}
