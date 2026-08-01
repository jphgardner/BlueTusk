using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
    : QuerySqlGenerator(dependencies)
{
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
                .Append("(")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier("value"));
            if (function.WithOrdinality)
            {
                Sql.Append(", ")
                    .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier("ordinality"));
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
            if (aggregate.Predicate is not null)
            {
                Sql.Append(" FILTER (WHERE ");
                Visit(aggregate.Predicate);
                Sql.Append(")");
            }

            return aggregate;
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

        return base.VisitExtension(extensionExpression);
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
    }
}
