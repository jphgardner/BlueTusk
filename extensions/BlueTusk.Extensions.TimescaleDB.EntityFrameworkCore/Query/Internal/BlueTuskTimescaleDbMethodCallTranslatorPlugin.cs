using System.Reflection;
using BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskTimescaleDbMethodCallTranslatorPlugin : IMethodCallTranslatorPlugin
{
    public BlueTuskTimescaleDbMethodCallTranslatorPlugin(
        ISqlExpressionFactory sqlExpressionFactory,
        IRelationalTypeMappingSource typeMappingSource,
        BlueTuskTimescaleDbQueryOptions options)
    {
        Translators =
        [
            new BlueTuskTimescaleDbMethodCallTranslator(
                sqlExpressionFactory,
                typeMappingSource,
                options.Schema),
        ];
    }

    public IEnumerable<IMethodCallTranslator> Translators { get; }
}

internal sealed class BlueTuskTimescaleDbMethodCallTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource,
    string schema)
    : IMethodCallTranslator
{
    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(BlueTuskTimescaleDbFunctionsExtensions) ||
            method.Name != nameof(BlueTuskTimescaleDbFunctionsExtensions.TimeBucket) ||
            arguments.Count < 3)
        {
            return null;
        }

        var functionArguments = arguments
            .Skip(1)
            .Select(argument => sqlExpressionFactory.ApplyDefaultTypeMapping(argument)!)
            .ToArray();
        var valueMapping = functionArguments[1].TypeMapping
            ?? typeMappingSource.FindMapping(method.ReturnType);
        return valueMapping is null
            ? null
            : sqlExpressionFactory.Function(
                schema,
                "time_bucket",
                functionArguments,
                nullable: true,
                argumentsPropagateNullability: Enumerable.Repeat(true, functionArguments.Length),
                method.ReturnType,
                valueMapping);
    }
}
