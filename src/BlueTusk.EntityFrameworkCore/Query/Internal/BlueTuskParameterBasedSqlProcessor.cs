using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskParameterBasedSqlProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters)
    : RelationalParameterBasedSqlProcessor(dependencies, parameters)
{
    protected override Expression ProcessSqlNullability(
        Expression selectExpression,
        ParametersCacheDecorator parametersDecorator)
        => new BlueTuskSqlNullabilityProcessor(Dependencies, Parameters)
            .Process(selectExpression, parametersDecorator);
}

internal sealed class BlueTuskSqlNullabilityProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    RelationalParameterBasedSqlProcessorParameters parameters)
    : SqlNullabilityProcessor(dependencies, parameters)
{
    protected override SqlExpression VisitCustomSqlExpression(
        SqlExpression sqlExpression,
        bool allowOptimizedExpansion,
        out bool nullable)
    {
        if (sqlExpression is BlueTuskAggregateExpression aggregate)
        {
            var arguments = aggregate.Arguments
                .Select(argument => Visit(argument, allowOptimizedExpansion, out _))
                .ToArray();
            var orderings = aggregate.Orderings
                .Select(ordering => ordering.Update(
                    Visit(ordering.Expression, allowOptimizedExpansion, out _)))
                .ToArray();
            var predicate = aggregate.Predicate is null
                ? null
                : Visit(aggregate.Predicate, allowOptimizedExpansion, out _);
            nullable = true;
            return aggregate.Update(arguments, orderings, predicate);
        }

        if (sqlExpression is not BlueTuskBinaryExpression binary)
        {
            return base.VisitCustomSqlExpression(sqlExpression, allowOptimizedExpansion, out nullable);
        }

        var left = Visit(binary.Left, allowOptimizedExpansion, out var leftNullable);
        var right = Visit(binary.Right, allowOptimizedExpansion, out var rightNullable);
        nullable = leftNullable || rightNullable;
        return binary.Update(left, right);
    }
}
