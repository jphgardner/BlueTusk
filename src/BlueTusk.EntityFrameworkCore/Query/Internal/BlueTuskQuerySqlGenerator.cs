using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
    : QuerySqlGenerator(dependencies)
{
    private bool _renderingCteBody;

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        if (extensionExpression is BlueTuskUnnestExpression unnest)
        {
            Sql.Append("unnest(");
            Visit(unnest.Array);
            Sql.Append(") WITH ORDINALITY AS ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(unnest.Alias))
                .Append("(")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier("value"))
                .Append(", ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier("ordinality"))
                .Append(")");
            return unnest;
        }

        if (extensionExpression is BlueTuskSetReturningFunctionTableExpression function)
        {
            Sql.Append(function.Name).Append("(");
            for (var index = 0; index < function.Arguments.Count; index++)
            {
                if (index > 0)
                {
                    Sql.Append(", ");
                }

                Visit(function.Arguments[index]);
            }

            Sql.Append(")");
            if (function.WithOrdinality)
            {
                Sql.Append(" WITH ORDINALITY");
            }

            Sql.Append(" AS ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(function.Alias))
                .Append("(");
            for (var index = 0; index < function.ColumnNames.Count; index++)
            {
                if (index > 0)
                {
                    Sql.Append(", ");
                }

                Sql.Append(
                    Dependencies.SqlGenerationHelper.DelimitIdentifier(
                        function.ColumnNames[index]));
                if (function.ColumnStoreTypes[index] is { } storeType)
                {
                    Sql.Append(" ").Append(storeType);
                }
            }

            if (function.WithOrdinality)
            {
                if (function.ColumnNames.Count > 0)
                {
                    Sql.Append(", ");
                }

                Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier("ordinality"));
            }

            Sql.Append(")");
            return function;
        }

        if (extensionExpression is BlueTuskAggregateExpression aggregate)
        {
            Sql.Append(aggregate.Name).Append("(");
            if (aggregate.IsDistinct)
            {
                Sql.Append("DISTINCT ");
            }

            for (var index = 0; index < aggregate.Arguments.Count; index++)
            {
                if (index > 0)
                {
                    Sql.Append(", ");
                }

                Visit(aggregate.Arguments[index]);
            }

            if (aggregate.Orderings.Count > 0)
            {
                Sql.Append(" ORDER BY ");
                for (var index = 0; index < aggregate.Orderings.Count; index++)
                {
                    if (index > 0)
                    {
                        Sql.Append(", ");
                    }

                    var ordering = aggregate.Orderings[index];
                    Visit(ordering.Expression);
                    Sql.Append(ordering.IsAscending ? " ASC" : " DESC");
                }
            }

            Sql.Append(")");
            if (aggregate.WithinGroupOrderings.Count > 0)
            {
                Sql.Append(" WITHIN GROUP (ORDER BY ");
                for (var index = 0; index < aggregate.WithinGroupOrderings.Count; index++)
                {
                    if (index > 0)
                    {
                        Sql.Append(", ");
                    }

                    var ordering = aggregate.WithinGroupOrderings[index];
                    Visit(ordering.Expression);
                    Sql.Append(ordering.IsAscending ? " ASC" : " DESC");
                }

                Sql.Append(")");
            }

            if (aggregate.Predicate is not null)
            {
                Sql.Append(" FILTER (WHERE ");
                Visit(aggregate.Predicate);
                Sql.Append(")");
            }

            return aggregate;
        }

        if (extensionExpression is BlueTuskWindowFunctionExpression window)
        {
            Sql.Append(window.Name).Append("(");
            for (var index = 0; index < window.Arguments.Count; index++)
            {
                if (index > 0)
                {
                    Sql.Append(", ");
                }

                Visit(window.Arguments[index]);
            }

            Sql.Append(") OVER (");
            if (window.Partitions.Count > 0)
            {
                Sql.Append("PARTITION BY ");
                for (var index = 0; index < window.Partitions.Count; index++)
                {
                    if (index > 0)
                    {
                        Sql.Append(", ");
                    }

                    Visit(window.Partitions[index]);
                }

                Sql.Append(" ");
            }

            Sql.Append("ORDER BY ");
            for (var index = 0; index < window.Orderings.Count; index++)
            {
                if (index > 0)
                {
                    Sql.Append(", ");
                }

                Visit(window.Orderings[index].Expression);
                if (!window.Orderings[index].IsAscending)
                {
                    Sql.Append(" DESC");
                }
            }

            Sql.Append(")");
            return window;
        }

        if (extensionExpression is BlueTuskWindowOrderingExpression windowOrdering)
        {
            Visit(windowOrdering.Operand);
            return windowOrdering;
        }

        if (extensionExpression is BlueTuskQuantifiedComparisonExpression quantifiedComparison)
        {
            Sql.Append("(");
            Visit(quantifiedComparison.Item);
            Sql.Append(" ").Append(quantifiedComparison.OperatorToken).Append(" ");
            Sql.Append(
                quantifiedComparison.Quantifier == BlueTuskArrayQuantifier.Any
                    ? "ANY("
                    : "ALL(");
            Visit(quantifiedComparison.Array);
            Sql.Append("))");
            return quantifiedComparison;
        }

        if (extensionExpression is BlueTuskRowValueExpression rowValue)
        {
            Sql.Append("(");
            for (var index = 0; index < rowValue.Values.Count; index++)
            {
                if (index > 0)
                {
                    Sql.Append(", ");
                }

                Visit(rowValue.Values[index]);
            }

            Sql.Append(")");
            return rowValue;
        }

        if (extensionExpression is BlueTuskBinaryExpression binary)
        {
            Sql.Append("(");
            Visit(binary.Left);
            Sql.Append(" ").Append(binary.OperatorToken).Append(" ");
            Visit(binary.Right);
            Sql.Append(")");
            return binary;
        }

        if (extensionExpression is BlueTuskUnaryExpression unary)
        {
            Sql.Append("(").Append(unary.OperatorToken).Append(" ");
            Visit(unary.Operand);
            Sql.Append(")");
            return unary;
        }

        return base.VisitExtension(extensionExpression);
    }

    protected override Expression VisitSelect(SelectExpression selectExpression)
    {
        if (_renderingCteBody
            || GetQueryAnnotation(selectExpression, BlueTuskQueryAnnotationNames.CommonTableExpression)?.Value
                is not BlueTuskCteClause cte)
        {
            return base.VisitSelect(selectExpression);
        }

        Sql.Append("WITH ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(cte.Name))
            .Append(" AS");
        if (cte.Materialization != BlueTuskCteMaterialization.Default)
        {
            Sql.Append(cte.Materialization == BlueTuskCteMaterialization.Materialized
                ? " MATERIALIZED"
                : " NOT MATERIALIZED");
        }

        Sql.Append(" (").AppendLine();
        _renderingCteBody = true;
        try
        {
            base.VisitSelect(selectExpression);
        }
        finally
        {
            _renderingCteBody = false;
        }

        Sql.AppendLine()
            .Append(")")
            .AppendLine()
            .Append("SELECT * FROM ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(cte.Name));
        AppendCteResultOrdering(selectExpression);
        return selectExpression;
    }

    protected override Expression VisitCrossApply(CrossApplyExpression crossApplyExpression)
    {
        Sql.Append("JOIN LATERAL ");
        VisitLateralTable(crossApplyExpression.Table);
        Sql.Append(" ON TRUE");
        return crossApplyExpression;
    }

    protected override Expression VisitOuterApply(OuterApplyExpression outerApplyExpression)
    {
        Sql.Append("LEFT JOIN LATERAL ");
        VisitLateralTable(outerApplyExpression.Table);
        Sql.Append(" ON TRUE");
        return outerApplyExpression;
    }

    protected override Expression VisitTable(TableExpression tableExpression)
    {
        if (tableExpression.FindAnnotation(BlueTuskQueryAnnotationNames.TableSample)?.Value
            is not BlueTuskTableSampleClause sample)
        {
            return base.VisitTable(tableExpression);
        }

        Sql.Append(
                Dependencies.SqlGenerationHelper.DelimitIdentifier(
                    tableExpression.Name,
                    tableExpression.Schema))
            .Append(" AS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(tableExpression.Alias))
            .Append(" TABLESAMPLE ")
            .Append(sample.Method == BlueTuskTableSampleMethod.System ? "SYSTEM" : "BERNOULLI")
            .Append(" (");
        Visit(sample.Percentage);
        Sql.Append(")");
        if (sample.Repeatable is not null)
        {
            Sql.Append(" REPEATABLE (");
            Visit(sample.Repeatable);
            Sql.Append(")");
        }

        return tableExpression;
    }

    private void VisitLateralTable(TableExpressionBase tableExpression)
    {
        if (tableExpression is TableExpression table)
        {
            Sql.Append("(SELECT * FROM ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(table.Name, table.Schema))
                .Append(") AS ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(table.Alias));
            return;
        }

        Visit(tableExpression);
    }

    protected override void GenerateLimitOffset(SelectExpression selectExpression)
    {
        if (selectExpression.Limit is not null)
        {
            Sql.AppendLine().Append("LIMIT ");
            Visit(selectExpression.Limit);
        }

        if (selectExpression.Offset is not null)
        {
            if (selectExpression.Limit is null)
            {
                Sql.AppendLine();
            }

            Sql.Append(" OFFSET ");
            Visit(selectExpression.Offset);
        }

        if (GetQueryAnnotation(selectExpression, BlueTuskQueryAnnotationNames.RowLocking)?.Value
            is BlueTuskRowLockingClause locking)
        {
            Sql.AppendLine().Append(locking.Strength switch
            {
                BlueTuskRowLockingStrength.Update => "FOR UPDATE",
                BlueTuskRowLockingStrength.NoKeyUpdate => "FOR NO KEY UPDATE",
                BlueTuskRowLockingStrength.Share => "FOR SHARE",
                BlueTuskRowLockingStrength.KeyShare => "FOR KEY SHARE",
                _ => throw new InvalidOperationException("Unknown PostgreSQL row-locking strength."),
            });
            if (locking.Behavior != BlueTuskRowLockingBehavior.Wait)
            {
                Sql.Append(locking.Behavior == BlueTuskRowLockingBehavior.NoWait
                    ? " NOWAIT"
                    : " SKIP LOCKED");
            }
        }
    }

    protected override void GenerateTop(SelectExpression selectExpression)
    {
        base.GenerateTop(selectExpression);
        if (GetQueryAnnotation(selectExpression, BlueTuskQueryAnnotationNames.DistinctOn)?.Value
            is SqlExpression key)
        {
            Sql.Append("DISTINCT ON (");
            Visit(key);
            Sql.Append(") ");
        }
    }

    private static Microsoft.EntityFrameworkCore.Infrastructure.IAnnotation? GetQueryAnnotation(
        SelectExpression selectExpression,
        string name)
        => selectExpression.Tables.Count == 0
            ? null
            : selectExpression.Tables[0].FindAnnotation(name);

    private void AppendCteResultOrdering(SelectExpression selectExpression)
    {
        if (selectExpression.Orderings.Count == 0)
        {
            return;
        }

        var positions = new int[selectExpression.Orderings.Count];
        for (var orderingIndex = 0; orderingIndex < selectExpression.Orderings.Count; orderingIndex++)
        {
            var ordering = selectExpression.Orderings[orderingIndex];
            var projectionIndex = selectExpression.Projection
                .Select((projection, index) => (projection, index))
                .FirstOrDefault(item => item.projection.Expression.Equals(ordering.Expression))
                .index;
            if (projectionIndex < 0
                || projectionIndex >= selectExpression.Projection.Count
                || !selectExpression.Projection[projectionIndex].Expression.Equals(ordering.Expression))
            {
                throw new InvalidOperationException(
                    "An ordered PostgreSQL CTE must project every ORDER BY expression so the result order can be preserved.");
            }

            positions[orderingIndex] = projectionIndex + 1;
        }

        Sql.AppendLine().Append("ORDER BY ");
        for (var index = 0; index < positions.Length; index++)
        {
            if (index > 0)
            {
                Sql.Append(", ");
            }

            Sql.Append(positions[index].ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!selectExpression.Orderings[index].IsAscending)
            {
                Sql.Append(" DESC");
            }
        }
    }
}
