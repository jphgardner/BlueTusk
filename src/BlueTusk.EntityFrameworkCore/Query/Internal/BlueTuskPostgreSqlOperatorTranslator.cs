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

        var left = arguments[1];
        var right = arguments[2];
        if (UsesSameStoreType(method, left, right))
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

        return new BlueTuskBinaryExpression(left, right, operatorToken, _booleanMapping);
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
            or nameof(BlueTuskDbFunctionsExtensions.MultirangeIsAdjacentTo))
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
            or nameof(BlueTuskDbFunctionsExtensions.FullTextQueryContainedBy);
    }
}
