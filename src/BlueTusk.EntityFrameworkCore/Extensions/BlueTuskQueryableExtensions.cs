using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    /// <summary>Inserts one row, ignores a typed conflict, and returns an inserted row.</summary>
    public static IQueryable<TEntity> InsertOnConflictDoNothingReturning<TEntity, TConflict>(
        this IQueryable<TEntity> target,
        Expression<Func<TEntity>> values,
        Expression<Func<TEntity, TConflict>> conflictTarget)
        where TEntity : class
        => CreateInsertOnConflictQuery(target, values, conflictTarget, updateProperties: null);

    /// <summary>Inserts one row, updates selected columns from <c>EXCLUDED</c> on conflict, and returns the row.</summary>
    public static IQueryable<TEntity> InsertOnConflictUpdateReturning<TEntity, TConflict, TUpdate>(
        this IQueryable<TEntity> target,
        Expression<Func<TEntity>> values,
        Expression<Func<TEntity, TConflict>> conflictTarget,
        Expression<Func<TEntity, TUpdate>> updateProperties)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(updateProperties);
        return CreateInsertOnConflictQuery(target, values, conflictTarget, updateProperties);
    }

    private static IQueryable<TEntity> CreateInsertOnConflictQuery<TEntity>(
        IQueryable<TEntity> target,
        LambdaExpression values,
        LambdaExpression conflictTarget,
        LambdaExpression? updateProperties)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(conflictTarget);
        if (values.Body is not MemberInitExpression { Bindings.Count: > 0 } initializer
            || initializer.NewExpression.Type != typeof(TEntity))
        {
            throw new ArgumentException(
                "PostgreSQL INSERT ON CONFLICT values must be a non-empty entity object initializer.",
                nameof(values));
        }

        var valueMethod = typeof(BlueTuskQueryableExtensions)
            .GetMethod(
                nameof(InsertValueCore),
                BindingFlags.NonPublic | BindingFlags.Static)!;
        var valueExpressions = new Expression[initializer.Bindings.Count];
        for (var index = 0; index < initializer.Bindings.Count; index++)
        {
            if (initializer.Bindings[index] is not MemberAssignment assignment)
            {
                throw new ArgumentException(
                    "PostgreSQL INSERT ON CONFLICT values must assign direct entity properties.",
                    nameof(values));
            }

            var value = assignment.Expression.Type.IsValueType
                ? Expression.Convert(assignment.Expression, typeof(object))
                : assignment.Expression;
            valueExpressions[index] = Expression.Call(
                valueMethod,
                Expression.Constant(assignment.Member.Name),
                value);
        }

        var conflictProperties = GetSelectedMemberNames(conflictTarget, nameof(conflictTarget));
        var updatedProperties = updateProperties is null
            ? []
            : GetSelectedMemberNames(updateProperties, nameof(updateProperties));
        var noTrackingTarget = target.Expression is MethodCallExpression
        {
            Method.DeclaringType: not null,
            Method.Name: nameof(EntityFrameworkQueryableExtensions.AsNoTracking),
        } noTrackingCall
            && noTrackingCall.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                ? target
                : target.AsNoTracking();
        return noTrackingTarget.Provider.CreateQuery<TEntity>(
            Expression.Call(
                null,
                typeof(BlueTuskQueryableExtensions)
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                    .Single(method =>
                        method.Name == nameof(InsertOnConflictReturningCore)
                        && method.IsGenericMethodDefinition)
                    .MakeGenericMethod(typeof(TEntity)),
                noTrackingTarget.Expression,
                Expression.NewArrayInit(typeof(ITuple), valueExpressions),
                Expression.Constant(conflictProperties),
                Expression.Constant(updatedProperties)));
    }

    private static string[] GetSelectedMemberNames(LambdaExpression selector, string parameterName)
    {
        var expressions = selector.Body switch
        {
            NewExpression tuple => tuple.Arguments,
            MethodCallExpression
            {
                Method.DeclaringType: not null,
                Method.Name: nameof(ValueTuple.Create),
            } tuple when tuple.Method.DeclaringType == typeof(ValueTuple) => tuple.Arguments,
            _ => [selector.Body],
        };
        var names = new string[expressions.Count];
        var distinctNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < expressions.Count; index++)
        {
            var expression = expressions[index];
            while (expression is UnaryExpression
                {
                    NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                } conversion)
            {
                expression = conversion.Operand;
            }

            if (expression is not MemberExpression member
                || member.Expression != selector.Parameters[0]
                || !distinctNames.Add(member.Member.Name))
            {
                throw new ArgumentException(
                    "PostgreSQL INSERT ON CONFLICT selectors must name distinct direct properties.",
                    parameterName);
            }

            names[index] = member.Member.Name;
        }

        return names;
    }

    internal static IQueryable<TEntity> InsertOnConflictReturningCore<TEntity>(
        IQueryable<TEntity> target,
        IReadOnlyList<ITuple> values,
        [NotParameterized] string[] conflictProperties,
        [NotParameterized] string[] updateProperties)
        where TEntity : class
        => throw new InvalidOperationException(
            "InsertOnConflictReturningCore is a provider query marker and cannot be invoked directly.");

    internal static ITuple InsertValueCore(
        [NotParameterized] string propertyName,
        object? value)
        => throw new InvalidOperationException(
            "InsertValueCore is a provider query marker and cannot be invoked directly.");

    /// <summary>Deletes matching rows and returns their mapped values through PostgreSQL <c>RETURNING</c>.</summary>
    public static IQueryable<TSource> DeleteReturning<TSource>(this IQueryable<TSource> source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(source);
        var noTrackingSource = source.AsNoTracking();
        return noTrackingSource.Provider.CreateQuery<TSource>(
            Expression.Call(
                null,
                GetMethod(nameof(DeleteReturning), genericArgumentCount: 1, parameterCount: 1)
                    .MakeGenericMethod(typeof(TSource)),
                noTrackingSource.Expression));
    }

