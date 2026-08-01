using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
    : QuerySqlGenerator(dependencies)
{
    protected override Expression VisitExtension(Expression extensionExpression)
    {
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
