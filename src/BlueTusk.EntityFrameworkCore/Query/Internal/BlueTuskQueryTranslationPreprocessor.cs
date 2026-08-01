using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQueryTranslationPreprocessor(
    QueryTranslationPreprocessorDependencies dependencies,
    RelationalQueryTranslationPreprocessorDependencies relationalDependencies,
    QueryCompilationContext queryCompilationContext)
    : RelationalQueryTranslationPreprocessor(
        dependencies,
        relationalDependencies,
        queryCompilationContext)
{
    protected override Expression ProcessQueryRoots(Expression expression)
        => new GenerateSeriesQueryRootRewritingExpressionVisitor().Visit(
            base.ProcessQueryRoots(expression));

    private sealed class GenerateSeriesQueryRootRewritingExpressionVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskDbFunctionsExtensions)
                && methodCallExpression.Method.Name == nameof(BlueTuskDbFunctionsExtensions.GenerateSeries))
            {
                return new BlueTuskGenerateSeriesQueryRootExpression(
                    methodCallExpression.Method.ReturnType.GetGenericArguments()[0],
                    methodCallExpression.Arguments.Skip(1).Select(argument => Visit(argument)!).ToArray());
            }

            return base.VisitMethodCall(methodCallExpression);
        }
    }
}
