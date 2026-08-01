using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskPostgreSqlFunctionTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource)
    : IMethodCallTranslator
{
    private static readonly Dictionary<string, string> Functions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(BlueTuskDbFunctionsExtensions.ArrayLength)] = "array_length",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayLowerBound)] = "array_lower",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayUpperBound)] = "array_upper",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayCardinality)] = "cardinality",
            [nameof(BlueTuskDbFunctionsExtensions.RangeLower)] = "lower",
            [nameof(BlueTuskDbFunctionsExtensions.RangeUpper)] = "upper",
            [nameof(BlueTuskDbFunctionsExtensions.RangeIsEmpty)] = "isempty",
            [nameof(BlueTuskDbFunctionsExtensions.RangeIsLowerInclusive)] = "lower_inc",
            [nameof(BlueTuskDbFunctionsExtensions.RangeIsUpperInclusive)] = "upper_inc",
            [nameof(BlueTuskDbFunctionsExtensions.RangeIsLowerInfinite)] = "lower_inf",
            [nameof(BlueTuskDbFunctionsExtensions.RangeIsUpperInfinite)] = "upper_inf",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeLower)] = "lower",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeUpper)] = "upper",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeIsEmpty)] = "isempty",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeIsLowerInclusive)] = "lower_inc",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeIsUpperInclusive)] = "upper_inc",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeIsLowerInfinite)] = "lower_inf",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeIsUpperInfinite)] = "upper_inf",
            [nameof(BlueTuskDbFunctionsExtensions.JsonTypeOf)] = "jsonb_typeof",
            [nameof(BlueTuskDbFunctionsExtensions.JsonArrayLength)] = "jsonb_array_length",
            [nameof(BlueTuskDbFunctionsExtensions.JsonPathQueryFirst)] = "jsonb_path_query_first",
            [nameof(BlueTuskDbFunctionsExtensions.RegexReplace)] = "regexp_replace",
            [nameof(BlueTuskDbFunctionsExtensions.RegexCount)] = "regexp_count",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkHost)] = "host",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkAddressFamily)] = "family",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkMaskLength)] = "masklen",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkPart)] = "network",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkBroadcast)] = "broadcast",
            [nameof(BlueTuskDbFunctionsExtensions.ToTextSearchVector)] = "to_tsvector",
            [nameof(BlueTuskDbFunctionsExtensions.ToTextSearchQuery)] = "to_tsquery",
            [nameof(BlueTuskDbFunctionsExtensions.PlainToTextSearchQuery)] = "plainto_tsquery",
            [nameof(BlueTuskDbFunctionsExtensions.PhraseToTextSearchQuery)] = "phraseto_tsquery",
            [nameof(BlueTuskDbFunctionsExtensions.WebSearchToTextSearchQuery)] = "websearch_to_tsquery",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchVectorLength)] = "length",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchQueryNodeCount)] = "numnode",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchRank)] = "ts_rank",
            [nameof(BlueTuskDbFunctionsExtensions.DatePart)] = "date_part",
            [nameof(BlueTuskDbFunctionsExtensions.DateTrunc)] = "date_trunc",
            [nameof(BlueTuskDbFunctionsExtensions.DateBin)] = "date_bin",
            [nameof(BlueTuskDbFunctionsExtensions.DateAge)] = "age",
            [nameof(BlueTuskDbFunctionsExtensions.MakeDate)] = "make_date",
            [nameof(BlueTuskDbFunctionsExtensions.MakeTime)] = "make_time",
            [nameof(BlueTuskDbFunctionsExtensions.MakeTimestamp)] = "make_timestamp",
            [nameof(BlueTuskDbFunctionsExtensions.MakeTimestampWithTimeZone)] = "make_timestamptz",
            [nameof(BlueTuskDbFunctionsExtensions.MakeInterval)] = "make_interval",
            [nameof(BlueTuskDbFunctionsExtensions.JustifyDays)] = "justify_days",
            [nameof(BlueTuskDbFunctionsExtensions.JustifyHours)] = "justify_hours",
            [nameof(BlueTuskDbFunctionsExtensions.JustifyInterval)] = "justify_interval",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryArea)] = "area",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryCenter)] = "center",
            [nameof(BlueTuskDbFunctionsExtensions.BoxDiagonal)] = "diagonal",
            [nameof(BlueTuskDbFunctionsExtensions.CircleDiameter)] = "diameter",
            [nameof(BlueTuskDbFunctionsExtensions.BoxHeight)] = "height",
            [nameof(BlueTuskDbFunctionsExtensions.PathIsClosed)] = "isclosed",
            [nameof(BlueTuskDbFunctionsExtensions.PathIsOpen)] = "isopen",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryLength)] = "length",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryPointCount)] = "npoints",
            [nameof(BlueTuskDbFunctionsExtensions.PathClose)] = "pclose",
            [nameof(BlueTuskDbFunctionsExtensions.PathOpen)] = "popen",
            [nameof(BlueTuskDbFunctionsExtensions.CircleRadius)] = "radius",
            [nameof(BlueTuskDbFunctionsExtensions.PointSlope)] = "slope",
            [nameof(BlueTuskDbFunctionsExtensions.BoxWidth)] = "width",
        };

    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(BlueTuskDbFunctionsExtensions) ||
            !Functions.TryGetValue(method.Name, out var functionName))
        {
            return null;
        }

        var functionArguments = arguments
            .Skip(1)
            .Select(argument => sqlExpressionFactory.ApplyDefaultTypeMapping(argument)!)
            .ToArray();
        var resultType = Nullable.GetUnderlyingType(method.ReturnType) ?? method.ReturnType;
        var resultMapping = method.Name switch
        {
            nameof(BlueTuskDbFunctionsExtensions.JsonPathQueryFirst) =>
                typeMappingSource.FindMapping("jsonb"),
            nameof(BlueTuskDbFunctionsExtensions.NetworkPart) =>
                typeMappingSource.FindMapping("cidr"),
            _ => typeMappingSource.FindMapping(resultType),
        };

        return sqlExpressionFactory.Function(
            functionName,
            functionArguments,
            nullable: true,
            argumentsPropagateNullability: Enumerable.Repeat(true, functionArguments.Length),
            resultType,
            resultMapping);
    }
}
