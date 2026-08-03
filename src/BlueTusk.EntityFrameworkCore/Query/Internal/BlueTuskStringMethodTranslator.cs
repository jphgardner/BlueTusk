using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskStringMethodTranslator(ISqlExpressionFactory sqlExpressionFactory)
    : IMethodCallTranslator
{
    private static readonly MethodInfo StartsWith = typeof(string).GetRuntimeMethod(
        nameof(string.StartsWith),
        [typeof(string)])!;

    private static readonly MethodInfo EndsWith = typeof(string).GetRuntimeMethod(
        nameof(string.EndsWith),
        [typeof(string)])!;

    private static readonly MethodInfo Contains = typeof(string).GetRuntimeMethod(
        nameof(string.Contains),
        [typeof(string)])!;

    private static readonly MethodInfo ToLower = typeof(string).GetRuntimeMethod(
        nameof(string.ToLower),
        Type.EmptyTypes)!;

    private static readonly MethodInfo ToUpper = typeof(string).GetRuntimeMethod(
        nameof(string.ToUpper),
        Type.EmptyTypes)!;

    private static readonly MethodInfo ToLowerInvariant = typeof(string).GetRuntimeMethod(
        nameof(string.ToLowerInvariant),
        Type.EmptyTypes)!;

    private static readonly MethodInfo ToUpperInvariant = typeof(string).GetRuntimeMethod(
        nameof(string.ToUpperInvariant),
        Type.EmptyTypes)!;

    private static readonly MethodInfo Replace = typeof(string).GetRuntimeMethod(
        nameof(string.Replace),
        [typeof(string), typeof(string)])!;

    private static readonly MethodInfo Substring = typeof(string).GetRuntimeMethod(
        nameof(string.Substring),
        [typeof(int)])!;

    private static readonly MethodInfo SubstringWithLength = typeof(string).GetRuntimeMethod(
        nameof(string.Substring),
        [typeof(int), typeof(int)])!;

    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (instance is null)
        {
            return null;
        }

        if (method == ToLower || method == ToUpper || method == ToLowerInvariant || method == ToUpperInvariant)
        {
            return Function(method == ToLower || method == ToLowerInvariant ? "lower" : "upper", instance);
        }

        if (method == Replace)
        {
            return Function("replace", instance, arguments[0], arguments[1]);
        }

        if (method == Substring || method == SubstringWithLength)
        {
            var start = sqlExpressionFactory.Add(
                arguments[0],
                sqlExpressionFactory.Constant(1));
            return method == Substring
                ? Function("substring", instance, start)
                : Function("substring", instance, start, arguments[1]);
        }

        if (method != StartsWith && method != EndsWith && method != Contains)
        {
            return null;
        }

        var argument = arguments[0];
        if (argument is SqlConstantExpression { Value: string value })
        {
            var escaped = EscapeLikePattern(value);
            var pattern = method == StartsWith
                ? escaped + "%"
                : method == EndsWith
                    ? "%" + escaped
                    : "%" + escaped + "%";

            return sqlExpressionFactory.Like(
                instance,
                sqlExpressionFactory.Constant(pattern, instance.TypeMapping),
                sqlExpressionFactory.Constant("\\", instance.TypeMapping));
        }

        if (method == Contains)
        {
            return sqlExpressionFactory.GreaterThan(
                Function("strpos", instance, argument, resultType: typeof(int)),
                sqlExpressionFactory.Constant(0));
        }

        var length = Function("char_length", argument, resultType: typeof(int));
        return sqlExpressionFactory.Equal(
            Function(method == StartsWith ? "left" : "right", instance, length),
            argument);
    }

    private SqlExpression Function(
        string name,
        SqlExpression first,
        SqlExpression? second = null,
        SqlExpression? third = null,
        Type? resultType = null)
    {
        var arguments = new List<SqlExpression> { first };
        if (second is not null)
        {
            arguments.Add(second);
        }

        if (third is not null)
        {
            arguments.Add(third);
        }

        return sqlExpressionFactory.Function(
            name,
            arguments,
            nullable: true,
            argumentsPropagateNullability: Enumerable.Repeat(true, arguments.Count),
            resultType ?? typeof(string),
            resultType is null ? first.TypeMapping : null);
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
