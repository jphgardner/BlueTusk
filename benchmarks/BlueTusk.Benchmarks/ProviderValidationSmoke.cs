namespace BlueTusk.Benchmarks;

internal static class ProviderValidationSmoke
{
    public static async Task RunAsync()
    {
        var benchmark = new ProviderComparisonBenchmarks();
        await benchmark.Setup();
        try
        {
            await CheckPairAsync(
                "warm pool checkout",
                benchmark.BlueTuskPoolCheckoutAsync,
                benchmark.NpgsqlPoolCheckoutAsync);
            await CheckPairAsync(
                "parameterized scalar",
                benchmark.BlueTuskParameterizedScalarAsync,
                benchmark.NpgsqlParameterizedScalarAsync,
                42);
            await CheckPairAsync(
                "prepared scalar",
                benchmark.BlueTuskPreparedScalarAsync,
                benchmark.NpgsqlPreparedScalarAsync,
                42);
            await CheckPairAsync(
                "sequential 1,000 rows",
                benchmark.BlueTuskSequential1000RowsAsync,
                benchmark.NpgsqlSequential1000RowsAsync,
                500_500L);
            await CheckPairAsync(
                "sequential 1 MiB bytea",
                benchmark.BlueTuskSequentialOneMegabyteByteaAsync,
                benchmark.NpgsqlSequentialOneMegabyteByteaAsync,
                1_048_576L);
            await CheckPairAsync(
                "begin and rollback transaction",
                benchmark.BlueTuskBeginRollbackTransactionAsync,
                benchmark.NpgsqlBeginRollbackTransactionAsync,
                1);
            await CheckPairAsync(
                "batch of 16 parameterized scalars",
                benchmark.BlueTuskBatch16ParameterizedScalarsAsync,
                benchmark.NpgsqlBatch16ParameterizedScalarsAsync,
                136);
            await CheckPairAsync(
                "binary COPY import of 1,000 rows",
                benchmark.BlueTuskBinaryCopyImport1000RowsAsync,
                benchmark.NpgsqlBinaryCopyImport1000RowsAsync,
                1_000L);
            await CheckPairAsync(
                "binary COPY export of 1,000 rows",
                benchmark.BlueTuskBinaryCopyExport1000RowsAsync,
                benchmark.NpgsqlBinaryCopyExport1000RowsAsync,
                499_500L);
            await CheckPairAsync(
                "prepared typed-row round-trip",
                benchmark.BlueTuskPreparedTypedRowRoundTripAsync,
                benchmark.NpgsqlPreparedTypedRowRoundTripAsync);
            await CheckPairAsync(
                "notification delivery",
                benchmark.BlueTuskNotificationDeliveryAsync,
                benchmark.NpgsqlNotificationDeliveryAsync,
                5);
            await CheckPairAsync(
                "large-object 1 MiB read",
                benchmark.BlueTuskLargeObjectReadOneMegabyteAsync,
                benchmark.NpgsqlLargeObjectReadOneMegabyteAsync,
                1_048_576L);
            await CheckPairAsync(
                "EF Core compiled query",
                benchmark.BlueTuskEfCompiledQueryAsync,
                benchmark.NpgsqlEfCompiledQueryAsync,
                450);
            await CheckPairAsync(
                "EF Core materialize 100 rows",
                benchmark.BlueTuskEfMaterialize100RowsAsync,
                benchmark.NpgsqlEfMaterialize100RowsAsync,
                100);
            await CheckPairAsync(
                "EF Core insert one",
                benchmark.BlueTuskEfInsertOneAsync,
                benchmark.NpgsqlEfInsertOneAsync,
                1);
            await CheckPairAsync(
                "EF Core update one",
                benchmark.BlueTuskEfUpdateOneAsync,
                benchmark.NpgsqlEfUpdateOneAsync,
                1);

            Console.WriteLine("Provider validation smoke passed for 16 paired workloads.");
        }
        finally
        {
            await benchmark.DisposeAsync();
        }
    }

    private static async Task CheckPairAsync(
        string workload,
        Func<Task> candidate,
        Func<Task> reference)
    {
        await candidate();
        await reference();
        Console.WriteLine($"PASS {workload}");
    }

    private static async Task CheckPairAsync<T>(
        string workload,
        Func<Task<T>> candidate,
        Func<Task<T>> reference)
        where T : IEquatable<T>
    {
        var candidateResult = await candidate();
        var referenceResult = await reference();
        if (!candidateResult.Equals(referenceResult))
        {
            throw new InvalidOperationException(
                $"{workload}: BlueTusk returned {candidateResult}; Npgsql returned {referenceResult}.");
        }

        Console.WriteLine($"PASS {workload}: {candidateResult}");
    }

    private static async Task CheckPairAsync<T>(
        string workload,
        Func<Task<T>> candidate,
        Func<Task<T>> reference,
        T expected)
        where T : IEquatable<T>
    {
        var candidateResult = await candidate();
        var referenceResult = await reference();
        if (!candidateResult.Equals(referenceResult))
        {
            throw new InvalidOperationException(
                $"{workload}: BlueTusk returned {candidateResult}; Npgsql returned {referenceResult}.");
        }

        if (!candidateResult.Equals(expected))
        {
            throw new InvalidOperationException(
                $"{workload}: both providers returned {candidateResult}; expected {expected}.");
        }

        Console.WriteLine($"PASS {workload}: {candidateResult}");
    }
}
