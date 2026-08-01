using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskPostgreSqlAggregateTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource)
    : IAggregateMethodCallTranslator
{
    private static readonly Dictionary<string, string> Functions =
        new(StringComparer.Ordinal)
        {
            [nameof(BlueTuskDbFunctionsExtensions.ArrayAggregate)] = "array_agg",
            [nameof(BlueTuskDbFunctionsExtensions.StringAggregate)] = "string_agg",
            [nameof(BlueTuskDbFunctionsExtensions.BooleanAnd)] = "bool_and",
            [nameof(BlueTuskDbFunctionsExtensions.BooleanOr)] = "bool_or",
            [nameof(BlueTuskDbFunctionsExtensions.RangeAggregate)] = "range_agg",
            [nameof(BlueTuskDbFunctionsExtensions.RangeIntersectAggregate)] = "range_intersect_agg",
            [nameof(BlueTuskDbFunctionsExtensions.JsonAggregate)] = "json_agg",
            [nameof(BlueTuskDbFunctionsExtensions.JsonbAggregate)] = "jsonb_agg",
            [nameof(BlueTuskDbFunctionsExtensions.XmlAggregate)] = "xmlagg",
            [nameof(BlueTuskDbFunctionsExtensions.IntegerBitAnd)] = "bit_and",
            [nameof(BlueTuskDbFunctionsExtensions.IntegerBitOr)] = "bit_or",
            [nameof(BlueTuskDbFunctionsExtensions.IntegerBitXor)] = "bit_xor",
            [nameof(BlueTuskDbFunctionsExtensions.BigIntBitAnd)] = "bit_and",
            [nameof(BlueTuskDbFunctionsExtensions.BigIntBitOr)] = "bit_or",
            [nameof(BlueTuskDbFunctionsExtensions.BigIntBitXor)] = "bit_xor",
            [nameof(BlueTuskDbFunctionsExtensions.StandardDeviationPopulation)] = "stddev_pop",
            [nameof(BlueTuskDbFunctionsExtensions.StandardDeviationSample)] = "stddev_samp",
            [nameof(BlueTuskDbFunctionsExtensions.VariancePopulation)] = "var_pop",
            [nameof(BlueTuskDbFunctionsExtensions.VarianceSample)] = "var_samp",
        };

    public SqlExpression? Translate(
        MethodInfo method,
        EnumerableExpression source,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(BlueTuskDbFunctionsExtensions)
            || source.Selector is not SqlExpression selector
            || !Functions.TryGetValue(method.Name, out var functionName))
        {
            return null;
        }

        selector = sqlExpressionFactory.ApplyDefaultTypeMapping(selector)!;
        var aggregateArguments = method.Name == nameof(BlueTuskDbFunctionsExtensions.StringAggregate)
            && arguments is [_, var delimiter]
                ? new[]
                {
                    selector,
                    sqlExpressionFactory.ApplyDefaultTypeMapping(delimiter)!,
                }
                : arguments.Count == 1
                    ? [selector]
                    : null;
        if (aggregateArguments is null)
        {
            return null;
        }

        var resultType = Nullable.GetUnderlyingType(method.ReturnType) ?? method.ReturnType;
        var resultMapping = method.Name switch
        {
            nameof(BlueTuskDbFunctionsExtensions.StringAggregate) =>
                typeMappingSource.FindMapping("text"),
            nameof(BlueTuskDbFunctionsExtensions.JsonAggregate) =>
                typeMappingSource.FindMapping("json"),
            nameof(BlueTuskDbFunctionsExtensions.JsonbAggregate) =>
                typeMappingSource.FindMapping("jsonb"),
            nameof(BlueTuskDbFunctionsExtensions.XmlAggregate) =>
                typeMappingSource.FindMapping("xml"),
            _ => typeMappingSource.FindMapping(resultType),
        };
        if (resultMapping is null)
        {
            return null;
        }

        return new BlueTuskAggregateExpression(
            functionName,
            aggregateArguments,
            source.IsDistinct,
            source.Orderings,
            source.Predicate,
            resultType,
            resultMapping);
    }
}