#pragma warning disable EF1001 // A provider extension intentionally matches EF's ExecuteUpdate setter builder.
    /// <summary>Updates matching rows and returns their mapped values through PostgreSQL <c>RETURNING</c>.</summary>
    public static IQueryable<TSource> UpdateReturning<TSource>(
        this IQueryable<TSource> source,
        Action<UpdateSettersBuilder<TSource>> setPropertyCalls)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(setPropertyCalls);
        var setterBuilder = new UpdateSettersBuilder<TSource>();
        setPropertyCalls(setterBuilder);
#pragma warning disable EF1001 // The provider uses EF's canonical ExecuteUpdate setter expression shape.
        var setters = setterBuilder.BuildSettersExpression();
#pragma warning restore EF1001
        var noTrackingSource = source.AsNoTracking();
        return noTrackingSource.Provider.CreateQuery<TSource>(
            Expression.Call(
                null,
                typeof(BlueTuskQueryableExtensions)
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                    .Single(method =>
                        method.Name == nameof(UpdateReturningCore)
                        && method.IsGenericMethodDefinition)
                    .MakeGenericMethod(typeof(TSource)),
                noTrackingSource.Expression,
                setters));
    }
#pragma warning restore EF1001

    /// <summary>Updates one matching mapped property and returns rows through PostgreSQL <c>RETURNING</c>.</summary>
    public static IQueryable<TSource> UpdateReturning<TSource, TProperty>(
        this IQueryable<TSource> source,
        Expression<Func<TSource, TProperty>> propertySelector,
        Expression<Func<TSource, TProperty>> valueSelector)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(propertySelector);
        ArgumentNullException.ThrowIfNull(valueSelector);
        var noTrackingSource = source.AsNoTracking();
        return noTrackingSource.Provider.CreateQuery<TSource>(
            Expression.Call(
                null,
                GetMethod(nameof(UpdateReturning), genericArgumentCount: 2, parameterCount: 3)
                    .MakeGenericMethod(typeof(TSource), typeof(TProperty)),
                noTrackingSource.Expression,
                Expression.Quote(propertySelector),
                Expression.Quote(valueSelector)));
    }

    internal static IQueryable<TSource> UpdateReturningCore<TSource>(
        IQueryable<TSource> source,
        [NotParameterized] IReadOnlyList<ITuple> setters)
        where TSource : class
        => throw new InvalidOperationException(
            "UpdateReturningCore is a provider query marker and cannot be invoked directly.");

    public static IQueryable<TSource> RecursiveDescendants<TSource, TKey>(
        this IQueryable<TSource> source,
        Expression<Func<TSource, (TKey Key, TKey? ParentKey)>> hierarchyKeySelector,
        TKey[] roots,
        [NotParameterized] BlueTuskRecursiveUnionBehavior unionBehavior =
            BlueTuskRecursiveUnionBehavior.Distinct)
        where TKey : struct
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(hierarchyKeySelector);
        ArgumentNullException.ThrowIfNull(roots);
        if (!Enum.IsDefined(unionBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(unionBehavior), unionBehavior, null);
        }

        var method = GetMethod(nameof(RecursiveDescendants), genericArgumentCount: 2, parameterCount: 4)
            .MakeGenericMethod(typeof(TSource), typeof(TKey));
        var normalizedSelector = NormalizeTupleSelector(hierarchyKeySelector);
        return source.Provider.CreateQuery<TSource>(
            Expression.Call(
                null,
                method,
                source.Expression,
                Expression.Quote(normalizedSelector),
                Expression.Constant(roots),
                Expression.Constant(unionBehavior)));
    }

    private static Expression<Func<TSource, (TKey Key, TKey? ParentKey)>> NormalizeTupleSelector<TSource, TKey>(
        Expression<Func<TSource, (TKey Key, TKey? ParentKey)>> selector)
        where TKey : struct
    {
        if (selector.Body is not NewExpression { Arguments.Count: 2 } tuple
            || !tuple.Type.IsGenericType
            || tuple.Type.GetGenericTypeDefinition() != typeof(ValueTuple<,>))
        {
            return selector;
        }

        var createMethod = typeof(ValueTuple)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                method.Name == nameof(ValueTuple.Create)
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 2)
            .MakeGenericMethod(typeof(TKey), typeof(TKey?));
        return Expression.Lambda<Func<TSource, (TKey Key, TKey? ParentKey)>>(
            Expression.Call(createMethod, tuple.Arguments),
            selector.Parameters);
    }

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

/// <summary>Controls duplicate elimination in a recursive hierarchy traversal.</summary>
public enum BlueTuskRecursiveUnionBehavior
{
    /// <summary>Uses <c>UNION</c>, preventing cycles from repeating an identical mapped row.</summary>
    Distinct,

    /// <summary>Uses <c>UNION ALL</c>; suitable only for hierarchies known to be acyclic.</summary>
    All,
}
