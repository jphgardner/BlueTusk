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
            [nameof(BlueTuskDbFunctionsExtensions.JsonObjectAggregate)] = "json_object_agg",
            [nameof(BlueTuskDbFunctionsExtensions.JsonbObjectAggregate)] = "jsonb_object_agg",
            [nameof(BlueTuskDbFunctionsExtensions.Correlation)] = "corr",
            [nameof(BlueTuskDbFunctionsExtensions.CovariancePopulation)] = "covar_pop",
            [nameof(BlueTuskDbFunctionsExtensions.CovarianceSample)] = "covar_samp",
            [nameof(BlueTuskDbFunctionsExtensions.RegressionAverageX)] = "regr_avgx",
            [nameof(BlueTuskDbFunctionsExtensions.RegressionAverageY)] = "regr_avgy",
            [nameof(BlueTuskDbFunctionsExtensions.RegressionCount)] = "regr_count",
            [nameof(BlueTuskDbFunctionsExtensions.RegressionIntercept)] = "regr_intercept",
            [nameof(BlueTuskDbFunctionsExtensions.RegressionR2)] = "regr_r2",
            [nameof(BlueTuskDbFunctionsExtensions.RegressionSlope)] = "regr_slope",
            [nameof(BlueTuskDbFunctionsExtensions.RegressionSumSquaresX)] = "regr_sxx",
            [nameof(BlueTuskDbFunctionsExtensions.RegressionSumProducts)] = "regr_sxy",
            [nameof(BlueTuskDbFunctionsExtensions.RegressionSumSquaresY)] = "regr_syy",
            [nameof(BlueTuskDbFunctionsExtensions.Mode)] = "mode",
            [nameof(BlueTuskDbFunctionsExtensions.PercentileContinuous)] = "percentile_cont",
            [nameof(BlueTuskDbFunctionsExtensions.PercentileDiscrete)] = "percentile_disc",
            [nameof(BlueTuskDbFunctionsExtensions.HypotheticalRank)] = "rank",
            [nameof(BlueTuskDbFunctionsExtensions.HypotheticalDenseRank)] = "dense_rank",
            [nameof(BlueTuskDbFunctionsExtensions.HypotheticalPercentRank)] = "percent_rank",
            [nameof(BlueTuskDbFunctionsExtensions.HypotheticalCumulativeDistribution)] = "cume_dist",
        };

    private static readonly HashSet<string> PairFunctions =
    [
        nameof(BlueTuskDbFunctionsExtensions.JsonObjectAggregate),
        nameof(BlueTuskDbFunctionsExtensions.JsonbObjectAggregate),
        nameof(BlueTuskDbFunctionsExtensions.Correlation),
        nameof(BlueTuskDbFunctionsExtensions.CovariancePopulation),
        nameof(BlueTuskDbFunctionsExtensions.CovarianceSample),
        nameof(BlueTuskDbFunctionsExtensions.RegressionAverageX),
        nameof(BlueTuskDbFunctionsExtensions.RegressionAverageY),
        nameof(BlueTuskDbFunctionsExtensions.RegressionCount),
        nameof(BlueTuskDbFunctionsExtensions.RegressionIntercept),
        nameof(BlueTuskDbFunctionsExtensions.RegressionR2),
        nameof(BlueTuskDbFunctionsExtensions.RegressionSlope),
        nameof(BlueTuskDbFunctionsExtensions.RegressionSumSquaresX),
        nameof(BlueTuskDbFunctionsExtensions.RegressionSumProducts),
        nameof(BlueTuskDbFunctionsExtensions.RegressionSumSquaresY),
    ];

    public SqlExpression? Translate(
        MethodInfo method,
        EnumerableExpression source,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(BlueTuskDbFunctionsExtensions)
            || !Functions.TryGetValue(method.Name, out var functionName))
        {
            return null;
        }

        if (method.Name is nameof(BlueTuskDbFunctionsExtensions.Mode)
            or nameof(BlueTuskDbFunctionsExtensions.PercentileContinuous)
            or nameof(BlueTuskDbFunctionsExtensions.PercentileDiscrete)
            or nameof(BlueTuskDbFunctionsExtensions.HypotheticalRank)
            or nameof(BlueTuskDbFunctionsExtensions.HypotheticalDenseRank)
            or nameof(BlueTuskDbFunctionsExtensions.HypotheticalPercentRank)
            or nameof(BlueTuskDbFunctionsExtensions.HypotheticalCumulativeDistribution))
        {
            return TranslateOrderedSet(method, source, arguments, functionName);
        }

        var aggregateArguments = PairFunctions.Contains(method.Name)
            ? source.Selector is BlueTuskRowValueExpression { Values.Count: 2 } row
                ? row.Values.Select(value => sqlExpressionFactory.ApplyDefaultTypeMapping(value)!).ToArray()
                : null
            : source.Selector is not SqlExpression selector
                ? null
                : method.Name == nameof(BlueTuskDbFunctionsExtensions.StringAggregate)
            && arguments is [_, var delimiter]
                ? new[]
                {
                    sqlExpressionFactory.ApplyDefaultTypeMapping(selector)!,
                    sqlExpressionFactory.ApplyDefaultTypeMapping(delimiter)!,
                }
                : arguments.Count == 1
                    ? [sqlExpressionFactory.ApplyDefaultTypeMapping(selector)!]
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
            nameof(BlueTuskDbFunctionsExtensions.JsonObjectAggregate) =>
                typeMappingSource.FindMapping("json"),
            nameof(BlueTuskDbFunctionsExtensions.JsonbObjectAggregate) =>
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
            [],
            source.Predicate,
            resultType,
            resultMapping);
    }

    private BlueTuskAggregateExpression? TranslateOrderedSet(
        MethodInfo method,
        EnumerableExpression source,
        IReadOnlyList<SqlExpression> arguments,
        string functionName)
    {
        if (source.Selector is not SqlExpression selector)
        {
            return null;
        }

        if (source.IsDistinct)
        {
            throw new InvalidOperationException(
                "PostgreSQL ordered-set aggregates do not accept DISTINCT input.");
        }

        selector = sqlExpressionFactory.ApplyDefaultTypeMapping(selector)!;
        IReadOnlyList<SqlExpression>? directArguments = method.Name == nameof(BlueTuskDbFunctionsExtensions.Mode)
            && arguments.Count == 1
                ? []
                : arguments is [_, var fraction]
                    ? [sqlExpressionFactory.ApplyDefaultTypeMapping(fraction)!]
                    : null;
        if (directArguments is null)
        {
            return null;
        }

        var resultType = Nullable.GetUnderlyingType(method.ReturnType) ?? method.ReturnType;
        var resultMapping = typeMappingSource.FindMapping(resultType);
        return resultMapping is null
            ? null
            : new BlueTuskAggregateExpression(
                functionName,
                directArguments,
                isDistinct: false,
                orderings: [],
                withinGroupOrderings: [new OrderingExpression(selector, ascending: true)],
                source.Predicate,
                resultType,
                resultMapping);
    }
}
