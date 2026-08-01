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
}
