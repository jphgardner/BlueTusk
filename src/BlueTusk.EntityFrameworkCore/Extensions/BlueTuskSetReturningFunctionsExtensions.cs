using Microsoft.EntityFrameworkCore.Infrastructure;
#if !BLUETUSK_EF8_OR_GREATER
using System.Linq.Expressions;
using BlueTusk.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query;
#endif

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
        BlueTuskGuard.NotNull(database);
        BlueTuskGuard.NotZero(step);
#if BLUETUSK_EF8_OR_GREATER
        return database.SqlQueryRaw<int>(
            """
            SELECT "Value"
            FROM generate_series({0}::integer, {1}::integer, {2}::integer) AS "series"("Value")
            """,
            start,
            stop,
            step);
#else
        return CreateLegacySeries(database, () => start, () => stop, () => step, "integer", "integer");
#endif
    }

    public static IQueryable<long> GenerateSeries(
        this DatabaseFacade database,
        long start,
        long stop,
        long step = 1)
    {
        BlueTuskGuard.NotNull(database);
        BlueTuskGuard.NotZero(step);
#if BLUETUSK_EF8_OR_GREATER
        return database.SqlQueryRaw<long>(
            """
            SELECT "Value"
            FROM generate_series({0}::bigint, {1}::bigint, {2}::bigint) AS "series"("Value")
            """,
            start,
            stop,
            step);
#else
        return CreateLegacySeries(database, () => start, () => stop, () => step, "bigint", "bigint");
#endif
    }

    public static IQueryable<decimal> GenerateSeries(
        this DatabaseFacade database,
        decimal start,
        decimal stop,
        decimal step = 1)
    {
        BlueTuskGuard.NotNull(database);
        BlueTuskGuard.NotZero(step);
#if BLUETUSK_EF8_OR_GREATER
        return database.SqlQueryRaw<decimal>(
            """
            SELECT "Value"
            FROM generate_series({0}::numeric, {1}::numeric, {2}::numeric) AS "series"("Value")
            """,
            start,
            stop,
            step);
#else
        return CreateLegacySeries(database, () => start, () => stop, () => step, "numeric", "numeric");
#endif
    }

    public static IQueryable<DateTime> GenerateSeries(
        this DatabaseFacade database,
        DateTime start,
        DateTime stop,
        TimeSpan step)
    {
        BlueTuskGuard.NotNull(database);
        ThrowIfZero(step);
#if BLUETUSK_EF8_OR_GREATER
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
#else
        return CreateLegacySeries(database, () => start, () => stop, () => step, "timestamp without time zone", "interval");
#endif
    }

    public static IQueryable<DateTimeOffset> GenerateSeries(
        this DatabaseFacade database,
        DateTimeOffset start,
        DateTimeOffset stop,
        TimeSpan step)
    {
        BlueTuskGuard.NotNull(database);
        ThrowIfZero(step);
#if BLUETUSK_EF8_OR_GREATER
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
#else
        return CreateLegacySeries(database, () => start, () => stop, () => step, "timestamp with time zone", "interval");
#endif
    }

#if !BLUETUSK_EF8_OR_GREATER
    private static IQueryable<TResult> CreateLegacySeries<TResult, TStep>(DatabaseFacade database,
        Expression<Func<TResult>> start, Expression<Func<TResult>> stop, Expression<Func<TStep>> step,
        string resultStoreType, string stepStoreType)
    {
        var root = new BlueTuskSetReturningFunctionQueryRootExpression(
            "generate_series", typeof(TResult), [start.Body, stop.Body, step.Body],
            [resultStoreType, resultStoreType, stepStoreType], resultStoreType, isNullable: false, withOrdinality: false);
#if BLUETUSK_EF7_OR_GREATER
        return database.GetService<IAsyncQueryProvider>().CreateQuery<TResult>(root);
#else
        return database.GetService<IAsyncQueryProvider>().CreateQuery<TResult>(root.ProjectResult());
#endif
    }
#endif

    private static void ThrowIfZero(TimeSpan step)
    {
        if (step == TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "The series step cannot be zero.");
        }
    }
}
