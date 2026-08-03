using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQuantifiedComparisonTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource)
    : IMethodCallTranslator
{
    private static readonly Dictionary<string, (string Operator, BlueTuskArrayQuantifier Quantifier)>
        Comparisons = new Dictionary<string, (string, BlueTuskArrayQuantifier)>(StringComparer.Ordinal)
        {
            [nameof(BlueTuskDbFunctionsExtensions.EqualAny)] = ("=", BlueTuskArrayQuantifier.Any),
            [nameof(BlueTuskDbFunctionsExtensions.NotEqualAny)] = ("<>", BlueTuskArrayQuantifier.Any),
            [nameof(BlueTuskDbFunctionsExtensions.LessThanAny)] = ("<", BlueTuskArrayQuantifier.Any),
            [nameof(BlueTuskDbFunctionsExtensions.LessThanOrEqualAny)] = ("<=", BlueTuskArrayQuantifier.Any),
            [nameof(BlueTuskDbFunctionsExtensions.GreaterThanAny)] = (">", BlueTuskArrayQuantifier.Any),
            [nameof(BlueTuskDbFunctionsExtensions.GreaterThanOrEqualAny)] = (">=", BlueTuskArrayQuantifier.Any),
            [nameof(BlueTuskDbFunctionsExtensions.EqualAll)] = ("=", BlueTuskArrayQuantifier.All),
            [nameof(BlueTuskDbFunctionsExtensions.NotEqualAll)] = ("<>", BlueTuskArrayQuantifier.All),
            [nameof(BlueTuskDbFunctionsExtensions.LessThanAll)] = ("<", BlueTuskArrayQuantifier.All),
            [nameof(BlueTuskDbFunctionsExtensions.LessThanOrEqualAll)] = ("<=", BlueTuskArrayQuantifier.All),
            [nameof(BlueTuskDbFunctionsExtensions.GreaterThanAll)] = (">", BlueTuskArrayQuantifier.All),
            [nameof(BlueTuskDbFunctionsExtensions.GreaterThanOrEqualAll)] = (">=", BlueTuskArrayQuantifier.All),
            [nameof(BlueTuskDbFunctionsExtensions.LikeAny)] = ("LIKE", BlueTuskArrayQuantifier.Any),
            [nameof(BlueTuskDbFunctionsExtensions.ILikeAny)] = ("ILIKE", BlueTuskArrayQuantifier.Any),
            [nameof(BlueTuskDbFunctionsExtensions.LikeAll)] = ("LIKE", BlueTuskArrayQuantifier.All),
            [nameof(BlueTuskDbFunctionsExtensions.ILikeAll)] = ("ILIKE", BlueTuskArrayQuantifier.All),
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
        if (method.DeclaringType != typeof(BlueTuskDbFunctionsExtensions)
            || !Comparisons.TryGetValue(method.Name, out var comparison))
        {
            return null;
        }

        var array = sqlExpressionFactory.ApplyDefaultTypeMapping(arguments[2]);
        var elementMapping = (RelationalTypeMapping?)array.TypeMapping?.ElementTypeMapping
            ?? arguments[1].TypeMapping
            ?? typeMappingSource.FindMapping(arguments[1].Type);
        if (array.TypeMapping is null || elementMapping is null)
        {
            return null;
        }

        var item = sqlExpressionFactory.ApplyTypeMapping(arguments[1], elementMapping);
        return new BlueTuskQuantifiedComparisonExpression(
            item,
            array,
            comparison.Operator,
            comparison.Quantifier,
            _booleanMapping);
    }
}
