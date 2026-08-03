using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskSqlTranslatingExpressionVisitor(
    RelationalSqlTranslatingExpressionVisitorDependencies dependencies,
    QueryCompilationContext queryCompilationContext,
    QueryableMethodTranslatingExpressionVisitor queryableMethodTranslatingExpressionVisitor)
    : RelationalSqlTranslatingExpressionVisitor(
        dependencies,
        queryCompilationContext,
        queryableMethodTranslatingExpressionVisitor)
{
    protected override Expression VisitUnary(UnaryExpression unaryExpression)
    {
        if (unaryExpression is { NodeType: ExpressionType.Convert, Type: var type }
            && type == typeof(ITuple)
            && unaryExpression.Operand.Type.IsAssignableTo(typeof(ITuple)))
        {
            return Visit(unaryExpression.Operand);
        }

        return base.VisitUnary(unaryExpression);
    }
}
