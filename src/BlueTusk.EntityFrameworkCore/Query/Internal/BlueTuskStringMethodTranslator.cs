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

    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (instance is null
            || (method != StartsWith && method != EndsWith && method != Contains))
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

        var wildcard = sqlExpressionFactory.Constant("%", instance.TypeMapping);
        var patternExpression = method == StartsWith
            ? Concat(argument, wildcard)
            : method == EndsWith
                ? Concat(wildcard, argument)
                : Concat(Concat(wildcard, argument), wildcard);

        return sqlExpressionFactory.Like(instance, patternExpression);
    }

    private SqlExpression Concat(SqlExpression left, SqlExpression right)
        => sqlExpressionFactory.Function(
            "concat",
            [left, right],
            nullable: true,
            argumentsPropagateNullability: [true, true],
            typeof(string),
            left.TypeMapping ?? right.TypeMapping);

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
