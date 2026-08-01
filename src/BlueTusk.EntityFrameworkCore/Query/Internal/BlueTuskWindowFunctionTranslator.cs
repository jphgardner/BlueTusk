using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskWindowFunctionTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource)
    : IMethodCallTranslator
{
    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(BlueTuskDbFunctionsExtensions))
        {
            return null;
        }

        var rawArguments = arguments.Skip(1).ToArray();
        if (method.Name == nameof(BlueTuskDbFunctionsExtensions.WindowDescending))
        {
            return new BlueTuskWindowOrderingExpression(
                sqlExpressionFactory.ApplyDefaultTypeMapping(rawArguments[0])!,
                isAscending: false);
        }

        if (!TryGetShape(method.Name, rawArguments.Length, out var shape))
        {
            return null;
        }

        var mappedArguments = rawArguments
            .Select(argument => sqlExpressionFactory.ApplyDefaultTypeMapping(argument)!)
            .ToArray();
        SqlExpression[] partitions = shape.PartitionIndex is { } partitionIndex
            ? [UnwrapOrdering(mappedArguments[partitionIndex]).Expression]
            : [];
        var ordering = UnwrapOrdering(mappedArguments[shape.OrderIndex]);
        var functionArguments = shape.ArgumentIndexes
            .Select(index => UnwrapOrdering(mappedArguments[index]).Expression)
            .ToArray();
        var resultType = method.ReturnType;
        var mappingType = Nullable.GetUnderlyingType(resultType) ?? resultType;
        var resultMapping = shape.UsesValueMapping
            ? functionArguments[0].TypeMapping
            : typeMappingSource.FindMapping(mappingType);
        if (resultMapping is null)
        {
            return null;
        }

        return new BlueTuskWindowFunctionExpression(
            shape.SqlName,
            functionArguments,
            partitions,
            [new OrderingExpression(ordering.Expression, ordering.IsAscending)],
            resultType,
            resultMapping);
    }

    private static (SqlExpression Expression, bool IsAscending) UnwrapOrdering(
        SqlExpression expression)
        => expression is BlueTuskWindowOrderingExpression ordering
            ? (ordering.Operand, ordering.IsAscending)
            : (expression, true);

    private static bool TryGetShape(
        string methodName,
        int argumentCount,
        out WindowFunctionShape shape)
    {
        var isPartitioned = methodName switch
        {
            nameof(BlueTuskDbFunctionsExtensions.WindowRowNumber)
                or nameof(BlueTuskDbFunctionsExtensions.WindowRank)
                or nameof(BlueTuskDbFunctionsExtensions.WindowDenseRank)
                or nameof(BlueTuskDbFunctionsExtensions.WindowPercentRank)
                or nameof(BlueTuskDbFunctionsExtensions.WindowCumulativeDistribution) =>
                argumentCount == 2,
            nameof(BlueTuskDbFunctionsExtensions.WindowNtile)
                or nameof(BlueTuskDbFunctionsExtensions.WindowFirstValue)
                or nameof(BlueTuskDbFunctionsExtensions.WindowLastValue) =>
                argumentCount == 3,
            nameof(BlueTuskDbFunctionsExtensions.WindowNthValue) => argumentCount == 4,
            nameof(BlueTuskDbFunctionsExtensions.WindowLag)
                or nameof(BlueTuskDbFunctionsExtensions.WindowLead) => argumentCount == 5,
            _ => false,
        };
        var sqlName = methodName switch
        {
            nameof(BlueTuskDbFunctionsExtensions.WindowRowNumber) => "row_number",
            nameof(BlueTuskDbFunctionsExtensions.WindowRank) => "rank",
            nameof(BlueTuskDbFunctionsExtensions.WindowDenseRank) => "dense_rank",
            nameof(BlueTuskDbFunctionsExtensions.WindowPercentRank) => "percent_rank",
            nameof(BlueTuskDbFunctionsExtensions.WindowCumulativeDistribution) => "cume_dist",
            nameof(BlueTuskDbFunctionsExtensions.WindowNtile) => "ntile",
            nameof(BlueTuskDbFunctionsExtensions.WindowLag) => "lag",
            nameof(BlueTuskDbFunctionsExtensions.WindowLead) => "lead",
            nameof(BlueTuskDbFunctionsExtensions.WindowFirstValue) => "first_value",
            nameof(BlueTuskDbFunctionsExtensions.WindowLastValue) => "last_value",
            nameof(BlueTuskDbFunctionsExtensions.WindowNthValue) => "nth_value",
            _ => null,
        };
        if (sqlName is null)
        {
            shape = default;
            return false;
        }

        var functionArgumentCount = methodName switch
        {
            nameof(BlueTuskDbFunctionsExtensions.WindowRowNumber)
                or nameof(BlueTuskDbFunctionsExtensions.WindowRank)
                or nameof(BlueTuskDbFunctionsExtensions.WindowDenseRank)
                or nameof(BlueTuskDbFunctionsExtensions.WindowPercentRank)
                or nameof(BlueTuskDbFunctionsExtensions.WindowCumulativeDistribution) => 0,
            nameof(BlueTuskDbFunctionsExtensions.WindowNtile)
                or nameof(BlueTuskDbFunctionsExtensions.WindowFirstValue)
                or nameof(BlueTuskDbFunctionsExtensions.WindowLastValue) => 1,
            nameof(BlueTuskDbFunctionsExtensions.WindowNthValue) => 2,
            _ => 3,
        };
        var orderIndex = argumentCount - 1;
        var partitionIndex = isPartitioned ? orderIndex - 1 : (int?)null;
        shape = new WindowFunctionShape(
            sqlName,
            Enumerable.Range(0, functionArgumentCount).ToArray(),
            partitionIndex,
            orderIndex,
            UsesValueMapping: methodName is nameof(BlueTuskDbFunctionsExtensions.WindowLag)
                or nameof(BlueTuskDbFunctionsExtensions.WindowLead)
                or nameof(BlueTuskDbFunctionsExtensions.WindowFirstValue)
                or nameof(BlueTuskDbFunctionsExtensions.WindowLastValue)
                or nameof(BlueTuskDbFunctionsExtensions.WindowNthValue));
        return true;
    }

    private readonly record struct WindowFunctionShape(
        string SqlName,
        IReadOnlyList<int> ArgumentIndexes,
        int? PartitionIndex,
        int OrderIndex,
        bool UsesValueMapping);
}
