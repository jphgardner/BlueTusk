using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore.Query;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL-specific composable query operations.</summary>
public static class BlueTuskQueryableExtensions
{
    public static IQueryable<TSource> DistinctOn<TSource, TKey>(
        this IQueryable<TSource> source,
        Expression<Func<TSource, TKey>> keySelector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keySelector);
        return source.Provider.CreateQuery<TSource>(
            Expression.Call(
                null,
                GetMethod(nameof(DistinctOn), genericArgumentCount: 2, parameterCount: 2)
                    .MakeGenericMethod(typeof(TSource), typeof(TKey)),
                source.Expression,
                Expression.Quote(keySelector)));
    }

    public static IQueryable<TSource> TableSampleSystem<TSource>(
        this IQueryable<TSource> source,
        double percentage)
        => CreateSamplingQuery(source, nameof(TableSampleSystem), percentage, repeatable: null);

    public static IQueryable<TSource> TableSampleSystem<TSource>(
        this IQueryable<TSource> source,
        double percentage,
        double repeatable)
        => CreateSamplingQuery(source, nameof(TableSampleSystem), percentage, repeatable);

    public static IQueryable<TSource> TableSampleBernoulli<TSource>(
        this IQueryable<TSource> source,
        double percentage)
        => CreateSamplingQuery(source, nameof(TableSampleBernoulli), percentage, repeatable: null);

    public static IQueryable<TSource> TableSampleBernoulli<TSource>(
        this IQueryable<TSource> source,
        double percentage,
        double repeatable)
        => CreateSamplingQuery(source, nameof(TableSampleBernoulli), percentage, repeatable);

    public static IQueryable<TSource> ForUpdate<TSource>(
        this IQueryable<TSource> source,
        [NotParameterized] BlueTuskRowLockingBehavior behavior = BlueTuskRowLockingBehavior.Wait)
        => CreateLockingQuery(source, nameof(ForUpdate), behavior);

    public static IQueryable<TSource> ForNoKeyUpdate<TSource>(
        this IQueryable<TSource> source,
        [NotParameterized] BlueTuskRowLockingBehavior behavior = BlueTuskRowLockingBehavior.Wait)
        => CreateLockingQuery(source, nameof(ForNoKeyUpdate), behavior);

    public static IQueryable<TSource> ForShare<TSource>(
        this IQueryable<TSource> source,
        [NotParameterized] BlueTuskRowLockingBehavior behavior = BlueTuskRowLockingBehavior.Wait)
        => CreateLockingQuery(source, nameof(ForShare), behavior);

    public static IQueryable<TSource> ForKeyShare<TSource>(
        this IQueryable<TSource> source,
        [NotParameterized] BlueTuskRowLockingBehavior behavior = BlueTuskRowLockingBehavior.Wait)
        => CreateLockingQuery(source, nameof(ForKeyShare), behavior);

    public static IQueryable<TSource> AsCte<TSource>(
        this IQueryable<TSource> source,
        [NotParameterized] string name)
        => CreateCteQuery(source, nameof(AsCte), name);

    public static IQueryable<TSource> AsMaterializedCte<TSource>(
        this IQueryable<TSource> source,
        [NotParameterized] string name)
        => CreateCteQuery(source, nameof(AsMaterializedCte), name);

    public static IQueryable<TSource> AsNotMaterializedCte<TSource>(
        this IQueryable<TSource> source,
        [NotParameterized] string name)
        => CreateCteQuery(source, nameof(AsNotMaterializedCte), name);

    private static IQueryable<TSource> CreateSamplingQuery<TSource>(
        IQueryable<TSource> source,
        string methodName,
        double percentage,
        double? repeatable)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (double.IsNaN(percentage) || percentage is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentage),
                percentage,
                "A PostgreSQL table-sampling percentage must be between 0 and 100.");
        }

        var parameterCount = repeatable.HasValue ? 3 : 2;
        var method = GetMethod(methodName, genericArgumentCount: 1, parameterCount)
            .MakeGenericMethod(typeof(TSource));
        var arguments = repeatable.HasValue
            ? new Expression[]
            {
                source.Expression,
                Expression.Constant(percentage),
                Expression.Constant(repeatable.GetValueOrDefault()),
            }
            :
            [
                source.Expression,
                Expression.Constant(percentage),
            ];
        return source.Provider.CreateQuery<TSource>(Expression.Call(null, method, arguments));
    }

    private static IQueryable<TSource> CreateLockingQuery<TSource>(
        IQueryable<TSource> source,
        string methodName,
        BlueTuskRowLockingBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(behavior))
        {
            throw new ArgumentOutOfRangeException(nameof(behavior), behavior, null);
        }

        var method = GetMethod(methodName, genericArgumentCount: 1, parameterCount: 2)
            .MakeGenericMethod(typeof(TSource));
        return source.Provider.CreateQuery<TSource>(
            Expression.Call(
                null,
                method,
                source.Expression,
                Expression.Constant(behavior)));
    }

    private static IQueryable<TSource> CreateCteQuery<TSource>(
        IQueryable<TSource> source,
        string methodName,
        string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A PostgreSQL CTE name cannot contain a null character.", nameof(name));
        }

        if (Encoding.UTF8.GetByteCount(name) > 63)
        {
            throw new ArgumentException(
                "A PostgreSQL CTE name cannot exceed 63 UTF-8 bytes.",
                nameof(name));
        }

        var method = GetMethod(methodName, genericArgumentCount: 1, parameterCount: 2)
            .MakeGenericMethod(typeof(TSource));
        return source.Provider.CreateQuery<TSource>(
            Expression.Call(
                null,
                method,
                source.Expression,
                Expression.Constant(name)));
    }

    private static MethodInfo GetMethod(
        string name,
        int genericArgumentCount,
        int parameterCount)
        => typeof(BlueTuskQueryableExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                method.Name == name
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == genericArgumentCount
                && method.GetParameters().Length == parameterCount);
}

/// <summary>Controls how a PostgreSQL row-locking query behaves when a row is unavailable.</summary>
public enum BlueTuskRowLockingBehavior
{
    Wait,
    NoWait,
    SkipLocked,
}
