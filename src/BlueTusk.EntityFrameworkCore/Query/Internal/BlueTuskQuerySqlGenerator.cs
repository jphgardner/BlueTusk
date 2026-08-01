using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
    : QuerySqlGenerator(dependencies)
{
    protected override Expression VisitExtension(Expression extensionExpression)
    {
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
