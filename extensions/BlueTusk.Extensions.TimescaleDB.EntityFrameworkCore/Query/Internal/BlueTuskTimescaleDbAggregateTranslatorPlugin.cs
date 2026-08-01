using System.Reflection;
using BlueTusk.EntityFrameworkCore.Query;
using BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskTimescaleDbAggregateTranslatorPlugin : IAggregateMethodCallTranslatorPlugin
{
    public BlueTuskTimescaleDbAggregateTranslatorPlugin(
        ISqlExpressionFactory sqlExpressionFactory,
        IRelationalTypeMappingSource typeMappingSource,
        BlueTuskTimescaleDbQueryOptions options)
    {
        Translators =
        [
            new BlueTuskTimescaleDbAggregateTranslator(
                sqlExpressionFactory,
                typeMappingSource,
                options.Schema),
        ];
    }

    public IEnumerable<IAggregateMethodCallTranslator> Translators { get; }
}

internal sealed class BlueTuskTimescaleDbAggregateTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource,
    string schema)
    : IAggregateMethodCallTranslator
{
    public SqlExpression? Translate(
        MethodInfo method,
        EnumerableExpression source,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(BlueTuskTimescaleDbFunctionsExtensions))
        {
            return null;
        }

        return method.Name switch
        {
            nameof(BlueTuskTimescaleDbFunctionsExtensions.TimescaleFirst) =>
                TranslateFirstOrLast(method, source, "first"),
            nameof(BlueTuskTimescaleDbFunctionsExtensions.TimescaleLast) =>
                TranslateFirstOrLast(method, source, "last"),
            nameof(BlueTuskTimescaleDbFunctionsExtensions.TimescaleHistogram) =>
                TranslateHistogram(method, source, arguments),
            _ => null,
        };
    }

    private SqlExpression? TranslateFirstOrLast(
        MethodInfo method,
        EnumerableExpression source,
        string functionName)
    {
        if (source.Selector is not SqlExpression selector ||
            !BlueTuskSqlExpressionFactory.TryGetRowValue(selector, out var values) ||
            values.Count != 2)
        {
            return null;
        }

        var aggregateArguments = values
            .Select(value => sqlExpressionFactory.ApplyDefaultTypeMapping(value)!)
            .ToArray();
        var resultType = aggregateArguments[0].Type;
        var resultMapping = aggregateArguments[0].TypeMapping
            ?? typeMappingSource.FindMapping(resultType);
        return resultMapping is null
            ? null
            : BlueTuskSqlExpressionFactory.AggregateFunction(
                schema,
                functionName,
                aggregateArguments,
                source.IsDistinct,
                source.Orderings,
                [],
                source.Predicate,
                resultType,
                resultMapping);
    }

    private SqlExpression? TranslateHistogram(
        MethodInfo method,
        EnumerableExpression source,
        IReadOnlyList<SqlExpression> arguments)
    {
        if (source.Selector is not SqlExpression selector || arguments.Count != 4)
        {
            return null;
        }

        var aggregateArguments = new[] { selector }
            .Concat(arguments.Skip(1))
            .Select(argument => sqlExpressionFactory.ApplyDefaultTypeMapping(argument)!)
            .ToArray();
        var resultMapping = typeMappingSource.FindMapping(method.ReturnType);
        return resultMapping is null
            ? null
            : BlueTuskSqlExpressionFactory.AggregateFunction(
                schema,
                "histogram",
                aggregateArguments,
                source.IsDistinct,
                source.Orderings,
                [],
                source.Predicate,
                method.ReturnType,
                resultMapping);
    }
}
