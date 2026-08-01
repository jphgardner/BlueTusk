using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQueryTranslationPreprocessor
    : RelationalQueryTranslationPreprocessor
{
    private readonly IModel _model;

    public BlueTuskQueryTranslationPreprocessor(
        QueryTranslationPreprocessorDependencies dependencies,
        RelationalQueryTranslationPreprocessorDependencies relationalDependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
        _model = queryCompilationContext.Model;
    }

    protected override Expression ProcessQueryRoots(Expression expression)
        => new GenerateSeriesQueryRootRewritingExpressionVisitor(_model).Visit(
            base.ProcessQueryRoots(expression));

    private sealed class GenerateSeriesQueryRootRewritingExpressionVisitor(IModel model)
        : ExpressionVisitor
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
                && methodCallExpression.Method.Name == nameof(BlueTuskDbFunctionsExtensions.Unnest))
            {
                var elementType = methodCallExpression.Method.ReturnType.GetGenericArguments()[0];
                var pairTypes = elementType.GetGenericArguments();
                return new BlueTuskRecordSetReturningFunctionQueryRootExpression(
                    "unnest",
                    elementType,
                    methodCallExpression.Arguments.Skip(1).Select(argument => Visit(argument)!).ToArray(),
                    [null, null],
                    [
                        new BlueTuskSetReturningFunctionColumn(
                            "first",
                            pairTypes[0],
                            StoreType: null,
                            IsNullable: true),
                        new BlueTuskSetReturningFunctionColumn(
                            "second",
                            pairTypes[1],
                            StoreType: null,
                            IsNullable: true),
                    ]);
            }

            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskDbFunctionsExtensions)
                && methodCallExpression.Method.Name == nameof(BlueTuskDbFunctionsExtensions.JsonToRecordset))
            {
                var elementType = methodCallExpression.Method.GetGenericArguments()[0];
                var entityType = model.FindEntityType(elementType)
                    ?? throw new InvalidOperationException(
                        $"The JSON recordset row type '{elementType.Name}' is not part of the EF model. "
                        + "Register it as a keyless entity before using JsonToRecordset.");
                if (entityType.FindPrimaryKey() is not null)
                {
                    throw new InvalidOperationException(
                        $"The JSON recordset row type '{elementType.Name}' must be configured as keyless.");
                }

                if (entityType.BaseType is not null
                    || entityType.GetDirectlyDerivedTypes().Any()
                    || entityType.GetNavigations().Any()
                    || entityType.GetComplexProperties().Any())
                {
                    throw new InvalidOperationException(
                        $"The JSON recordset row type '{elementType.Name}' must be a flat keyless entity "
                        + "without inheritance, navigations, or complex properties.");
                }

                return new BlueTuskJsonToRecordsetQueryRootExpression(
                    entityType,
                    Visit(methodCallExpression.Arguments[1])!);
            }

            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskDbFunctionsExtensions)
                && TryGetJsonRecordFunction(methodCallExpression.Method.Name, out var recordSpecification))
            {
                return new BlueTuskRecordSetReturningFunctionQueryRootExpression(
                    recordSpecification.Name!,
                    methodCallExpression.Method.ReturnType.GetGenericArguments()[0],
                    methodCallExpression.Arguments.Skip(1).Select(argument => Visit(argument)!).ToArray(),
                    ["jsonb"],
                    [
                        new BlueTuskSetReturningFunctionColumn(
                            "key",
                            typeof(string),
                            "text",
                            IsNullable: false),
                        new BlueTuskSetReturningFunctionColumn(
                            "value",
                            typeof(string),
                            recordSpecification.ValueStoreType,
                            recordSpecification.IsValueNullable),
                    ]);
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
