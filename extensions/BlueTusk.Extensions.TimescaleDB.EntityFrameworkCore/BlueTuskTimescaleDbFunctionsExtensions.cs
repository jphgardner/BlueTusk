using BlueTusk.TypeSystem;

namespace Microsoft.EntityFrameworkCore;

/// <summary>TimescaleDB functions translated by the optional BlueTusk EF package.</summary>
public static class BlueTuskTimescaleDbFunctionsExtensions
{
    public static DateTimeOffset TimeBucket(
        this DbFunctions _, BlueTuskInterval bucketWidth, DateTimeOffset timestamp) =>
        ThrowTranslationOnly<DateTimeOffset>();

    public static DateTimeOffset TimeBucket(
        this DbFunctions _, BlueTuskInterval bucketWidth, DateTimeOffset timestamp, BlueTuskInterval offset) =>
        ThrowTranslationOnly<DateTimeOffset>();

    public static DateTimeOffset TimeBucket(
        this DbFunctions _, BlueTuskInterval bucketWidth, DateTimeOffset timestamp, DateTimeOffset origin) =>
        ThrowTranslationOnly<DateTimeOffset>();

    public static DateTimeOffset TimeBucket(
        this DbFunctions _, BlueTuskInterval bucketWidth, DateTimeOffset timestamp, string timezone) =>
        ThrowTranslationOnly<DateTimeOffset>();

    public static DateTimeOffset TimeBucket(
        this DbFunctions _,
        BlueTuskInterval bucketWidth,
        DateTimeOffset timestamp,
        string timezone,
        DateTimeOffset origin,
        BlueTuskInterval offset) =>
        ThrowTranslationOnly<DateTimeOffset>();

    public static DateTime TimeBucket(
        this DbFunctions _, BlueTuskInterval bucketWidth, DateTime timestamp) =>
        ThrowTranslationOnly<DateTime>();

    public static DateTime TimeBucket(
        this DbFunctions _, BlueTuskInterval bucketWidth, DateTime timestamp, BlueTuskInterval offset) =>
        ThrowTranslationOnly<DateTime>();

    public static DateTime TimeBucket(
        this DbFunctions _, BlueTuskInterval bucketWidth, DateTime timestamp, DateTime origin) =>
        ThrowTranslationOnly<DateTime>();

    public static DateOnly TimeBucket(
        this DbFunctions _, BlueTuskInterval bucketWidth, DateOnly date) =>
        ThrowTranslationOnly<DateOnly>();

    public static DateOnly TimeBucket(
        this DbFunctions _, BlueTuskInterval bucketWidth, DateOnly date, BlueTuskInterval offset) =>
        ThrowTranslationOnly<DateOnly>();

    public static DateOnly TimeBucket(
        this DbFunctions _, BlueTuskInterval bucketWidth, DateOnly date, DateOnly origin) =>
        ThrowTranslationOnly<DateOnly>();

    public static short TimeBucket(this DbFunctions _, short bucketWidth, short value) =>
        ThrowTranslationOnly<short>();

    public static short TimeBucket(this DbFunctions _, short bucketWidth, short value, short offset) =>
        ThrowTranslationOnly<short>();

    public static int TimeBucket(this DbFunctions _, int bucketWidth, int value) =>
        ThrowTranslationOnly<int>();

    public static int TimeBucket(this DbFunctions _, int bucketWidth, int value, int offset) =>
        ThrowTranslationOnly<int>();

    public static long TimeBucket(this DbFunctions _, long bucketWidth, long value) =>
        ThrowTranslationOnly<long>();

    public static long TimeBucket(this DbFunctions _, long bucketWidth, long value, long offset) =>
        ThrowTranslationOnly<long>();

    /// <summary>Returns the value associated with the earliest ordering key in a group.</summary>
    public static TValue? TimescaleFirst<TValue, TOrder>(
        this DbFunctions _, IEnumerable<(TValue Value, TOrder Order)> values) =>
        ThrowTranslationOnly<TValue?>();

    /// <summary>Returns the value associated with the latest ordering key in a group.</summary>
    public static TValue? TimescaleLast<TValue, TOrder>(
        this DbFunctions _, IEnumerable<(TValue Value, TOrder Order)> values) =>
        ThrowTranslationOnly<TValue?>();

    /// <summary>Builds underflow, bucket, and overflow counts for a group of double values.</summary>
    public static int[] TimescaleHistogram(
        this DbFunctions _,
        IEnumerable<double> values,
        double minimum,
        double maximum,
        int bucketCount) =>
        ThrowTranslationOnly<int[]>();

    private static T ThrowTranslationOnly<T>() =>
        throw new InvalidOperationException(
            "This TimescaleDB method is for use in translated Entity Framework Core queries only.");
}
