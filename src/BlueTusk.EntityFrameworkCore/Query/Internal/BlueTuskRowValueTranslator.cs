using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskRowValueTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource)
    : IMethodCallTranslator
{
    private static readonly Dictionary<string, string> Comparisons =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(BlueTuskDbFunctionsExtensions.RowEqual)] = "=",
            [nameof(BlueTuskDbFunctionsExtensions.RowNotEqual)] = "<>",
            [nameof(BlueTuskDbFunctionsExtensions.RowLessThan)] = "<",
            [nameof(BlueTuskDbFunctionsExtensions.RowLessThanOrEqual)] = "<=",
            [nameof(BlueTuskDbFunctionsExtensions.RowGreaterThan)] = ">",
            [nameof(BlueTuskDbFunctionsExtensions.RowGreaterThanOrEqual)] = ">=",
        };

    private readonly RelationalTypeMapping _booleanMapping =
        typeMappingSource.FindMapping(typeof(bool))
        ?? throw new InvalidOperationException("BlueTusk requires a Boolean relational type mapping.");

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(ValueTuple))]
    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType == typeof(ValueTuple)
            && method is { IsStatic: true, Name: nameof(ValueTuple.Create) })
        {
            return new BlueTuskRowValueExpression(
                arguments.Select(argument => sqlExpressionFactory.ApplyDefaultTypeMapping(argument)!).ToArray(),
                method.ReturnType,
                new BlueTuskRowValueTypeMapping(method.ReturnType));
        }

        if (method.DeclaringType != typeof(BlueTuskDbFunctionsExtensions)
            || !Comparisons.TryGetValue(method.Name, out var operatorToken)
            || arguments[1] is not BlueTuskRowValueExpression left
            || arguments[2] is not BlueTuskRowValueExpression right)
        {
            return null;
        }

        if (left.Values.Count != right.Values.Count)
        {
            throw new InvalidOperationException(
                "PostgreSQL row-value comparisons require tuples with the same number of elements.");
        }

        return new BlueTuskBinaryExpression(left, right, operatorToken, _booleanMapping);
    }
}
