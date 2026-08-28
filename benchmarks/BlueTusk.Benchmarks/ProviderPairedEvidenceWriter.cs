using System.Diagnostics;
using System.Text.Json;

namespace BlueTusk.Benchmarks;

internal static class ProviderPairedEvidenceWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private const int TrialCount = 5;
    private const int BlocksPerTrial = 501;

    public static async Task CaptureCriticalAsync(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var benchmark = new ProviderComparisonBenchmarks();
        await benchmark.Setup();
        try
        {
            PairedWorkloadEvidence[] workloads =
            [
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskPreparedScalarAsync),
                    benchmark.BlueTuskPreparedScalarAsync,
                    nameof(benchmark.NpgsqlPreparedScalarAsync),
                    benchmark.NpgsqlPreparedScalarAsync,
                    expectedResult: 42,
                    warmupOperationsPerProvider: 512,
                    operationsPerBlock: 64),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskSequentialOneMegabyteByteaAsync),
                    benchmark.BlueTuskSequentialOneMegabyteByteaAsync,
                    nameof(benchmark.NpgsqlSequentialOneMegabyteByteaAsync),
                    benchmark.NpgsqlSequentialOneMegabyteByteaAsync,
                    expectedResult: 1_048_576L,
                    warmupOperationsPerProvider: 32,
                    operationsPerBlock: 4),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskBinaryCopyExport1000RowsAsync),
                    benchmark.BlueTuskBinaryCopyExport1000RowsAsync,
                    nameof(benchmark.NpgsqlBinaryCopyExport1000RowsAsync),
                    benchmark.NpgsqlBinaryCopyExport1000RowsAsync,
                    expectedResult: 499_500L,
                    warmupOperationsPerProvider: 8,
                    operationsPerBlock: 1),
            ];

            await WriteReportAsync(outputPath, workloads);
        }
        finally
        {
            await benchmark.DisposeAsync();
        }
    }

    public static async Task CaptureCopyAsync(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var benchmark = new ProviderComparisonBenchmarks();
        await benchmark.Setup();
        try
        {
            PairedWorkloadEvidence[] workloads =
            [
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskBinaryCopyImport1000RowsAsync),
                    benchmark.BlueTuskBinaryCopyImport1000RowsAsync,
                    nameof(benchmark.NpgsqlBinaryCopyImport1000RowsAsync),
                    benchmark.NpgsqlBinaryCopyImport1000RowsAsync,
                    expectedResult: 1_000L,
                    warmupOperationsPerProvider: 8,
                    operationsPerBlock: 1),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskBinaryCopyExport1000RowsAsync),
                    benchmark.BlueTuskBinaryCopyExport1000RowsAsync,
                    nameof(benchmark.NpgsqlBinaryCopyExport1000RowsAsync),
                    benchmark.NpgsqlBinaryCopyExport1000RowsAsync,
                    expectedResult: 499_500L,
                    warmupOperationsPerProvider: 8,
                    operationsPerBlock: 1),
            ];

            await WriteReportAsync(outputPath, workloads);
        }
        finally
        {
            await benchmark.DisposeAsync();
        }
    }

    public static async Task CaptureAsync(
        string outputPath,
        bool includeExtendedWorkloads = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var benchmark = new ProviderComparisonBenchmarks();
        await benchmark.Setup();
        try
        {
            var workloads = new List<PairedWorkloadEvidence>
            {
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskPoolCheckoutAsync),
                    benchmark.BlueTuskPoolCheckoutAsync,
                    nameof(benchmark.NpgsqlPoolCheckoutAsync),
                    benchmark.NpgsqlPoolCheckoutAsync,
                    warmupOperationsPerProvider: 4096,
                    operationsPerBlock: 256),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskParameterizedScalarAsync),
                    benchmark.BlueTuskParameterizedScalarAsync,
                    nameof(benchmark.NpgsqlParameterizedScalarAsync),
                    benchmark.NpgsqlParameterizedScalarAsync,
                    expectedResult: 42,
                    warmupOperationsPerProvider: 512,
                    operationsPerBlock: 32),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskPreparedScalarAsync),
                    benchmark.BlueTuskPreparedScalarAsync,
                    nameof(benchmark.NpgsqlPreparedScalarAsync),
                    benchmark.NpgsqlPreparedScalarAsync,
                    expectedResult: 42,
                    warmupOperationsPerProvider: 512,
                    operationsPerBlock: 64),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskSequential1000RowsAsync),
                    benchmark.BlueTuskSequential1000RowsAsync,
                    nameof(benchmark.NpgsqlSequential1000RowsAsync),
                    benchmark.NpgsqlSequential1000RowsAsync,
                    expectedResult: 500_500L,
                    warmupOperationsPerProvider: 64,
                    operationsPerBlock: 16),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskSequentialOneMegabyteByteaAsync),
                    benchmark.BlueTuskSequentialOneMegabyteByteaAsync,
                    nameof(benchmark.NpgsqlSequentialOneMegabyteByteaAsync),
                    benchmark.NpgsqlSequentialOneMegabyteByteaAsync,
                    expectedResult: 1_048_576L,
                    warmupOperationsPerProvider: 32,
                    operationsPerBlock: 4),
            };

            if (includeExtendedWorkloads)
            {
                workloads.AddRange(
                [
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskBeginRollbackTransactionAsync),
                    benchmark.BlueTuskBeginRollbackTransactionAsync,
                    nameof(benchmark.NpgsqlBeginRollbackTransactionAsync),
                    benchmark.NpgsqlBeginRollbackTransactionAsync,
                    expectedResult: 1,
                    warmupOperationsPerProvider: 256,
                    operationsPerBlock: 32),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskBatch16ParameterizedScalarsAsync),
                    benchmark.BlueTuskBatch16ParameterizedScalarsAsync,
                    nameof(benchmark.NpgsqlBatch16ParameterizedScalarsAsync),
                    benchmark.NpgsqlBatch16ParameterizedScalarsAsync,
                    expectedResult: 136,
                    warmupOperationsPerProvider: 64,
                    operationsPerBlock: 8),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskBinaryCopyImport1000RowsAsync),
                    benchmark.BlueTuskBinaryCopyImport1000RowsAsync,
                    nameof(benchmark.NpgsqlBinaryCopyImport1000RowsAsync),
                    benchmark.NpgsqlBinaryCopyImport1000RowsAsync,
                    expectedResult: 1_000L,
                    warmupOperationsPerProvider: 8,
                    operationsPerBlock: 1),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskBinaryCopyExport1000RowsAsync),
                    benchmark.BlueTuskBinaryCopyExport1000RowsAsync,
                    nameof(benchmark.NpgsqlBinaryCopyExport1000RowsAsync),
                    benchmark.NpgsqlBinaryCopyExport1000RowsAsync,
                    expectedResult: 499_500L,
                    warmupOperationsPerProvider: 8,
                    operationsPerBlock: 1),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskPreparedTypedRowRoundTripAsync),
                    benchmark.BlueTuskPreparedTypedRowRoundTripAsync,
                    nameof(benchmark.NpgsqlPreparedTypedRowRoundTripAsync),
                    benchmark.NpgsqlPreparedTypedRowRoundTripAsync,
                    expectedResult: 12_534,
                    warmupOperationsPerProvider: 128,
                    operationsPerBlock: 16),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskNotificationDeliveryAsync),
                    benchmark.BlueTuskNotificationDeliveryAsync,
                    nameof(benchmark.NpgsqlNotificationDeliveryAsync),
                    benchmark.NpgsqlNotificationDeliveryAsync,
                    expectedResult: 5,
                    warmupOperationsPerProvider: 64,
                    operationsPerBlock: 8),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskLargeObjectReadOneMegabyteAsync),
                    benchmark.BlueTuskLargeObjectReadOneMegabyteAsync,
                    nameof(benchmark.NpgsqlLargeObjectReadOneMegabyteAsync),
                    benchmark.NpgsqlLargeObjectReadOneMegabyteAsync,
                    expectedResult: 1_048_576L,
                    warmupOperationsPerProvider: 4,
                    operationsPerBlock: 1),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskEfCompiledQueryAsync),
                    benchmark.BlueTuskEfCompiledQueryAsync,
                    nameof(benchmark.NpgsqlEfCompiledQueryAsync),
                    benchmark.NpgsqlEfCompiledQueryAsync,
                    expectedResult: 450,
                    warmupOperationsPerProvider: 64,
                    operationsPerBlock: 8),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskEfMaterialize100RowsAsync),
                    benchmark.BlueTuskEfMaterialize100RowsAsync,
                    nameof(benchmark.NpgsqlEfMaterialize100RowsAsync),
                    benchmark.NpgsqlEfMaterialize100RowsAsync,
                    expectedResult: 100,
                    warmupOperationsPerProvider: 16,
                    operationsPerBlock: 2),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskEfInsertOneAsync),
                    benchmark.BlueTuskEfInsertOneAsync,
                    nameof(benchmark.NpgsqlEfInsertOneAsync),
                    benchmark.NpgsqlEfInsertOneAsync,
                    expectedResult: 1,
                    warmupOperationsPerProvider: 16,
                    operationsPerBlock: 2),
                await CaptureWorkloadAsync(
                    nameof(benchmark.BlueTuskEfUpdateOneAsync),
                    benchmark.BlueTuskEfUpdateOneAsync,
                    nameof(benchmark.NpgsqlEfUpdateOneAsync),
                    benchmark.NpgsqlEfUpdateOneAsync,
                    expectedResult: 1,
                    warmupOperationsPerProvider: 16,
                    operationsPerBlock: 2),
                ]);
            }

            await WriteReportAsync(outputPath, workloads);
        }
        finally
        {
            await benchmark.DisposeAsync();
        }
    }

    private static async Task WriteReportAsync(
        string outputPath,
        IReadOnlyList<PairedWorkloadEvidence> workloads)
    {
        var report = new PairedEvidenceReport(
            SchemaVersion: 1,
            Method: "alternating-provider-blocks",
            CapturedUtc: DateTimeOffset.UtcNow,
            StopwatchFrequency: Stopwatch.Frequency,
            TrialCount,
            BlocksPerTrial,
            Workloads: workloads);

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await using var stream = File.Create(fullOutputPath);
        await JsonSerializer.SerializeAsync(stream, report, JsonOptions);
        await stream.WriteAsync("\n"u8.ToArray());

        Console.WriteLine(
            $"Captured {TrialCount} paired trials for {workloads.Count} provider " +
            $"workloads at '{fullOutputPath}'.");
    }

    private static async Task<PairedWorkloadEvidence> CaptureWorkloadAsync(
        string candidateName,
        Func<Task> candidate,
        string referenceName,
        Func<Task> reference,
        int warmupOperationsPerProvider,
        int operationsPerBlock)
    {
        await WarmUpAsync(
            candidate,
            reference,
            warmupOperationsPerProvider);

        return await CaptureTrialsAsync(
            candidateName,
            referenceName,
            warmupOperationsPerProvider,
            operationsPerBlock,
            () => MeasureBlockAsync(candidate, operationsPerBlock),
            () => MeasureBlockAsync(reference, operationsPerBlock));
    }

    private static async Task<PairedWorkloadEvidence> CaptureWorkloadAsync<T>(
        string candidateName,
        Func<Task<T>> candidate,
        string referenceName,
        Func<Task<T>> reference,
        T expectedResult,
        int warmupOperationsPerProvider,
        int operationsPerBlock)
        where T : IEquatable<T>
    {
        await WarmUpAsync(
            candidate,
            reference,
            expectedResult,
            warmupOperationsPerProvider);

        return await CaptureTrialsAsync(
            candidateName,
            referenceName,
            warmupOperationsPerProvider,
            operationsPerBlock,
            () => MeasureBlockAsync(candidate, expectedResult, operationsPerBlock),
            () => MeasureBlockAsync(reference, expectedResult, operationsPerBlock));
    }

    private static async Task<PairedWorkloadEvidence> CaptureTrialsAsync(
        string candidateName,
        string referenceName,
        int warmupOperationsPerProvider,
        int operationsPerBlock,
        Func<Task<double>> measureCandidate,
        Func<Task<double>> measureReference)
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
                    candidateSamples[blockIndex] = await measureCandidate();
                    referenceSamples[blockIndex] = await measureReference();
                }
                else
                {
                    referenceSamples[blockIndex] = await measureReference();
                    candidateSamples[blockIndex] = await measureCandidate();
                }
            }

            trials[trialIndex] = new PairedTrialEvidence(
                CandidateFirst: candidateFirst,
                CandidateNanosecondsPerOperation: candidateSamples,
                ReferenceNanosecondsPerOperation: referenceSamples);
        }

        return new PairedWorkloadEvidence(
            candidateName,
            referenceName,
            warmupOperationsPerProvider,
            operationsPerBlock,
            trials);
    }

    private static async Task WarmUpAsync(
        Func<Task> candidate,
        Func<Task> reference,
        int operationsPerProvider)
    {
        for (var index = 0; index < operationsPerProvider; index++)
        {
            if ((index & 1) == 0)
            {
                await candidate();
                await reference();
            }
            else
            {
                await reference();
                await candidate();
            }
        }
    }

    private static async Task WarmUpAsync<T>(
        Func<Task<T>> candidate,
        Func<Task<T>> reference,
        T expectedResult,
        int operationsPerProvider)
        where T : IEquatable<T>
    {
        for (var index = 0; index < operationsPerProvider; index++)
        {
            if ((index & 1) == 0)
            {
                await InvokeCheckedAsync(candidate, expectedResult);
                await InvokeCheckedAsync(reference, expectedResult);
            }
            else
            {
                await InvokeCheckedAsync(reference, expectedResult);
                await InvokeCheckedAsync(candidate, expectedResult);
            }
        }
    }

    private static async Task<double> MeasureBlockAsync(
        Func<Task> operation,
        int operationsPerBlock)
    {
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < operationsPerBlock; index++)
        {
            await operation();
        }

        return Stopwatch.GetElapsedTime(started).TotalNanoseconds / operationsPerBlock;
    }

    private static async Task<double> MeasureBlockAsync<T>(
        Func<Task<T>> operation,
        T expectedResult,
        int operationsPerBlock)
        where T : IEquatable<T>
    {
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < operationsPerBlock; index++)
        {
            await InvokeCheckedAsync(operation, expectedResult);
        }

        return Stopwatch.GetElapsedTime(started).TotalNanoseconds / operationsPerBlock;
    }

    private static async Task InvokeCheckedAsync<T>(
        Func<Task<T>> operation,
        T expectedResult)
        where T : IEquatable<T>
    {
        var result = await operation();
        if (!result.Equals(expectedResult))
        {
            throw new InvalidOperationException(
                $"The provider workload returned {result}; expected {expectedResult}.");
        }
    }

    private sealed record PairedEvidenceReport(
        int SchemaVersion,
        string Method,
        DateTimeOffset CapturedUtc,
        long StopwatchFrequency,
        int TrialCount,
        int BlocksPerTrial,
        IReadOnlyList<PairedWorkloadEvidence> Workloads);

    private sealed record PairedWorkloadEvidence(
        string Candidate,
        string Reference,
        int WarmupOperationsPerProvider,
        int OperationsPerBlock,
        IReadOnlyList<PairedTrialEvidence> Trials);

    private sealed record PairedTrialEvidence(
        bool CandidateFirst,
        IReadOnlyList<double> CandidateNanosecondsPerOperation,
        IReadOnlyList<double> ReferenceNanosecondsPerOperation);
}
