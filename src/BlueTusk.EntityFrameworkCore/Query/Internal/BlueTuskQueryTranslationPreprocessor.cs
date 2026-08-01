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
            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskQueryableExtensions)
                && methodCallExpression.Method.Name == nameof(BlueTuskQueryableExtensions.RecursiveDescendants))
            {
                if (Visit(methodCallExpression.Arguments[0]) is not EntityQueryRootExpression root)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL recursive hierarchy traversal must be applied directly to one mapped table root; compose filters, projections, and ordering afterward.");
                }

                if (root.EntityType.BaseType is not null
                    || root.EntityType.GetDirectlyDerivedTypes().Any()
                    || root.EntityType.GetDeclaredQueryFilters().Count != 0)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL recursive hierarchy traversal requires a non-inherited entity without a global query filter.");
                }

                var selector = (LambdaExpression)((UnaryExpression)methodCallExpression.Arguments[1]).Operand;
                if (selector.Body is not MethodCallExpression
                    {
                        Method.DeclaringType: not null,
                        Method.Name: nameof(ValueTuple.Create),
                        Arguments: [MemberExpression key, MemberExpression parentKey],
                    } tuple
                    || tuple.Method.DeclaringType != typeof(ValueTuple)
                    || key.Expression != selector.Parameters[0]
                    || parentKey.Expression != selector.Parameters[0]
                    || root.EntityType.FindProperty(key.Member.Name) is null
                    || root.EntityType.FindProperty(parentKey.Member.Name) is null)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL recursive hierarchy selectors must use ValueTuple.Create with key and parent-key mapped properties directly.");
                }

                if (methodCallExpression.Arguments[3] is not ConstantExpression
                    {
                        Value: BlueTuskRecursiveUnionBehavior unionBehavior,
                    })
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL recursive UNION behavior must be a constant value.");
                }

                return new BlueTuskRecursiveCteQueryRootExpression(
                    root.EntityType,
                    key.Member.Name,
                    parentKey.Member.Name,
                    Visit(methodCallExpression.Arguments[2]),
                    unionBehavior);
            }

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
                && TryGetNativeSetFunction(
                    methodCallExpression.Method.Name,
                    methodCallExpression.Arguments.Count - 1,
                    out var nativeSpecification))
            {
                return new BlueTuskSetReturningFunctionQueryRootExpression(
                    nativeSpecification.Name!,
                    methodCallExpression.Method.ReturnType.GetGenericArguments()[0],
                    methodCallExpression.Arguments.Skip(1).Select(argument => Visit(argument)!).ToArray(),
                    nativeSpecification.ArgumentStoreTypes,
                    nativeSpecification.ResultStoreType,
                    nativeSpecification.IsNullable,
                    withOrdinality: true);
            }

            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskDbFunctionsExtensions)
                && methodCallExpression.Method.Name == nameof(BlueTuskDbFunctionsExtensions.Unnest))
            {
                var elementType = methodCallExpression.Method.ReturnType.GetGenericArguments()[0];
                var columns = GetUnnestColumns(elementType, methodCallExpression.Arguments.Count - 1);
                return new BlueTuskRecordSetReturningFunctionQueryRootExpression(
                    "unnest",
                    elementType,
                    methodCallExpression.Arguments.Skip(1).Select(argument => Visit(argument)!).ToArray(),
                    Enumerable.Repeat<string?>(null, columns.Length).ToArray(),
                    columns);
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
                && TryGetJsonFunction(
                    methodCallExpression.Method.Name,
                    methodCallExpression.Arguments.Count - 1,
                    out var specification))
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

        private static BlueTuskSetReturningFunctionColumn[] GetUnnestColumns(
            Type elementType,
            int argumentCount)
        {
            if (!elementType.IsGenericType)
            {
                throw new InvalidOperationException(
                    $"The multi-array unnest row type '{elementType.Name}' is not supported.");
            }

            var definition = elementType.GetGenericTypeDefinition();
            var expectedCount = definition == typeof(KeyValuePair<,>)
                || definition == typeof(BlueTuskUnnestPair<,>)
                    ? 2
                    : definition == typeof(BlueTuskUnnestTriple<,,>)
                        ? 3
                        : definition == typeof(BlueTuskUnnestQuadruple<,,,>)
                            ? 4
                            : 0;
            if (expectedCount == 0 || argumentCount != expectedCount)
            {
                throw new InvalidOperationException(
                    $"The multi-array unnest row type '{elementType.Name}' does not match its {argumentCount} inputs.");
            }

            var elementTypes = elementType.GetGenericArguments();
            if (definition != typeof(KeyValuePair<,>)
                && elementTypes.Any(type => type.IsValueType && Nullable.GetUnderlyingType(type) is null))
            {
                throw new InvalidOperationException(
                    "Generic multi-array unnest inputs with value-type elements must use nullable element "
                    + "arrays because PostgreSQL pads shorter arrays with NULL.");
            }

            string[] names = ["first", "second", "third", "fourth"];
            return elementTypes
                .Select((type, index) => new BlueTuskSetReturningFunctionColumn(
                    names[index],
                    type,
                    StoreType: null,
                    IsNullable: true))
                .ToArray();
        }

        private static bool TryGetJsonFunction(
            string methodName,
            int argumentCount,
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
                    new(
                        "jsonb_path_query",
                        argumentCount == 2
                            ? ["jsonb", "jsonpath"]
                            : ["jsonb", "jsonpath", "jsonb", "boolean"],
                        "jsonb",
                        IsNullable: false),
                _ => default,
            };

            return specification.Name is not null;
        }

        private static bool TryGetNativeSetFunction(
            string methodName,
            int argumentCount,
            out JsonSetReturningFunctionSpecification specification)
        {
            specification = methodName switch
            {
                nameof(BlueTuskDbFunctionsExtensions.GenerateSubscripts) =>
                    new(
                        "generate_subscripts",
                        argumentCount == 2 ? [null, "integer"] : [null, "integer", "boolean"],
                        "integer",
                        IsNullable: false),
                nameof(BlueTuskDbFunctionsExtensions.RegexMatches) =>
                    new(
                        "regexp_matches",
                        Enumerable.Repeat<string?>("text", argumentCount).ToArray(),
                        null,
                        IsNullable: false),
                nameof(BlueTuskDbFunctionsExtensions.RegexSplitToTable) =>
                    new(
                        "regexp_split_to_table",
                        Enumerable.Repeat<string?>("text", argumentCount).ToArray(),
                        "text",
                        IsNullable: false),
                nameof(BlueTuskDbFunctionsExtensions.StringToTable) =>
                    new(
                        "string_to_table",
                        Enumerable.Repeat<string?>("text", argumentCount).ToArray(),
                        "text",
                        IsNullable: true),
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
            string? ResultStoreType,
            bool IsNullable);

        private readonly record struct JsonRecordSetReturningFunctionSpecification(
            string? Name,
            string ValueStoreType,
            bool IsValueNullable);
    }
}
