using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQueryTranslationPreprocessor(
    QueryTranslationPreprocessorDependencies dependencies,
    RelationalQueryTranslationPreprocessorDependencies relationalDependencies,
    QueryCompilationContext queryCompilationContext)
    : RelationalQueryTranslationPreprocessor(
        dependencies,
        relationalDependencies,
        queryCompilationContext)
{
    protected override Expression ProcessQueryRoots(Expression expression)
        => new GenerateSeriesQueryRootRewritingExpressionVisitor().Visit(
            base.ProcessQueryRoots(expression));

    private sealed class GenerateSeriesQueryRootRewritingExpressionVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskDbFunctionsExtensions)
                && methodCallExpression.Method.Name == nameof(BlueTuskDbFunctionsExtensions.GenerateSeries))
            {
                return new BlueTuskSetReturningFunctionQueryRootExpression(
                    "generate_series",
                    methodCallExpression.Method.ReturnType.GetGenericArguments()[0],
                    methodCallExpression.Arguments.Skip(1).Select(argument => Visit(argument)!).ToArray(),
                    Enumerable.Repeat<string?>(null, methodCallExpression.Arguments.Count - 1).ToArray(),
                    resultStoreType: null,
                    isNullable: false,
                    withOrdinality: false);
            }

            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskDbFunctionsExtensions)
                && TryGetJsonRecordFunction(methodCallExpression.Method.Name, out var recordSpecification))
            {
                return new BlueTuskRecordSetReturningFunctionQueryRootExpression(
                    recordSpecification.Name!,
                    methodCallExpression.Method.ReturnType.GetGenericArguments()[0],
                    methodCallExpression.Arguments.Skip(1).Select(argument => Visit(argument)!).ToArray(),
                    ["jsonb"],
                    recordSpecification.ValueStoreType,
                    recordSpecification.IsValueNullable);
            }

            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskDbFunctionsExtensions)
                && TryGetJsonFunction(methodCallExpression.Method.Name, out var specification))
            {
                return new BlueTuskSetReturningFunctionQueryRootExpression(
                    specification.Name!,
                    typeof(string),
                    methodCallExpression.Arguments.Skip(1).Select(argument => Visit(argument)!).ToArray(),
                    specification.ArgumentStoreTypes,
                    specification.ResultStoreType,
                    specification.IsNullable,
                    withOrdinality: true);
            }

            return base.VisitMethodCall(methodCallExpression);
        }

        private static bool TryGetJsonFunction(
            string methodName,
            out JsonSetReturningFunctionSpecification specification)
        {
            specification = methodName switch
            {
                nameof(BlueTuskDbFunctionsExtensions.JsonArrayElements) =>
                    new("jsonb_array_elements", ["jsonb"], "jsonb", IsNullable: false),
                nameof(BlueTuskDbFunctionsExtensions.JsonArrayElementsText) =>
                    new("jsonb_array_elements_text", ["jsonb"], "text", IsNullable: true),
                nameof(BlueTuskDbFunctionsExtensions.JsonObjectKeys) =>
                    new("jsonb_object_keys", ["jsonb"], "text", IsNullable: false),
                nameof(BlueTuskDbFunctionsExtensions.JsonPathQuery) =>
                    new("jsonb_path_query", ["jsonb", "jsonpath"], "jsonb", IsNullable: false),
                _ => default,
            };

            return specification.Name is not null;
        }

        private static bool TryGetJsonRecordFunction(
            string methodName,
            out JsonRecordSetReturningFunctionSpecification specification)
        {
            specification = methodName switch
            {
                nameof(BlueTuskDbFunctionsExtensions.JsonEach) =>
                    new("jsonb_each", "jsonb", IsValueNullable: false),
                nameof(BlueTuskDbFunctionsExtensions.JsonEachText) =>
                    new("jsonb_each_text", "text", IsValueNullable: true),
                _ => default,
            };

            return specification.Name is not null;
        }

        private readonly record struct JsonSetReturningFunctionSpecification(
            string? Name,
            IReadOnlyList<string?> ArgumentStoreTypes,
            string ResultStoreType,
            bool IsNullable);

        private readonly record struct JsonRecordSetReturningFunctionSpecification(
            string? Name,
            string ValueStoreType,
            bool IsValueNullable);
    }
}
