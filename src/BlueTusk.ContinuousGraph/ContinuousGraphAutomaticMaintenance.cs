using System.Linq.Expressions;
using BlueTusk.Live;
using BlueTusk.Streams;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.ContinuousGraph;

internal sealed record ContinuousGraphAutomaticTableImpact(
    string Schema,
    string Table,
    IReadOnlyList<string> ResultKeyColumns,
    bool IsComplete);

internal sealed class ContinuousGraphTieredEvaluator<TResult, TKey>(
    string impactFingerprint,
    IReadOnlyList<ContinuousGraphAutomaticTableImpact> tableImpacts,
    Func<IReadOnlyCollection<TKey>, LiveQueryExecutionContext, CancellationToken,
        ValueTask<IReadOnlyList<TResult>>> executeScopedAsync,
    int maximumAffectedKeys,
    IEqualityComparer<TKey> keyComparer,
    IContinuousGraphCdcProjector<TResult, TKey>? trustedProjector) :
    IContinuousGraphIncrementalEvaluator<TResult, TKey>
    where TResult : class
    where TKey : notnull
{
    private readonly Dictionary<(string Schema, string Table), ContinuousGraphAutomaticTableImpact[]> _tableImpacts =
        tableImpacts.GroupBy(static impact => (impact.Schema, impact.Table))
            .ToDictionary(static group => group.Key, static group => group.ToArray());

    public async ValueTask<ContinuousGraphIncrementalResult<TResult, TKey>> EvaluateAsync(
        ChangeTransaction transaction,
        LiveQueryExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (trustedProjector is not null &&
            trustedProjector.TrustContract.IsComplete &&
            string.Equals(
                trustedProjector.TrustContract.SchemaFingerprint,
                impactFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            ContinuousGraphIncrementalResult<TResult, TKey>? projected = null;
            try
            {
                projected = await trustedProjector.ProjectAsync(
                    transaction,
                    context,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The trusted path is an optimisation. Its failure must not prevent
                // the compiler-generated authoritative path from proving the state.
            }

            if (projected is not null &&
                projected.Disposition is not ContinuousGraphIncrementalDisposition.RequiresRepair)
            {
                return projected.WithMaintenanceTier(
                    projected.Disposition is ContinuousGraphIncrementalDisposition.Exact
                        ? ContinuousGraphMaintenanceTier.TrustedCdcDelta
                        : ContinuousGraphMaintenanceTier.None);
            }
        }

        var affected = new HashSet<TKey>(keyComparer);
        var relevant = false;
        await foreach (var change in transaction.Changes
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (change is TruncateChange truncate)
            {
                if (truncate.Tables.Any(IsRelevant))
                {
                    return ContinuousGraphIncrementalResult<TResult, TKey>
                        .RequiresRepair("relevant-truncate");
                }

                continue;
            }

            if (change is LogicalMessageChange)
            {
                continue;
            }

            var (first, second) = change switch
            {
                InsertChange insert => (insert.NewRow, (ChangeRow?)null),
                UpdateChange update => (update.OldRow, update.NewRow),
                DeleteChange delete => (delete.OldRow, (ChangeRow?)null),
                _ => ((ChangeRow?)null, (ChangeRow?)null),
            };
            if (first is null ||
                !_tableImpacts.TryGetValue((first.Table.Schema, first.Table.Name), out var impacts))
            {
                continue;
            }

            relevant = true;
            if (impacts.Any(static impact => !impact.IsComplete))
            {
                return ContinuousGraphIncrementalResult<TResult, TKey>
                    .RequiresRepair("unscopable-relevant-element");
            }

            for (var index = 0; index < 2; index++)
            {
                var row = index == 0 ? first : second;
                if (row is null)
                {
                    break;
                }

                foreach (var impact in impacts)
                {
                    if (!TryAddKeys(row, impact.ResultKeyColumns, affected))
                    {
                        return ContinuousGraphIncrementalResult<TResult, TKey>
                            .RequiresRepair("incomplete-or-undecodable-key-tuple");
                    }

                    if (affected.Count > maximumAffectedKeys)
                    {
                        return ContinuousGraphIncrementalResult<TResult, TKey>
                            .RequiresRepair($"affected-key-limit:{affected.Count}");
                    }
                }
            }
        }

        if (!relevant)
        {
            return ContinuousGraphIncrementalResult<TResult, TKey>
                .Unrelated("no-graph-table-dependency");
        }

        if (affected.Count == 0)
        {
            return ContinuousGraphIncrementalResult<TResult, TKey>
                .RequiresRepair("relevant-change-without-provable-key");
        }

        var keys = affected.ToArray();
        var rowsForKeys = await executeScopedAsync(
            keys,
            context,
            cancellationToken).ConfigureAwait(false);
        return ContinuousGraphIncrementalResult<TResult, TKey>.Exact(
            keys,
            rowsForKeys,
            "compiler-generated-key-scope");
    }

    private bool IsRelevant(ChangeTable table) => _tableImpacts.ContainsKey((table.Schema, table.Name));

    private static bool TryAddKeys(
        ChangeRow row,
        IReadOnlyList<string> keyColumns,
        HashSet<TKey> affected)
    {
        foreach (var columnName in keyColumns)
        {
            var column = row.Table.Columns.FirstOrDefault(column =>
                string.Equals(column.Name, columnName, StringComparison.Ordinal));
            if (column is null)
            {
                return false;
            }

            var value = row[column.Ordinal];
            if (value.State is not ChangeColumnState.Value)
            {
                return false;
            }

            try
            {
                affected.Add(ChangeValueDecoders.Decode<TKey>(column, value));
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or
                NotSupportedException or
                FormatException or
                OverflowException)
            {
                return false;
            }
        }

        return true;
    }
}

internal static class ContinuousGraphKeyScope
{
    public static IQueryable<TResult> Apply<TResult, TKey>(
        IQueryable<TResult> query,
        Expression<Func<TResult, TKey>> keySelector,
        IReadOnlyCollection<TKey> affectedKeys)
        where TKey : notnull
    {
        BlueTuskGuard.NotNull(query);
        BlueTuskGuard.NotNull(keySelector);
        BlueTuskGuard.NotNull(affectedKeys);
        if (affectedKeys.Count == 0)
        {
            throw new ArgumentException(
                "An authoritative key scope cannot be empty.",
                nameof(affectedKeys));
        }

        var operations = new Stack<MethodCallExpression>();
        var source = query.Expression;
        while (source is MethodCallExpression call && IsTrailingOperation(call))
        {
            operations.Push(call);
            source = call.Arguments[0];
        }

        var predicate = Expression.Lambda<Func<TResult, bool>>(
            CreateKeyFilter<TKey>(keySelector.Body, affectedKeys),
            keySelector.Parameters);
        source = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.Where),
            [typeof(TResult)],
            source,
            Expression.Quote(predicate));

        while (operations.TryPop(out var operation))
        {
            var arguments = operation.Arguments.ToArray();
            arguments[0] = source;
            source = Expression.Call(operation.Method, arguments);
        }

        return query.Provider.CreateQuery<TResult>(source);
    }

    private static MethodCallExpression CreateKeyFilter<TKey>(Expression key, IReadOnlyCollection<TKey> affectedKeys)
    {
        // Use scalar equality for the common one-key delta. EF can reuse the
        // parameterized statement and PostgreSQL can use the endpoint index directly.
        if (affectedKeys.Count == 1)
        {
            var value = affectedKeys.First();
            Expression<Func<TKey>> parameter = () => value;
            return CreateScalarKeyFilter(key, parameter.Body);
        }

        var keys = affectedKeys.ToArray();
        Expression<Func<TKey[]>> keyParameter = () => keys;
        return Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Contains),
            [typeof(TKey)],
            keyParameter.Body,
            key);
    }

    internal static MethodCallExpression CreateScalarKeyFilter(Expression key, Expression value) =>
        Expression.Call(
            typeof(object),
            nameof(object.Equals),
            Type.EmptyTypes,
            Expression.Convert(key, typeof(object)),
            Expression.Convert(value, typeof(object)));

    private static bool IsTrailingOperation(MethodCallExpression call) =>
        (call.Method.DeclaringType == typeof(Queryable) &&
        call.Method.Name is
            nameof(Queryable.OrderBy) or
            nameof(Queryable.OrderByDescending) or
            nameof(Queryable.ThenBy) or
            nameof(Queryable.ThenByDescending) or
            nameof(Queryable.Take)) ||
        (call.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions) &&
            call.Method.Name == nameof(EntityFrameworkQueryableExtensions.AsNoTracking));
}
