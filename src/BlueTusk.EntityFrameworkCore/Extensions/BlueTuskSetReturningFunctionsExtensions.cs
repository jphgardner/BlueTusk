using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Typed, composable PostgreSQL set-returning query roots.</summary>
public static class BlueTuskSetReturningFunctionsExtensions
{
    public static IQueryable<int> GenerateSeries(
        this DatabaseFacade database,
        int start,
        int stop,
        int step = 1)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfZero(step);
        return database.SqlQueryRaw<int>(
            """
            SELECT "Value"
            FROM generate_series({0}::integer, {1}::integer, {2}::integer) AS "series"("Value")
            """,
            start,
            stop,
            step);
    }

    public static IQueryable<long> GenerateSeries(
        this DatabaseFacade database,
        long start,
        long stop,
        long step = 1)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfZero(step);
        return database.SqlQueryRaw<long>(
            """
            SELECT "Value"
            FROM generate_series({0}::bigint, {1}::bigint, {2}::bigint) AS "series"("Value")
            """,
            start,
            stop,
            step);
    }

    public static IQueryable<decimal> GenerateSeries(
        this DatabaseFacade database,
        decimal start,
        decimal stop,
        decimal step = 1)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfZero(step);
        return database.SqlQueryRaw<decimal>(
            """
            SELECT "Value"
            FROM generate_series({0}::numeric, {1}::numeric, {2}::numeric) AS "series"("Value")
            """,
            start,
            stop,
            step);
    }

    public static IQueryable<DateTime> GenerateSeries(
        this DatabaseFacade database,
        DateTime start,
        DateTime stop,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(database);
        ThrowIfZero(step);
        return database.SqlQueryRaw<DateTime>(
            """
            SELECT "Value"
            FROM generate_series(
                {0}::timestamp without time zone,
                {1}::timestamp without time zone,
                {2}::interval) AS "series"("Value")
            """,
            start,
            stop,
            step);
    }

    public static IQueryable<DateTimeOffset> GenerateSeries(
        this DatabaseFacade database,
        DateTimeOffset start,
        DateTimeOffset stop,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(database);
        ThrowIfZero(step);
        return database.SqlQueryRaw<DateTimeOffset>(
            """
            SELECT "Value"
            FROM generate_series(
                {0}::timestamp with time zone,
                {1}::timestamp with time zone,
                {2}::interval) AS "series"("Value")
            """,
            start,
            stop,
            step);
    }

    private static void ThrowIfZero(TimeSpan step)
    {
        if (step == TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "The series step cannot be zero.");
        }
    }
}
