using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskPostgreSqlOperatorTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource)
    : IMethodCallTranslator
{
    private static readonly Dictionary<string, string> Operators =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(BlueTuskDbFunctionsExtensions.ILike)] = "ILIKE",
            [nameof(BlueTuskDbFunctionsExtensions.RegexIsMatch)] = "~",
            [nameof(BlueTuskDbFunctionsExtensions.RegexIsMatchInsensitive)] = "~*",
            [nameof(BlueTuskDbFunctionsExtensions.RegexIsNotMatch)] = "!~",
            [nameof(BlueTuskDbFunctionsExtensions.RegexIsNotMatchInsensitive)] = "!~*",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayContains)] = "@>",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayContainedBy)] = "<@",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayOverlaps)] = "&&",
            [nameof(BlueTuskDbFunctionsExtensions.RangeContains)] = "@>",
            [nameof(BlueTuskDbFunctionsExtensions.RangeContainedBy)] = "<@",
            [nameof(BlueTuskDbFunctionsExtensions.RangeOverlaps)] = "&&",
            [nameof(BlueTuskDbFunctionsExtensions.RangeIsStrictlyLeftOf)] = "<<",
            [nameof(BlueTuskDbFunctionsExtensions.RangeIsStrictlyRightOf)] = ">>",
            [nameof(BlueTuskDbFunctionsExtensions.RangeIsAdjacentTo)] = "-|-",
            [nameof(BlueTuskDbFunctionsExtensions.RangeDoesNotExtendRightOf)] = "&<",
            [nameof(BlueTuskDbFunctionsExtensions.RangeDoesNotExtendLeftOf)] = "&>",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeContains)] = "@>",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeContainedBy)] = "<@",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeOverlaps)] = "&&",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeIsStrictlyLeftOf)] = "<<",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeIsStrictlyRightOf)] = ">>",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeDoesNotExtendRightOf)] = "&<",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeDoesNotExtendLeftOf)] = "&>",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeIsAdjacentTo)] = "-|-",
            [nameof(BlueTuskDbFunctionsExtensions.JsonContains)] = "@>",
            [nameof(BlueTuskDbFunctionsExtensions.JsonContainedBy)] = "<@",
            [nameof(BlueTuskDbFunctionsExtensions.JsonExists)] = "?",
            [nameof(BlueTuskDbFunctionsExtensions.JsonExistsAny)] = "?|",
            [nameof(BlueTuskDbFunctionsExtensions.JsonExistsAll)] = "?&",
            [nameof(BlueTuskDbFunctionsExtensions.JsonPathExists)] = "@?",
            [nameof(BlueTuskDbFunctionsExtensions.JsonPathMatches)] = "@@",
            [nameof(BlueTuskDbFunctionsExtensions.FullTextMatches)] = "@@",
            [nameof(BlueTuskDbFunctionsExtensions.FullTextQueryContains)] = "@>",
            [nameof(BlueTuskDbFunctionsExtensions.FullTextQueryContainedBy)] = "<@",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkContains)] = ">>=",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkContainedBy)] = "<<=",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkOverlaps)] = "&&",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkStrictlyContains)] = ">>",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkStrictlyContainedBy)] = "<<",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayConcatenate)] = "||",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayAppend)] = "||",
            [nameof(BlueTuskDbFunctionsExtensions.ArrayPrepend)] = "||",
            [nameof(BlueTuskDbFunctionsExtensions.RangeUnion)] = "+",
            [nameof(BlueTuskDbFunctionsExtensions.RangeIntersect)] = "*",
            [nameof(BlueTuskDbFunctionsExtensions.RangeExcept)] = "-",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeUnion)] = "+",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeIntersect)] = "*",
            [nameof(BlueTuskDbFunctionsExtensions.MultirangeExcept)] = "-",
            [nameof(BlueTuskDbFunctionsExtensions.JsonConcatenate)] = "||",
            [nameof(BlueTuskDbFunctionsExtensions.JsonDelete)] = "-",
            [nameof(BlueTuskDbFunctionsExtensions.JsonDeletePath)] = "#-",
            [nameof(BlueTuskDbFunctionsExtensions.JsonGet)] = "->",
            [nameof(BlueTuskDbFunctionsExtensions.JsonGetText)] = "->>",
            [nameof(BlueTuskDbFunctionsExtensions.JsonGetPath)] = "#>",
            [nameof(BlueTuskDbFunctionsExtensions.JsonGetPathText)] = "#>>",
            [nameof(BlueTuskDbFunctionsExtensions.FullTextVectorConcatenate)] = "||",
            [nameof(BlueTuskDbFunctionsExtensions.FullTextQueryAnd)] = "&&",
            [nameof(BlueTuskDbFunctionsExtensions.FullTextQueryOr)] = "||",
            [nameof(BlueTuskDbFunctionsExtensions.FullTextQueryPhrase)] = "<->",
            [nameof(BlueTuskDbFunctionsExtensions.FullTextQueryNot)] = "!!",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkBitwiseNot)] = "~",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkBitwiseAnd)] = "&",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkBitwiseOr)] = "|",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkAdd)] = "+",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkSubtract)] = "-",
            [nameof(BlueTuskDbFunctionsExtensions.NetworkDistance)] = "-",
            [nameof(BlueTuskDbFunctionsExtensions.BitStringConcatenate)] = "||",
            [nameof(BlueTuskDbFunctionsExtensions.BitStringAnd)] = "&",
            [nameof(BlueTuskDbFunctionsExtensions.BitStringOr)] = "|",
            [nameof(BlueTuskDbFunctionsExtensions.BitStringXor)] = "#",
            [nameof(BlueTuskDbFunctionsExtensions.BitStringNot)] = "~",
            [nameof(BlueTuskDbFunctionsExtensions.BitStringShiftLeft)] = "<<",
            [nameof(BlueTuskDbFunctionsExtensions.BitStringShiftRight)] = ">>",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryIsStrictlyLeftOf)] = "<<",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryIsStrictlyRightOf)] = ">>",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryIsStrictlyBelow)] = "<<|",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryIsStrictlyAbove)] = "|>>",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryDoesNotExtendRightOf)] = "&<",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryDoesNotExtendLeftOf)] = "&>",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryDoesNotExtendAbove)] = "&<|",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryDoesNotExtendBelow)] = "|&>",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryOverlaps)] = "&&",
            [nameof(BlueTuskDbFunctionsExtensions.GeometrySameAs)] = "~=",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryEqual)] = "=",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryNotEqual)] = "<>",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryLessThan)] = "<",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryLessThanOrEqual)] = "<=",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryGreaterThan)] = ">",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryGreaterThanOrEqual)] = ">=",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryContains)] = "@>",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryContainedBy)] = "<@",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryIntersects)] = "?#",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryIsPerpendicular)] = "?-|",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryIsParallel)] = "?||",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryIsHorizontal)] = "?-",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryIsVertical)] = "?|",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryDistance)] = "<->",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryIntersection)] = "#",
            [nameof(BlueTuskDbFunctionsExtensions.GeometryClosestPoint)] = "##",
            [nameof(BlueTuskDbFunctionsExtensions.PointAdd)] = "+",
            [nameof(BlueTuskDbFunctionsExtensions.PointSubtract)] = "-",
            [nameof(BlueTuskDbFunctionsExtensions.PointMultiply)] = "*",
            [nameof(BlueTuskDbFunctionsExtensions.PointDivide)] = "/",
            [nameof(BlueTuskDbFunctionsExtensions.PathTranslate)] = "+",
            [nameof(BlueTuskDbFunctionsExtensions.PathTranslateNegative)] = "-",
            [nameof(BlueTuskDbFunctionsExtensions.PathScale)] = "*",
            [nameof(BlueTuskDbFunctionsExtensions.PathScaleInverse)] = "/",
            [nameof(BlueTuskDbFunctionsExtensions.PathConcatenate)] = "+",
            [nameof(BlueTuskDbFunctionsExtensions.BoxTranslate)] = "+",
            [nameof(BlueTuskDbFunctionsExtensions.BoxTranslateNegative)] = "-",
            [nameof(BlueTuskDbFunctionsExtensions.BoxScale)] = "*",
            [nameof(BlueTuskDbFunctionsExtensions.BoxScaleInverse)] = "/",
            [nameof(BlueTuskDbFunctionsExtensions.CircleTranslate)] = "+",
            [nameof(BlueTuskDbFunctionsExtensions.CircleTranslateNegative)] = "-",
            [nameof(BlueTuskDbFunctionsExtensions.CircleScale)] = "*",
            [nameof(BlueTuskDbFunctionsExtensions.CircleScaleInverse)] = "/",
        };

    private readonly RelationalTypeMapping _booleanMapping =
        typeMappingSource.FindMapping(typeof(bool))
        ?? throw new InvalidOperationException("BlueTusk requires a Boolean relational type mapping.");

    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(BlueTuskDbFunctionsExtensions) ||
            !Operators.TryGetValue(method.Name, out var operatorToken))
        {
            return null;
        }

        var resultType = Nullable.GetUnderlyingType(method.ReturnType) ?? method.ReturnType;
        if (arguments.Count == 2)
        {
            var operand = sqlExpressionFactory.ApplyDefaultTypeMapping(arguments[1]);
            var unaryResultMapping = FindResultMapping(method, resultType, operand, operand);
            return new BlueTuskUnaryExpression(operand, operatorToken, resultType, unaryResultMapping);
        }

        var left = arguments[1];
        var right = arguments[2];
        if (method.Name is nameof(BlueTuskDbFunctionsExtensions.JsonConcatenate)
            or nameof(BlueTuskDbFunctionsExtensions.JsonDelete)
            or nameof(BlueTuskDbFunctionsExtensions.JsonDeletePath)
            or nameof(BlueTuskDbFunctionsExtensions.JsonGet)
            or nameof(BlueTuskDbFunctionsExtensions.JsonGetText)
            or nameof(BlueTuskDbFunctionsExtensions.JsonGetPath)
            or nameof(BlueTuskDbFunctionsExtensions.JsonGetPathText))
        {
            var jsonbMapping = typeMappingSource.FindMapping("jsonb")
                ?? throw new InvalidOperationException("BlueTusk requires a JSONB relational type mapping.");
            left = sqlExpressionFactory.ApplyTypeMapping(left, jsonbMapping);
            right = method.Name == nameof(BlueTuskDbFunctionsExtensions.JsonConcatenate)
                ? sqlExpressionFactory.ApplyTypeMapping(right, jsonbMapping)
                : sqlExpressionFactory.ApplyDefaultTypeMapping(right);
        }
        else if (UsesSameStoreType(method, left, right))
        {
            var typeMapping = left.TypeMapping
                ?? right.TypeMapping
                ?? typeMappingSource.FindMapping(left.Type);
            left = sqlExpressionFactory.ApplyTypeMapping(left, typeMapping);
            right = sqlExpressionFactory.ApplyTypeMapping(right, typeMapping);
        }
        else
        {
            left = sqlExpressionFactory.ApplyDefaultTypeMapping(left);
            right = sqlExpressionFactory.ApplyDefaultTypeMapping(right);
        }

        var resultMapping = FindResultMapping(method, resultType, left, right);
        return new BlueTuskBinaryExpression(left, right, operatorToken, resultType, resultMapping);
    }

    private RelationalTypeMapping FindResultMapping(
        MethodInfo method,
        Type resultType,
        SqlExpression left,
        SqlExpression right)
    {
        if (resultType == typeof(bool))
        {
            return _booleanMapping;
        }

        if (method.Name is nameof(BlueTuskDbFunctionsExtensions.JsonGetText)
            or nameof(BlueTuskDbFunctionsExtensions.JsonGetPathText))
        {
            return typeMappingSource.FindMapping(typeof(string))
                ?? throw new InvalidOperationException("BlueTusk requires a text relational type mapping.");
        }

        if (method.Name is nameof(BlueTuskDbFunctionsExtensions.JsonConcatenate)
            or nameof(BlueTuskDbFunctionsExtensions.JsonDelete)
            or nameof(BlueTuskDbFunctionsExtensions.JsonDeletePath)
            or nameof(BlueTuskDbFunctionsExtensions.JsonGet)
            or nameof(BlueTuskDbFunctionsExtensions.JsonGetPath))
        {
            return left.TypeMapping!;
        }

        if (method.Name == nameof(BlueTuskDbFunctionsExtensions.ArrayPrepend))
        {
            return right.TypeMapping!;
        }

        return resultType == left.Type
            ? left.TypeMapping!
            : resultType == right.Type
                ? right.TypeMapping!
                : typeMappingSource.FindMapping(resultType)
                    ?? throw new InvalidOperationException(
                        $"BlueTusk requires a relational type mapping for {resultType.Name} operator results.");
    }

    private static bool UsesSameStoreType(MethodInfo method, SqlExpression left, SqlExpression right)
    {
        if (method.Name is nameof(BlueTuskDbFunctionsExtensions.RangeContains)
            or nameof(BlueTuskDbFunctionsExtensions.RangeContainedBy)
            or nameof(BlueTuskDbFunctionsExtensions.RangeOverlaps)
            or nameof(BlueTuskDbFunctionsExtensions.RangeIsStrictlyLeftOf)
            or nameof(BlueTuskDbFunctionsExtensions.RangeIsStrictlyRightOf)
            or nameof(BlueTuskDbFunctionsExtensions.RangeIsAdjacentTo)
            or nameof(BlueTuskDbFunctionsExtensions.RangeDoesNotExtendRightOf)
            or nameof(BlueTuskDbFunctionsExtensions.RangeDoesNotExtendLeftOf)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeContains)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeContainedBy)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeOverlaps)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeIsStrictlyLeftOf)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeIsStrictlyRightOf)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeDoesNotExtendRightOf)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeDoesNotExtendLeftOf)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeIsAdjacentTo)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryDistance)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryIntersection)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryClosestPoint)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryContainedBy)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryContains)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryIntersects))
        {
            return left.Type == right.Type;
        }

        return method.Name is nameof(BlueTuskDbFunctionsExtensions.ILike)
            or nameof(BlueTuskDbFunctionsExtensions.RegexIsMatch)
            or nameof(BlueTuskDbFunctionsExtensions.RegexIsMatchInsensitive)
            or nameof(BlueTuskDbFunctionsExtensions.RegexIsNotMatch)
            or nameof(BlueTuskDbFunctionsExtensions.RegexIsNotMatchInsensitive)
            or nameof(BlueTuskDbFunctionsExtensions.ArrayContains)
            or nameof(BlueTuskDbFunctionsExtensions.ArrayContainedBy)
            or nameof(BlueTuskDbFunctionsExtensions.ArrayOverlaps)
            or nameof(BlueTuskDbFunctionsExtensions.JsonContains)
            or nameof(BlueTuskDbFunctionsExtensions.JsonContainedBy)
            or nameof(BlueTuskDbFunctionsExtensions.NetworkContains)
            or nameof(BlueTuskDbFunctionsExtensions.NetworkContainedBy)
            or nameof(BlueTuskDbFunctionsExtensions.NetworkOverlaps)
            or nameof(BlueTuskDbFunctionsExtensions.NetworkStrictlyContains)
            or nameof(BlueTuskDbFunctionsExtensions.NetworkStrictlyContainedBy)
            or nameof(BlueTuskDbFunctionsExtensions.FullTextQueryContains)
            or nameof(BlueTuskDbFunctionsExtensions.FullTextQueryContainedBy)
            or nameof(BlueTuskDbFunctionsExtensions.ArrayConcatenate)
            or nameof(BlueTuskDbFunctionsExtensions.RangeUnion)
            or nameof(BlueTuskDbFunctionsExtensions.RangeIntersect)
            or nameof(BlueTuskDbFunctionsExtensions.RangeExcept)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeUnion)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeIntersect)
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeExcept)
            or nameof(BlueTuskDbFunctionsExtensions.FullTextVectorConcatenate)
            or nameof(BlueTuskDbFunctionsExtensions.FullTextQueryAnd)
            or nameof(BlueTuskDbFunctionsExtensions.FullTextQueryOr)
            or nameof(BlueTuskDbFunctionsExtensions.FullTextQueryPhrase)
            or nameof(BlueTuskDbFunctionsExtensions.NetworkBitwiseAnd)
            or nameof(BlueTuskDbFunctionsExtensions.NetworkBitwiseOr)
            or nameof(BlueTuskDbFunctionsExtensions.BitStringConcatenate)
            or nameof(BlueTuskDbFunctionsExtensions.BitStringAnd)
            or nameof(BlueTuskDbFunctionsExtensions.BitStringOr)
            or nameof(BlueTuskDbFunctionsExtensions.BitStringXor)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryIsStrictlyLeftOf)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryIsStrictlyRightOf)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryIsStrictlyBelow)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryIsStrictlyAbove)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryDoesNotExtendRightOf)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryDoesNotExtendLeftOf)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryDoesNotExtendAbove)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryDoesNotExtendBelow)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryOverlaps)
            or nameof(BlueTuskDbFunctionsExtensions.GeometrySameAs)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryEqual)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryNotEqual)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryLessThan)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryLessThanOrEqual)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryGreaterThan)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryGreaterThanOrEqual)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryIsPerpendicular)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryIsParallel)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryIsHorizontal)
            or nameof(BlueTuskDbFunctionsExtensions.GeometryIsVertical)
            or nameof(BlueTuskDbFunctionsExtensions.PointAdd)
            or nameof(BlueTuskDbFunctionsExtensions.PointSubtract)
            or nameof(BlueTuskDbFunctionsExtensions.PointMultiply)
            or nameof(BlueTuskDbFunctionsExtensions.PointDivide)
            or nameof(BlueTuskDbFunctionsExtensions.PathConcatenate);
    }
}
