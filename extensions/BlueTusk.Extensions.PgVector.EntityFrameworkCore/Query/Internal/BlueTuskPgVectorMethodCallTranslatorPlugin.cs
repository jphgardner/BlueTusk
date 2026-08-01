using System.Reflection;
using BlueTusk.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace BlueTusk.Extensions.PgVector.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskPgVectorMethodCallTranslatorPlugin : IMethodCallTranslatorPlugin
{
    public BlueTuskPgVectorMethodCallTranslatorPlugin(
        ISqlExpressionFactory sqlExpressionFactory)
    {
        Translators =
        [
            new BlueTuskPgVectorMethodCallTranslator(sqlExpressionFactory),
        ];
    }

    public IEnumerable<IMethodCallTranslator> Translators { get; }
}

internal sealed class BlueTuskPgVectorMethodCallTranslator(
    ISqlExpressionFactory sqlExpressionFactory)
    : IMethodCallTranslator
{
    private static readonly Dictionary<string, string> Operators =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(BlueTuskPgVectorDbFunctionsExtensions.L2Distance)] = "<->",
            [nameof(BlueTuskPgVectorDbFunctionsExtensions.MaxInnerProduct)] = "<#>",
            [nameof(BlueTuskPgVectorDbFunctionsExtensions.CosineDistance)] = "<=>",
            [nameof(BlueTuskPgVectorDbFunctionsExtensions.L1Distance)] = "<+>",
        };

    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(BlueTuskPgVectorDbFunctionsExtensions) ||
            !Operators.TryGetValue(method.Name, out var operatorToken))
        {
            return null;
        }

        var mapping = arguments[1].TypeMapping ?? arguments[2].TypeMapping;
        if (mapping is null)
        {
            throw new InvalidOperationException("The pgvector EF plugin requires a vector operand mapping.");
        }

        var left = sqlExpressionFactory.ApplyTypeMapping(arguments[1], mapping);
        var right = sqlExpressionFactory.ApplyTypeMapping(arguments[2], mapping);
        var doubleMapping = sqlExpressionFactory
            .ApplyDefaultTypeMapping(sqlExpressionFactory.Constant(0d))
            .TypeMapping
            ?? throw new InvalidOperationException("BlueTusk requires a double precision mapping.");
        return BlueTuskSqlExpressionFactory.BinaryOperator(
            left,
            right,
            operatorToken,
            typeof(double),
            doubleMapping);
    }
}
