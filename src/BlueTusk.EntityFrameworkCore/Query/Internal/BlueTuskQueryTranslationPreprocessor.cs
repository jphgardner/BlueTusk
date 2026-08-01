using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
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
                && methodCallExpression.Method.Name == nameof(BlueTuskQueryableExtensions.InsertOnConflictReturningCore))
            {
                var (root, noTracking) = GetInsertTarget(Visit(methodCallExpression.Arguments[0]));
                if (methodCallExpression.Arguments[1] is not NewArrayExpression { Expressions.Count: > 0 } valueArray
                    || methodCallExpression.Arguments[2] is not ConstantExpression { Value: string[] conflictProperties }
                    || methodCallExpression.Arguments[3] is not ConstantExpression { Value: string[] updateProperties })
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL INSERT ON CONFLICT values could not be normalized from '{methodCallExpression.Arguments[1]}'.");
                }

                var values = new BlueTuskInsertPropertyValue[valueArray.Expressions.Count];
                for (var index = 0; index < valueArray.Expressions.Count; index++)
                {
                    if (valueArray.Expressions[index] is not MethodCallExpression
                        {
                            Method.Name: nameof(BlueTuskQueryableExtensions.InsertValueCore),
                            Arguments: [ConstantExpression { Value: string propertyName }, var value],
                        }
                        || root.EntityType.FindProperty(propertyName) is null)
                    {
                        throw new InvalidOperationException(
                            "PostgreSQL INSERT ON CONFLICT values must assign direct mapped properties.");
                    }

                    if (value is UnaryExpression
                        {
                            NodeType: ExpressionType.Convert,
                            Type: { } conversionType,
                            Operand: var operand,
                        }
                        && conversionType == typeof(object))
                    {
                        value = operand;
                    }

                    values[index] = new BlueTuskInsertPropertyValue(propertyName, value);
                }

                var insertRoot = CreateInsertRoot(
                    root,
                    values,
                    conflictProperties,
                    updateProperties);
                return noTracking is null
                    ? insertRoot
                    : noTracking.Update(null, [insertRoot]);
            }

            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskQueryableExtensions)
                && methodCallExpression.Method.Name is
                    nameof(BlueTuskQueryableExtensions.InsertOnConflictDoNothingReturning)
                    or nameof(BlueTuskQueryableExtensions.InsertOnConflictUpdateReturning))
            {
                var (root, noTracking) = GetInsertTarget(Visit(methodCallExpression.Arguments[0]));

                var valuesSelector = (LambdaExpression)((UnaryExpression)methodCallExpression.Arguments[1]).Operand;
                var values = GetInsertValues(valuesSelector, root.EntityType);
                var conflictSelector = (LambdaExpression)((UnaryExpression)methodCallExpression.Arguments[2]).Operand;
                var conflictProperties = GetSelectedProperties(
                    conflictSelector,
                    root.EntityType,
                    "conflict target");
                var updateProperties = methodCallExpression.Arguments.Count == 4
                    ? GetSelectedProperties(
                        (LambdaExpression)((UnaryExpression)methodCallExpression.Arguments[3]).Operand,
                        root.EntityType,
                        "conflict update")
                    : [];
                var insertRoot = CreateInsertRoot(
                    root,
                    values,
                    conflictProperties,
                    updateProperties);
                return noTracking is null
                    ? insertRoot
                    : noTracking.Update(null, [insertRoot]);
            }

            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskQueryableExtensions)
                && methodCallExpression.Method.Name == nameof(BlueTuskQueryableExtensions.UpdateReturning)
                && methodCallExpression.Arguments.Count == 3)
            {
                var propertySelector = ((UnaryExpression)methodCallExpression.Arguments[1]).Operand;
                var valueSelector = ((UnaryExpression)methodCallExpression.Arguments[2]).Operand;
                var setterConstructor = typeof(Tuple<Delegate, object>)
                    .GetConstructor([typeof(Delegate), typeof(object)])!;
                var setters = Expression.NewArrayInit(
                    typeof(ITuple),
                    Expression.New(setterConstructor, propertySelector, valueSelector));
                var marker = typeof(BlueTuskQueryableExtensions)
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                    .Single(method =>
                        method.Name == nameof(BlueTuskQueryableExtensions.UpdateReturningCore)
                        && method.IsGenericMethodDefinition)
                    .MakeGenericMethod(methodCallExpression.Method.GetGenericArguments()[0]);
                return Expression.Call(
                    marker,
                    Visit(methodCallExpression.Arguments[0]),
                    setters);
            }

            if (methodCallExpression.Method.DeclaringType == typeof(BlueTuskQueryableExtensions)
                && methodCallExpression.Method.Name == nameof(BlueTuskQueryableExtensions.UpdateReturning)
                && methodCallExpression.Arguments.Count == 2)
            {
                throw new InvalidOperationException(
                    "Compiled PostgreSQL UPDATE RETURNING queries must use the property/value-selector overload.");
            }

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

        private static (EntityQueryRootExpression Root, MethodCallExpression? NoTracking) GetInsertTarget(
            Expression target)
        {
            if (target is MethodCallExpression
                {
                    Method.DeclaringType: not null,
                    Method.Name: nameof(EntityFrameworkQueryableExtensions.AsNoTracking),
                    Arguments: [EntityQueryRootExpression noTrackingRoot],
                } noTracking
                && noTracking.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions))
            {
                return (noTrackingRoot, noTracking);
            }

            if (target is EntityQueryRootExpression root)
            {
                return (root, null);
            }

            throw new InvalidOperationException(
                "PostgreSQL INSERT ON CONFLICT RETURNING must target a DbSet directly.");
        }

        private static BlueTuskInsertOnConflictQueryRootExpression CreateInsertRoot(
            EntityQueryRootExpression root,
            IReadOnlyList<BlueTuskInsertPropertyValue> values,
            IReadOnlyList<string> conflictProperties,
            IReadOnlyList<string> updateProperties)
        {
            if (root.EntityType.BaseType is not null
                || root.EntityType.GetDirectlyDerivedTypes().Any()
                || root.EntityType.GetDeclaredQueryFilters().Count != 0)
            {
                throw new InvalidOperationException(
                    "PostgreSQL INSERT ON CONFLICT RETURNING requires a non-inherited entity without a global query filter.");
            }

            foreach (var property in conflictProperties.Concat(updateProperties))
            {
                if (root.EntityType.FindProperty(property) is null)
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL INSERT ON CONFLICT property '{property}' is not mapped.");
                }
            }

            return new BlueTuskInsertOnConflictQueryRootExpression(
                root.EntityType,
                values,
                conflictProperties,
                updateProperties);
        }

        private static BlueTuskInsertPropertyValue[] GetInsertValues(
            LambdaExpression selector,
            IEntityType entityType)
        {
            if (selector.Body is not MemberInitExpression initializer
                || initializer.NewExpression.Type != entityType.ClrType
                || initializer.Bindings.Count == 0)
            {
                throw new InvalidOperationException(
                    "PostgreSQL INSERT ON CONFLICT values must be a non-empty mapped entity object initializer.");
            }

            var values = new BlueTuskInsertPropertyValue[initializer.Bindings.Count];
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < initializer.Bindings.Count; index++)
            {
                if (initializer.Bindings[index] is not MemberAssignment assignment
                    || entityType.FindProperty(assignment.Member.Name) is null
                    || !propertyNames.Add(assignment.Member.Name))
                {
                    throw new InvalidOperationException(
                        "PostgreSQL INSERT ON CONFLICT values must assign each direct mapped property at most once.");
                }

                values[index] = new BlueTuskInsertPropertyValue(
                    assignment.Member.Name,
                    assignment.Expression);
            }

            return values;
        }

        private static string[] GetSelectedProperties(
            LambdaExpression selector,
            IEntityType entityType,
            string role)
        {
            var expressions = selector.Body switch
            {
                NewExpression tuple => tuple.Arguments,
                MethodCallExpression
                {
                    Method.DeclaringType: not null,
                    Method.Name: nameof(ValueTuple.Create),
                } tuple when tuple.Method.DeclaringType == typeof(ValueTuple) => tuple.Arguments,
                _ => [selector.Body],
            };
            var properties = new string[expressions.Count];
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < expressions.Count; index++)
            {
                var expression = expressions[index];
                while (expression is UnaryExpression
                    {
                        NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                    } conversion)
                {
                    expression = conversion.Operand;
                }

                if (expression is not MemberExpression member
                    || member.Expression != selector.Parameters[0]
                    || entityType.FindProperty(member.Member.Name) is null
                    || !propertyNames.Add(member.Member.Name))
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL INSERT ON CONFLICT {role} must select distinct direct mapped properties.");
                }

                properties[index] = member.Member.Name;
            }

            return properties;
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
