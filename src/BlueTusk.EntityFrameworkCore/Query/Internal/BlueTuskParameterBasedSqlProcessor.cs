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
            var withinGroupOrderings = aggregate.WithinGroupOrderings
                .Select(ordering => ordering.Update(
                    Visit(ordering.Expression, allowOptimizedExpansion, out _)))
                .ToArray();
            var predicate = aggregate.Predicate is null
                ? null
                : Visit(aggregate.Predicate, allowOptimizedExpansion, out _);
            nullable = true;
            return aggregate.Update(arguments, orderings, withinGroupOrderings, predicate);
        }

        if (sqlExpression is BlueTuskWindowFunctionExpression window)
        {
            var arguments = window.Arguments
                .Select(argument => Visit(argument, allowOptimizedExpansion, out _))
                .ToArray();
            var partitions = window.Partitions
                .Select(partition => Visit(partition, allowOptimizedExpansion, out _))
                .ToArray();
            var orderings = window.Orderings
                .Select(ordering => ordering.Update(
                    Visit(ordering.Expression, allowOptimizedExpansion, out _)))
                .ToArray();
            nullable = window.Name is "lag" or "lead" or "first_value" or "last_value" or "nth_value";
            return window.Update(arguments, partitions, orderings);
        }

        if (sqlExpression is BlueTuskQuantifiedComparisonExpression quantifiedComparison)
        {
            var item = Visit(
                quantifiedComparison.Item,
                allowOptimizedExpansion,
                out var itemNullable);
            var array = Visit(
                quantifiedComparison.Array,
                allowOptimizedExpansion,
                out var arrayNullable);
            var elementType = array.Type.GetElementType();
            var elementNullable = elementType is not null
                && (!elementType.IsValueType || Nullable.GetUnderlyingType(elementType) is not null);
            nullable = itemNullable || arrayNullable || elementNullable;
            return quantifiedComparison.Update(item, array);
        }

        if (sqlExpression is BlueTuskRowValueExpression rowValue)
        {
            var nullableValue = false;
            var values = rowValue.Values
                .Select(value =>
                {
                    var visited = Visit(value, allowOptimizedExpansion, out var valueNullable);
                    nullableValue |= valueNullable;
                    return visited;
                })
                .ToArray();
            nullable = nullableValue;
            return rowValue.Update(values);
        }

        if (sqlExpression is BlueTuskUnaryExpression unary)
        {
            var operand = Visit(unary.Operand, allowOptimizedExpansion, out var operandNullable);
            nullable = operandNullable;
            return unary.Update(operand);
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
