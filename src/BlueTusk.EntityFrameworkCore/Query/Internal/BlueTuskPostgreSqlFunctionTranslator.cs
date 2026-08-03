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
            [nameof(BlueTuskDbFunctionsExtensions.ArrayDimensions)] = "array_dims",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayDimensionCount)] = "array_ndims",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayPosition)] = "array_position",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayPositions)] = "array_positions",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayRemove)] = "array_remove",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayReplace)] = "array_replace",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayReverse)] = "array_reverse",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayShuffle)] = "array_shuffle",
            [nameof(BlueTuskDbFunctionsExtensions.ArraySample)] = "array_sample",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayTrim)] = "trim_array",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayToString)] = "array_to_string",
            [nameof(BlueTuskDbFunctionsExtensions.StringToArray)] = "string_to_array",
            [nameof(BlueTuskDbFunctionsExtensions.StringAscii)] = "ascii",
            [nameof(BlueTuskDbFunctionsExtensions.StringCharacter)] = "chr",
            [nameof(BlueTuskDbFunctionsExtensions.BitLength)] = "bit_length",
            [nameof(BlueTuskDbFunctionsExtensions.ByteLength)] = "octet_length",
            [nameof(BlueTuskDbFunctionsExtensions.StringInitialCapital)] = "initcap",
            [nameof(BlueTuskDbFunctionsExtensions.StringLeft)] = "left",
            [nameof(BlueTuskDbFunctionsExtensions.StringRight)] = "right",
            [nameof(BlueTuskDbFunctionsExtensions.StringPadLeft)] = "lpad",
            [nameof(BlueTuskDbFunctionsExtensions.StringPadRight)] = "rpad",
            [nameof(BlueTuskDbFunctionsExtensions.StringTrimLeft)] = "ltrim",
            [nameof(BlueTuskDbFunctionsExtensions.StringTrimRight)] = "rtrim",
            [nameof(BlueTuskDbFunctionsExtensions.StringTrim)] = "btrim",
            [nameof(BlueTuskDbFunctionsExtensions.Md5)] = "md5",
            [nameof(BlueTuskDbFunctionsExtensions.ParseIdentifier)] = "parse_ident",
            [nameof(BlueTuskDbFunctionsExtensions.QuoteIdentifier)] = "quote_ident",
            [nameof(BlueTuskDbFunctionsExtensions.QuoteLiteral)] = "quote_literal",
            [nameof(BlueTuskDbFunctionsExtensions.QuoteNullableLiteral)] = "quote_nullable",
            [nameof(BlueTuskDbFunctionsExtensions.StringRepeat)] = "repeat",
            [nameof(BlueTuskDbFunctionsExtensions.StringReverse)] = "reverse",
            [nameof(BlueTuskDbFunctionsExtensions.StringSplitPart)] = "split_part",
            [nameof(BlueTuskDbFunctionsExtensions.StringStartsWith)] = "starts_with",
            [nameof(BlueTuskDbFunctionsExtensions.StringTranslate)] = "translate",
            [nameof(BlueTuskDbFunctionsExtensions.BinaryEncode)] = "encode",
            [nameof(BlueTuskDbFunctionsExtensions.BinaryDecode)] = "decode",
            [nameof(BlueTuskDbFunctionsExtensions.BinaryGetByte)] = "get_byte",
            [nameof(BlueTuskDbFunctionsExtensions.BinarySetByte)] = "set_byte",
            [nameof(BlueTuskDbFunctionsExtensions.BinaryGetBit)] = "get_bit",
            [nameof(BlueTuskDbFunctionsExtensions.BinarySetBit)] = "set_bit",
            [nameof(BlueTuskDbFunctionsExtensions.BinaryTrim)] = "btrim",
            [nameof(BlueTuskDbFunctionsExtensions.BinaryTrimLeft)] = "ltrim",
            [nameof(BlueTuskDbFunctionsExtensions.BinaryTrimRight)] = "rtrim",
            [nameof(BlueTuskDbFunctionsExtensions.BinaryReverse)] = "reverse",
            [nameof(BlueTuskDbFunctionsExtensions.CubeRoot)] = "cbrt",
            [nameof(BlueTuskDbFunctionsExtensions.Degrees)] = "degrees",
            [nameof(BlueTuskDbFunctionsExtensions.Radians)] = "radians",
            [nameof(BlueTuskDbFunctionsExtensions.NumericDivide)] = "div",
            [nameof(BlueTuskDbFunctionsExtensions.Factorial)] = "factorial",
            [nameof(BlueTuskDbFunctionsExtensions.GreatestCommonDivisor)] = "gcd",
            [nameof(BlueTuskDbFunctionsExtensions.LeastCommonMultiple)] = "lcm",
            [nameof(BlueTuskDbFunctionsExtensions.NumericMinimumScale)] = "min_scale",
            [nameof(BlueTuskDbFunctionsExtensions.NumericScale)] = "scale",
            [nameof(BlueTuskDbFunctionsExtensions.NumericTrimScale)] = "trim_scale",
            [nameof(BlueTuskDbFunctionsExtensions.WidthBucket)] = "width_bucket",
            [nameof(BlueTuskDbFunctionsExtensions.FormatValue)] = "to_char",
            [nameof(BlueTuskDbFunctionsExtensions.ParseDate)] = "to_date",
            [nameof(BlueTuskDbFunctionsExtensions.ParseNumber)] = "to_number",
            [nameof(BlueTuskDbFunctionsExtensions.ParseTimestamp)] = "to_timestamp",
            [nameof(BlueTuskDbFunctionsExtensions.UnixTimestamp)] = "to_timestamp",
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
            [nameof(BlueTuskDbFunctionsExtensions.JsonPathQueryArray)] = "jsonb_path_query_array",
            [nameof(BlueTuskDbFunctionsExtensions.JsonPathExistsFunction)] = "jsonb_path_exists",
            [nameof(BlueTuskDbFunctionsExtensions.JsonPathMatchesFunction)] = "jsonb_path_match",
            [nameof(BlueTuskDbFunctionsExtensions.JsonPretty)] = "jsonb_pretty",
            [nameof(BlueTuskDbFunctionsExtensions.JsonStripNulls)] = "jsonb_strip_nulls",
            [nameof(BlueTuskDbFunctionsExtensions.JsonSet)] = "jsonb_set",
            [nameof(BlueTuskDbFunctionsExtensions.JsonSetLax)] = "jsonb_set_lax",
            [nameof(BlueTuskDbFunctionsExtensions.JsonInsert)] = "jsonb_insert",
            [nameof(BlueTuskDbFunctionsExtensions.RegexReplace)] = "regexp_replace",
            [nameof(BlueTuskDbFunctionsExtensions.RegexCount)] = "regexp_count",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkHost)] = "host",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkAddressFamily)] = "family",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkMaskLength)] = "masklen",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkPart)] = "network",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkBroadcast)] = "broadcast",
            [nameof(BlueTuskDbFunctionsExtensions.ToTextSearchVector)] = "to_tsvector",
            [nameof(BlueTuskDbFunctionsExtensions.JsonToTextSearchVector)] = "jsonb_to_tsvector",
            [nameof(BlueTuskDbFunctionsExtensions.ToTextSearchQuery)] = "to_tsquery",
            [nameof(BlueTuskDbFunctionsExtensions.PlainToTextSearchQuery)] = "plainto_tsquery",
            [nameof(BlueTuskDbFunctionsExtensions.PhraseToTextSearchQuery)] = "phraseto_tsquery",
            [nameof(BlueTuskDbFunctionsExtensions.WebSearchToTextSearchQuery)] = "websearch_to_tsquery",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchVectorLength)] = "length",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchQueryNodeCount)] = "numnode",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchQueryTree)] = "querytree",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchSetWeight)] = "setweight",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchStrip)] = "strip",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchRewrite)] = "ts_rewrite",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchRank)] = "ts_rank",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchCoverDensityRank)] = "ts_rank_cd",
            [nameof(BlueTuskDbFunctionsExtensions.TextSearchHeadline)] = "ts_headline",
            [nameof(BlueTuskDbFunctionsExtensions.JsonTextSearchHeadline)] = "ts_headline",
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

        var rawArguments = arguments.Skip(1).ToArray();
        var jsonbMapping = typeMappingSource.FindMapping("jsonb");
        var functionArguments = rawArguments
            .Select((argument, index) => UsesJsonbArgument(method.Name, index, rawArguments.Length)
                ? sqlExpressionFactory.ApplyTypeMapping(argument, jsonbMapping)
                : sqlExpressionFactory.ApplyDefaultTypeMapping(argument)!)
            .ToArray();
        var resultType = Nullable.GetUnderlyingType(method.ReturnType) ?? method.ReturnType;
        var resultMapping = method.Name switch
        {
            nameof(BlueTuskDbFunctionsExtensions.JsonPathQueryFirst) =>
                typeMappingSource.FindMapping("jsonb"),
            nameof(BlueTuskDbFunctionsExtensions.JsonPathQueryArray) =>
                typeMappingSource.FindMapping("jsonb"),
            nameof(BlueTuskDbFunctionsExtensions.JsonStripNulls) =>
                typeMappingSource.FindMapping("jsonb"),
            nameof(BlueTuskDbFunctionsExtensions.JsonSet) =>
                typeMappingSource.FindMapping("jsonb"),
            nameof(BlueTuskDbFunctionsExtensions.JsonSetLax) =>
                typeMappingSource.FindMapping("jsonb"),
            nameof(BlueTuskDbFunctionsExtensions.JsonInsert) =>
                typeMappingSource.FindMapping("jsonb"),
            nameof(BlueTuskDbFunctionsExtensions.JsonTextSearchHeadline) =>
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

    private static bool UsesJsonbArgument(string methodName, int argumentIndex, int argumentCount)
        => methodName switch
        {
            nameof(BlueTuskDbFunctionsExtensions.JsonTypeOf)
                or nameof(BlueTuskDbFunctionsExtensions.JsonArrayLength)
                or nameof(BlueTuskDbFunctionsExtensions.JsonPretty)
                or nameof(BlueTuskDbFunctionsExtensions.JsonStripNulls) => argumentIndex == 0,
            nameof(BlueTuskDbFunctionsExtensions.JsonPathQueryFirst) =>
                argumentIndex == 0 || argumentCount == 4 && argumentIndex == 2,
            nameof(BlueTuskDbFunctionsExtensions.JsonPathQueryArray)
                or nameof(BlueTuskDbFunctionsExtensions.JsonPathExistsFunction)
                or nameof(BlueTuskDbFunctionsExtensions.JsonPathMatchesFunction) =>
                argumentIndex is 0 or 2,
            nameof(BlueTuskDbFunctionsExtensions.JsonSet)
                or nameof(BlueTuskDbFunctionsExtensions.JsonSetLax)
                or nameof(BlueTuskDbFunctionsExtensions.JsonInsert) =>
                argumentIndex is 0 or 2,
            nameof(BlueTuskDbFunctionsExtensions.JsonToTextSearchVector) =>
                argumentCount == 2 || argumentIndex > 0,
            nameof(BlueTuskDbFunctionsExtensions.JsonTextSearchHeadline) =>
                argumentIndex == (argumentCount == 3 ? 0 : 1),
            _ => false,
        };
}
