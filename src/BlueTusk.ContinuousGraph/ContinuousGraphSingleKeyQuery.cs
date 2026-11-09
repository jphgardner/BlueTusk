using System.Linq.Expressions;
using BlueTusk.EntityFrameworkCore.Graphs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.ContinuousGraph;

// The normal scoped query supports arbitrary registered composition. This fast
// path accepts only a typed root followed by one ordering and one literal Take.
internal sealed class ContinuousGraphSingleKeyQuery<TContext, TResult, TKey>(
    IModel model,
    WeakReference<IDbContextOptions> options,
    BlueTuskGraphQueryRootExpression template,
    Func<TContext, object[], TKey, IAsyncEnumerable<TResult>> execute)
    where TContext : DbContext
    where TResult : class
    where TKey : notnull
{
    public static ContinuousGraphSingleKeyQuery<TContext, TResult, TKey>? TryCreate(
        TContext context,
        IQueryable<TResult> query,
        Expression<Func<TResult, TKey>> keySelector)
    {
        if ((typeof(TKey) != typeof(int) && typeof(TKey) != typeof(long) && typeof(TKey) != typeof(Guid)) ||
            FindRoot(query.Expression) is not { } root ||
            !ReferenceEquals(query.Provider, root.QueryProvider) ||
            HasExpressionInterceptor(context))
        {
            return null;
        }

        var contextParameter = Expression.Parameter(typeof(TContext), "context");
        var graphArguments = Expression.Parameter(typeof(object[]), "graphArguments");
        var affectedKey = Expression.Parameter(typeof(TKey), "affectedKey");
        var template = root.WithArgumentParameter(graphArguments);
        var predicate = Expression.Lambda<Func<TResult, bool>>(
            ContinuousGraphKeyScope.CreateScalarKeyFilter(keySelector.Body, affectedKey),
            keySelector.Parameters);
        var filtered = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [typeof(TResult)], template, Expression.Quote(predicate));

        // One row is a replacement and two rows fail duplicate/out-of-scope
        // validation in the evaluator. For either outcome the SQL sort is
        // unnecessary. Keep Take(1) and composed shapes on the regular path.
        var probe = Expression.Call(
            typeof(Queryable), nameof(Queryable.Take), [typeof(TResult)], filtered, Expression.Constant(2));
        var untracked = Expression.Call(
            typeof(EntityFrameworkQueryableExtensions), nameof(EntityFrameworkQueryableExtensions.AsNoTracking),
            [typeof(TResult)], probe);
        var lambda = Expression.Lambda<Func<TContext, object[], TKey, IQueryable<TResult>>>(
            untracked, contextParameter, graphArguments, affectedKey);

        return new(
            context.Model,
            new WeakReference<IDbContextOptions>(context.GetService<IDbContextOptions>()),
            template,
            EF.CompileAsyncQuery<TContext, object[], TKey, TResult>(lambda));
    }

    public IAsyncEnumerable<TResult>? TryExecute(TContext context, IQueryable<TResult> query, TKey key)
    {
        if (FindRoot(query.Expression) is not { Values: ConstantExpression { Value: object[] arguments } } root ||
            !ReferenceEquals(query.Provider, root.QueryProvider) ||
            !template.HasSameSqlShape(root) ||
            !ReferenceEquals(model, context.Model) ||
            !options.TryGetTarget(out var registrationOptions) ||
            !ReferenceEquals(registrationOptions, context.GetService<IDbContextOptions>()) ||
            HasExpressionInterceptor(context))
        {
            return null;
        }

        // Both values come from this execution. The caller owns the context
        // and supplies cancellation to the enumerator as it reads the rows.
        return execute(context, arguments, key);
    }

    // A query expression interceptor may depend on the caller's original
    // ordering or limit. Connection and command interceptors still run on
    // the normal EF pipeline used by the compiled query.
    private static bool HasExpressionInterceptor(TContext context) =>
        context.GetService<IInterceptors>().Aggregate<IQueryExpressionInterceptor>() is not null;

    private static BlueTuskGraphQueryRootExpression? FindRoot(Expression query)
    {
        if (query is MethodCallExpression noTracking &&
            noTracking.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions) &&
            noTracking.Method.Name == nameof(EntityFrameworkQueryableExtensions.AsNoTracking))
        {
            query = noTracking.Arguments[0];
        }

        return query is MethodCallExpression
               {
                   Method.Name: nameof(Queryable.Take),
                   Arguments: [MethodCallExpression order, ConstantExpression { Value: int and > 1 }],
               } take &&
               take.Method.DeclaringType == typeof(Queryable) &&
               order.Method.DeclaringType == typeof(Queryable) &&
               order.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending)
            ? order.Arguments[0] as BlueTuskGraphQueryRootExpression
            : null;
    }
}
