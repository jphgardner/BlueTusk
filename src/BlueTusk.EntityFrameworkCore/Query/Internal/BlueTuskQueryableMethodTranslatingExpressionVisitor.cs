using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskQueryableMethodTranslatingExpressionVisitor
    : RelationalQueryableMethodTranslatingExpressionVisitor
{
    private readonly RelationalQueryCompilationContext _queryCompilationContext;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public BlueTuskQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        RelationalQueryCompilationContext queryCompilationContext)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
        _queryCompilationContext = queryCompilationContext;
        _typeMappingSource = relationalDependencies.TypeMappingSource;
    }

    private BlueTuskQueryableMethodTranslatingExpressionVisitor(
        BlueTuskQueryableMethodTranslatingExpressionVisitor parentVisitor)
        : base(parentVisitor)
    {
        _queryCompilationContext = parentVisitor._queryCompilationContext;
        _typeMappingSource = parentVisitor._typeMappingSource;
    }

    protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor()
        => new BlueTuskQueryableMethodTranslatingExpressionVisitor(this);

    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        if (methodCallExpression.Method.DeclaringType != typeof(BlueTuskQueryableExtensions))
        {
            return base.VisitMethodCall(methodCallExpression);
        }

        if (Visit(methodCallExpression.Arguments[0]) is not ShapedQueryExpression source
            || source.QueryExpression is not SelectExpression selectExpression)
        {
            return QueryCompilationContext.NotTranslatedExpression;
        }

        return methodCallExpression.Method.Name switch
        {
            nameof(BlueTuskQueryableExtensions.DeleteReturning) =>
                TranslateReturningModification(
                    source,
                    selectExpression,
                    BlueTuskReturningModificationKind.Delete,
                    []),
            nameof(BlueTuskQueryableExtensions.UpdateReturning)
                when methodCallExpression.Arguments.Count == 3 =>
                TranslateUpdateReturning(
                    source,
                    selectExpression,
                    [
                        new ExecuteUpdateSetter(
                            (LambdaExpression)((UnaryExpression)methodCallExpression.Arguments[1]).Operand,
                            (LambdaExpression)((UnaryExpression)methodCallExpression.Arguments[2]).Operand),
                    ]),
            nameof(BlueTuskQueryableExtensions.UpdateReturning) =>
                throw new InvalidOperationException(
                    "Compiled PostgreSQL UPDATE RETURNING queries must use the property/value-selector overload."),
            nameof(BlueTuskQueryableExtensions.UpdateReturningCore) => TranslateUpdateReturning(
                source,
                selectExpression,
                methodCallExpression),
            nameof(BlueTuskQueryableExtensions.DistinctOn) =>
                TranslateDistinctOn(source, selectExpression, methodCallExpression),
            nameof(BlueTuskQueryableExtensions.TableSampleSystem) =>
                TranslateTableSample(
                    source,
                    selectExpression,
                    methodCallExpression,
                    BlueTuskTableSampleMethod.System),
            nameof(BlueTuskQueryableExtensions.TableSampleBernoulli) =>
                TranslateTableSample(
                    source,
                    selectExpression,
                    methodCallExpression,
                    BlueTuskTableSampleMethod.Bernoulli),
            nameof(BlueTuskQueryableExtensions.ForUpdate) =>
                TranslateRowLocking(
                    source,
                    selectExpression,
                    methodCallExpression,
                    BlueTuskRowLockingStrength.Update),
            nameof(BlueTuskQueryableExtensions.ForNoKeyUpdate) =>
                TranslateRowLocking(
                    source,
                    selectExpression,
                    methodCallExpression,
                    BlueTuskRowLockingStrength.NoKeyUpdate),
            nameof(BlueTuskQueryableExtensions.ForShare) =>
                TranslateRowLocking(
                    source,
                    selectExpression,
                    methodCallExpression,
                    BlueTuskRowLockingStrength.Share),
            nameof(BlueTuskQueryableExtensions.ForKeyShare) =>
                TranslateRowLocking(
                    source,
                    selectExpression,
                    methodCallExpression,
                    BlueTuskRowLockingStrength.KeyShare),
            nameof(BlueTuskQueryableExtensions.AsCte) =>
                TranslateCte(
                    source,
                    selectExpression,
                    methodCallExpression,
                    BlueTuskCteMaterialization.Default),
            nameof(BlueTuskQueryableExtensions.AsMaterializedCte) =>
                TranslateCte(
                    source,
                    selectExpression,
                    methodCallExpression,
                    BlueTuskCteMaterialization.Materialized),
            nameof(BlueTuskQueryableExtensions.AsNotMaterializedCte) =>
                TranslateCte(
                    source,
                    selectExpression,
                    methodCallExpression,
                    BlueTuskCteMaterialization.NotMaterialized),
            _ => base.VisitMethodCall(methodCallExpression),
        };
    }

    private ShapedQueryExpression TranslateUpdateReturning(
        ShapedQueryExpression source,
        SelectExpression selectExpression,
        MethodCallExpression methodCallExpression)
    {
        if (methodCallExpression.Arguments[1] is not NewArrayExpression { Expressions.Count: > 0 } setterArray)
        {
            throw new InvalidOperationException(
                "PostgreSQL UPDATE RETURNING requires at least one SetProperty invocation.");
        }

        var setters = new ExecuteUpdateSetter[setterArray.Expressions.Count];
        for (var setterIndex = 0; setterIndex < setters.Length; setterIndex++)
        {
            if (setterArray.Expressions[setterIndex] is not NewExpression
                {
                    Arguments: [LambdaExpression propertySelector, var valueSelector],
                })
            {
                throw new InvalidOperationException(
                    "PostgreSQL UPDATE RETURNING accepts only SetProperty invocations.");
            }

            if (valueSelector is UnaryExpression
                {
                    NodeType: ExpressionType.Convert,
                    Operand: var unwrappedValueSelector,
                    Type: { } conversionType,
                }
                && conversionType == typeof(object))
            {
                valueSelector = unwrappedValueSelector;
            }

            setters[setterIndex] = new ExecuteUpdateSetter(propertySelector, valueSelector);
        }

        return TranslateUpdateReturning(source, selectExpression, setters);
    }

    private ShapedQueryExpression TranslateUpdateReturning(
        ShapedQueryExpression source,
        SelectExpression selectExpression,
        IReadOnlyList<ExecuteUpdateSetter> setters)
    {
#pragma warning disable EF1001 // Provider translation reuses EF's canonical ExecuteUpdate setter translator.
        var settersTranslated = TryTranslateSetters(
            source,
            setters,
            out var translatedSetters,
            out var targetTable);
#pragma warning restore EF1001
        if (!settersTranslated
            || targetTable is not TableExpression table
            || selectExpression.Tables is not [TableExpression sourceTable]
            || table.Name != sourceTable.Name
            || table.Schema != sourceTable.Schema
            || table.Alias != sourceTable.Alias)
        {
            throw new InvalidOperationException(
                "PostgreSQL UPDATE RETURNING requires setters that target the query's single mapped table.");
        }

        return TranslateReturningModification(
            source,
            selectExpression,
            BlueTuskReturningModificationKind.Update,
            translatedSetters!);
    }

    private ShapedQueryExpression TranslateReturningModification(
        ShapedQueryExpression source,
        SelectExpression selectExpression,
        BlueTuskReturningModificationKind kind,
        IReadOnlyList<ColumnValueSetter> setters)
    {
        if (_queryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.TrackAll)
        {
            throw new InvalidOperationException(
                "PostgreSQL data-modification RETURNING queries must be no-tracking; call AsNoTracking in a compiled query.");
        }

        if (selectExpression is not
            {
                Tables: [TableExpression table],
                Offset: null,
                Limit: null,
                IsDistinct: false,
                GroupBy: [],
                Having: null,
                Orderings: [],
            }
            || table.FindAnnotation(BlueTuskQueryAnnotationNames.CommonTableExpression) is not null
            || table.FindAnnotation(BlueTuskQueryAnnotationNames.RecursiveCommonTableExpression) is not null
            || table.FindAnnotation(BlueTuskQueryAnnotationNames.TableSample) is not null
            || table.FindAnnotation(BlueTuskQueryAnnotationNames.RowLocking) is not null)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {kind.ToString().ToUpperInvariant()} RETURNING requires a single mapped table with optional predicates and projection, without ordering, paging, distinct, grouping, sampling, locking, or a CTE.");
        }

        AddQueryAnnotation(
            selectExpression,
            BlueTuskQueryAnnotationNames.DataModificationReturning,
            new BlueTuskReturningModificationClause(kind, setters));
        return source;
    }

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        if (extensionExpression is BlueTuskSetReturningFunctionQueryRootExpression function)
        {
            return TranslateSetReturningFunction(function) ?? base.VisitExtension(extensionExpression);
        }

        if (extensionExpression is BlueTuskRecordSetReturningFunctionQueryRootExpression recordFunction)
        {
            return TranslateRecordSetReturningFunction(recordFunction)
                ?? base.VisitExtension(extensionExpression);
        }

        if (extensionExpression is BlueTuskJsonToRecordsetQueryRootExpression jsonRecordset)
        {
            return TranslateJsonToRecordset(jsonRecordset)
                ?? base.VisitExtension(extensionExpression);
        }

        if (extensionExpression is BlueTuskRecursiveCteQueryRootExpression recursiveCte)
        {
            return TranslateRecursiveDescendants(recursiveCte)
                ?? base.VisitExtension(extensionExpression);
        }

        if (extensionExpression is BlueTuskInsertOnConflictQueryRootExpression insertOnConflict)
        {
            return TranslateInsertOnConflict(insertOnConflict)
                ?? base.VisitExtension(extensionExpression);
        }

        return base.VisitExtension(extensionExpression);
    }

    private ShapedQueryExpression? TranslateInsertOnConflict(
        BlueTuskInsertOnConflictQueryRootExpression insertOnConflict)
    {
        if (_queryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.TrackAll)
        {
            throw new InvalidOperationException(
                "PostgreSQL INSERT ON CONFLICT RETURNING queries must be no-tracking; call AsNoTracking in a compiled query.");
        }

        if (base.VisitExtension(new EntityQueryRootExpression(insertOnConflict.EntityType))
                is not ShapedQueryExpression source
            || source.QueryExpression is not SelectExpression { Tables: [TableExpression table] }
                selectExpression)
        {
            return null;
        }

        var tableName = insertOnConflict.EntityType.GetTableName();
        if (tableName is null
            || insertOnConflict.EntityType.Model.GetRelationalModel().FindTable(
                tableName,
                insertOnConflict.EntityType.GetSchema()) is not { } relationalTable)
        {
            return null;
        }

        var values = new BlueTuskInsertColumnValue[insertOnConflict.Values.Count];
        for (var index = 0; index < insertOnConflict.Values.Count; index++)
        {
            var propertyValue = insertOnConflict.Values[index];
            if (insertOnConflict.EntityType.FindProperty(propertyValue.PropertyName) is not { } property
                || relationalTable.FindColumn(property) is not { } column
                || TranslateExpression(propertyValue.Value, applyDefaultTypeMapping: false) is not { } value)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL INSERT ON CONFLICT value for mapped property '{propertyValue.PropertyName}' could not be translated from expression '{propertyValue.Value}'.");
            }

            values[index] = new BlueTuskInsertColumnValue(
                column.Name,
                RelationalDependencies.SqlExpressionFactory.ApplyTypeMapping(
                    value,
                    column.StoreTypeMapping));
        }

        var conflictColumns = GetInsertColumns(
            insertOnConflict.EntityType,
            relationalTable,
            insertOnConflict.ConflictProperties,
            "conflict target");
        var updateColumns = GetInsertColumns(
            insertOnConflict.EntityType,
            relationalTable,
            insertOnConflict.UpdateProperties,
            "conflict update");
        AddQueryAnnotation(
            selectExpression,
            BlueTuskQueryAnnotationNames.InsertOnConflictReturning,
            new BlueTuskInsertOnConflictClause(values, conflictColumns, updateColumns));
        return source;
    }

    private static string[] GetInsertColumns(
        IEntityType entityType,
        ITable relationalTable,
        IReadOnlyList<string> propertyNames,
        string role)
    {
        var columns = new string[propertyNames.Count];
        for (var index = 0; index < propertyNames.Count; index++)
        {
            if (entityType.FindProperty(propertyNames[index]) is not { } property
                || relationalTable.FindColumn(property) is not { } column)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL INSERT ON CONFLICT {role} property '{propertyNames[index]}' is not mapped to the target table.");
            }

            columns[index] = column.Name;
        }

        return columns;
    }

    private ShapedQueryExpression TranslateDistinctOn(
        ShapedQueryExpression source,
        SelectExpression selectExpression,
        MethodCallExpression methodCallExpression)
    {
        if (selectExpression.IsDistinct)
        {
            throw new InvalidOperationException(
                "PostgreSQL DISTINCT ON cannot be combined with LINQ Distinct on the same query.");
        }

        var lambda = (LambdaExpression)((UnaryExpression)methodCallExpression.Arguments[1]).Operand;
        var key = TranslateLambdaExpression(source, lambda)
            ?? throw new InvalidOperationException(
                "The PostgreSQL DISTINCT ON key could not be translated to a scalar SQL expression.");
        if (selectExpression.Orderings.Count > 0
            && !selectExpression.Orderings[0].Expression.Equals(key))
        {
            throw new InvalidOperationException(
                "PostgreSQL DISTINCT ON keys must match the leftmost ORDER BY expression.");
        }

        AddQueryAnnotation(selectExpression, BlueTuskQueryAnnotationNames.DistinctOn, key);
        return source;
    }

    private ShapedQueryExpression TranslateTableSample(
        ShapedQueryExpression source,
        SelectExpression selectExpression,
        MethodCallExpression methodCallExpression,
        BlueTuskTableSampleMethod method)
    {
        if (selectExpression.Tables is not [TableExpression])
        {
            throw new InvalidOperationException(
                "PostgreSQL TABLESAMPLE must be applied to a single mapped table query.");
        }

        var percentage = TranslateExpression(methodCallExpression.Arguments[1])
            ?? throw new InvalidOperationException(
                "The PostgreSQL TABLESAMPLE percentage could not be translated.");
        var repeatable = methodCallExpression.Arguments.Count == 3
            ? TranslateExpression(methodCallExpression.Arguments[2])
                ?? throw new InvalidOperationException(
                    "The PostgreSQL TABLESAMPLE repeatable seed could not be translated.")
            : null;
        AddQueryAnnotation(
            selectExpression,
            BlueTuskQueryAnnotationNames.TableSample,
            new BlueTuskTableSampleClause(method, percentage, repeatable));
        return source;
    }

    private static ShapedQueryExpression TranslateRowLocking(
        ShapedQueryExpression source,
        SelectExpression selectExpression,
        MethodCallExpression methodCallExpression,
        BlueTuskRowLockingStrength strength)
    {
        if (methodCallExpression.Arguments[1] is not ConstantExpression
            {
                Value: BlueTuskRowLockingBehavior behavior,
            })
        {
            throw new InvalidOperationException(
                "The PostgreSQL row-locking behavior must be a constant value.");
        }

        AddQueryAnnotation(
            selectExpression,
            BlueTuskQueryAnnotationNames.RowLocking,
            new BlueTuskRowLockingClause(strength, behavior));
        return source;
    }

    private static ShapedQueryExpression TranslateCte(
        ShapedQueryExpression source,
        SelectExpression selectExpression,
        MethodCallExpression methodCallExpression,
        BlueTuskCteMaterialization materialization)
    {
        if (methodCallExpression.Arguments[1] is not ConstantExpression { Value: string name })
        {
            throw new InvalidOperationException(
                "The PostgreSQL CTE name must be a constant value.");
        }

        if (selectExpression.Tables[0].FindAnnotation(
                BlueTuskQueryAnnotationNames.RecursiveCommonTableExpression) is not null)
        {
            throw new InvalidOperationException(
                "A recursive hierarchy query cannot also be wrapped in another PostgreSQL CTE.");
        }

        AddQueryAnnotation(
            selectExpression,
            BlueTuskQueryAnnotationNames.CommonTableExpression,
            new BlueTuskCteClause(name, materialization));
        return source;
    }

    private ShapedQueryExpression? TranslateRecursiveDescendants(
        BlueTuskRecursiveCteQueryRootExpression recursiveCte)
    {
        if (base.VisitExtension(new EntityQueryRootExpression(recursiveCte.EntityType))
                is not ShapedQueryExpression source
            || source.QueryExpression is not SelectExpression { Tables: [TableExpression table] }
                selectExpression)
        {
            return null;
        }

        var tableName = recursiveCte.EntityType.GetTableName();
        if (tableName is null
            || recursiveCte.EntityType.Model.GetRelationalModel().FindTable(
                tableName,
                recursiveCte.EntityType.GetSchema()) is not { } relationalTable
            || recursiveCte.EntityType.FindProperty(recursiveCte.KeyProperty) is not { } keyProperty
            || recursiveCte.EntityType.FindProperty(recursiveCte.ParentKeyProperty) is not { } parentKeyProperty
            || relationalTable.FindColumn(keyProperty) is not { } key
            || relationalTable.FindColumn(parentKeyProperty) is not { } parentKey)
        {
            return null;
        }

        if (!string.Equals(
                key.StoreTypeMapping.StoreType,
                parentKey.StoreTypeMapping.StoreType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PostgreSQL recursive hierarchy key and parent-key columns must use the same store type.");
        }

        var roots = TranslateExpression(recursiveCte.Roots)
            ?? throw new InvalidOperationException(
                "The PostgreSQL recursive hierarchy root keys could not be translated as an array parameter.");
        AddQueryAnnotation(
            selectExpression,
            BlueTuskQueryAnnotationNames.RecursiveCommonTableExpression,
            new BlueTuskRecursiveCteClause(
                $"bluetusk_{table.Alias}_hierarchy",
                table.Name,
                table.Schema,
                key.Name,
                parentKey.Name,
                roots,
                recursiveCte.UnionBehavior));
        return source;
    }

    private static void AddQueryAnnotation(
        SelectExpression selectExpression,
        string name,
        object value)
    {
        if (selectExpression.Tables.Count == 0)
        {
            throw new InvalidOperationException(
                "The PostgreSQL query operation requires a relational table source.");
        }

        if (selectExpression.Tables[0].FindAnnotation(name) is not null)
        {
            throw new InvalidOperationException(
                $"The PostgreSQL query operation '{name}' can be applied only once.");
        }

        var tables = selectExpression.Tables.ToArray();
        tables[0] = tables[0].AddAnnotation(name, value);
#pragma warning disable EF1001 // Provider query annotations require replacing the relational table node.
        selectExpression.SetTables(tables);
#pragma warning restore EF1001
    }

    protected override ShapedQueryExpression? TranslatePrimitiveCollection(
        SqlExpression sqlExpression,
        IProperty? property,
        string tableAlias)
    {
        var elementClrType = GetElementType(sqlExpression.Type);
        if (elementClrType is null)
        {
            return null;
        }

        var elementType = Nullable.GetUnderlyingType(elementClrType) ?? elementClrType;
        var elementTypeMapping = (RelationalTypeMapping?)sqlExpression.TypeMapping?.ElementTypeMapping
            ?? _typeMappingSource.FindMapping(elementType);
        if (elementTypeMapping is null)
        {
            return null;
        }

        var isElementNullable = property?.GetElementType()?.IsNullable
            ?? (!elementClrType.IsValueType || Nullable.GetUnderlyingType(elementClrType) is not null);
        var ordinalityTypeMapping = _typeMappingSource.FindMapping(typeof(int))!;
        var ordinalityColumn = new ColumnExpression(
            "ordinality",
            tableAlias,
            typeof(int),
            ordinalityTypeMapping,
            nullable: false);

#pragma warning disable EF1001 // SelectExpression constructors are provider-facing infrastructure.
        var selectExpression = new SelectExpression(
            [new BlueTuskUnnestExpression(tableAlias, sqlExpression)],
            new ColumnExpression(
                "value",
                tableAlias,
                elementType,
                elementTypeMapping,
                isElementNullable),
            identifier: [(ordinalityColumn, (ValueComparer)ordinalityTypeMapping.Comparer)],
            _queryCompilationContext.SqlAliasManager);
#pragma warning restore EF1001
        selectExpression.AppendOrdering(new OrderingExpression(ordinalityColumn, ascending: true));

        Expression shaperExpression = new ProjectionBindingExpression(
            selectExpression,
            new ProjectionMember(),
            elementClrType.IsValueType && Nullable.GetUnderlyingType(elementClrType) is null
                ? typeof(Nullable<>).MakeGenericType(elementClrType)
                : elementClrType);
        if (elementClrType != shaperExpression.Type)
        {
            shaperExpression = Expression.Convert(shaperExpression, elementClrType);
        }

        return new ShapedQueryExpression(selectExpression, shaperExpression);
    }

    private ShapedQueryExpression? TranslateSetReturningFunction(
        BlueTuskSetReturningFunctionQueryRootExpression function)
    {
        var elementTypeMapping = function.ResultStoreType is null
            ? _typeMappingSource.FindMapping(function.ElementType, RelationalDependencies.Model)
            : _typeMappingSource.FindMapping(function.ResultStoreType);
        if (elementTypeMapping is null)
        {
            return null;
        }

        var arguments = new SqlExpression[function.Arguments.Count];
        for (var index = 0; index < function.Arguments.Count; index++)
        {
            var argumentStoreType = function.ArgumentStoreTypes[index];
            if (TranslateExpression(
                    function.Arguments[index],
                    applyDefaultTypeMapping: argumentStoreType is null) is not { } argument)
            {
                return null;
            }

            if (argumentStoreType is not null)
            {
                var argumentTypeMapping = _typeMappingSource.FindMapping(argumentStoreType);
                if (argumentTypeMapping is null)
                {
                    return null;
                }

                argument = RelationalDependencies.SqlExpressionFactory.ApplyTypeMapping(
                    argument,
                    argumentTypeMapping);
            }

            arguments[index] = argument;
        }

        var tableAlias = _queryCompilationContext.SqlAliasManager.GenerateTableAlias(function.Name);

        var valueColumn = new ColumnExpression(
            "value",
            tableAlias,
            function.ElementType,
            elementTypeMapping,
            function.IsNullable);
        var identifier = new List<(ColumnExpression Column, ValueComparer Comparer)>();
        ColumnExpression? ordinalityColumn = null;
        if (function.WithOrdinality)
        {
            var ordinalityTypeMapping = _typeMappingSource.FindMapping(typeof(long))!;
            ordinalityColumn = new ColumnExpression(
                "ordinality",
                tableAlias,
                typeof(long),
                ordinalityTypeMapping,
                nullable: false);
            identifier.Add((ordinalityColumn, (ValueComparer)ordinalityTypeMapping.Comparer));
        }
        else
        {
            identifier.Add((valueColumn, (ValueComparer)elementTypeMapping.Comparer));
        }

#pragma warning disable EF1001 // SelectExpression constructors are provider-facing infrastructure.
        var selectExpression = new SelectExpression(
            [
                new BlueTuskSetReturningFunctionTableExpression(
                    tableAlias,
                    function.Name,
                    arguments,
                    ["value"],
                    function.WithOrdinality),
            ],
            valueColumn,
            identifier,
            _queryCompilationContext.SqlAliasManager);
#pragma warning restore EF1001
        if (ordinalityColumn is not null)
        {
            selectExpression.AppendOrdering(new OrderingExpression(ordinalityColumn, ascending: true));
        }

        return new ShapedQueryExpression(
            selectExpression,
            new ProjectionBindingExpression(
                selectExpression,
                new ProjectionMember(),
                function.ElementType));
    }

    private ShapedQueryExpression? TranslateRecordSetReturningFunction(
        BlueTuskRecordSetReturningFunctionQueryRootExpression function)
    {
        if (TranslateSetReturningArguments(
                function.Arguments,
                function.ArgumentStoreTypes) is not { } arguments)
        {
            return null;
        }

        var tableAlias = _queryCompilationContext.SqlAliasManager.GenerateTableAlias(function.Name);
        var columns = new ColumnExpression[function.Columns.Count];
        for (var index = 0; index < function.Columns.Count; index++)
        {
            var column = function.Columns[index];
            var columnClrType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
            var columnMapping = column.StoreType is null
                ? _typeMappingSource.FindMapping(columnClrType, RelationalDependencies.Model)
                : _typeMappingSource.FindMapping(column.StoreType);
            if (columnMapping is null)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL set-returning column '{column.Name}' has no mapping for CLR type "
                    + $"'{column.ClrType.Name}'.");
            }

            columns[index] = new ColumnExpression(
                column.Name,
                tableAlias,
                columnClrType,
                columnMapping,
                column.IsNullable);
        }

        var ordinalityTypeMapping = _typeMappingSource.FindMapping(typeof(long))!;
        var ordinalityColumn = new ColumnExpression(
            "ordinality",
            tableAlias,
            typeof(long),
            ordinalityTypeMapping,
            nullable: false);

#pragma warning disable EF1001 // SelectExpression constructors are provider-facing infrastructure.
        var selectExpression = new SelectExpression(
            [
                new BlueTuskSetReturningFunctionTableExpression(
                    tableAlias,
                    function.Name,
                    arguments,
                    function.Columns.Select(column => column.Name).ToArray(),
                    withOrdinality: true),
            ],
            columns[0],
            identifier: [(ordinalityColumn, (ValueComparer)ordinalityTypeMapping.Comparer)],
            _queryCompilationContext.SqlAliasManager);
#pragma warning restore EF1001
        var constructor = function.ElementType.GetConstructor(
            function.Columns.Select(column => column.ClrType).ToArray())
            ?? throw new InvalidOperationException(
                $"The set-returning row type '{function.ElementType.Name}' does not expose the expected constructor.");
        var rowProperties = constructor.GetParameters()
            .Select(parameter => function.ElementType.GetProperty(
                    parameter.Name!,
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.IgnoreCase)
                ?? throw new InvalidOperationException(
                    $"The set-returning row type '{function.ElementType.Name}' does not expose property "
                    + $"'{parameter.Name}'."))
            .ToArray();
        var projectionMembers = rowProperties
            .Select(property => new ProjectionMember().Append(property))
            .ToArray();
        selectExpression.ReplaceProjection(
            projectionMembers
                .Select((projection, index) => (projection, index))
                .ToDictionary(
                    item => item.projection,
                    item => (Expression)columns[item.index]));
        selectExpression.AppendOrdering(new OrderingExpression(ordinalityColumn, ascending: true));

        var shaper = Expression.New(
            constructor,
            projectionMembers
                .Select((projection, index) =>
                    (Expression)new ProjectionBindingExpression(
                        selectExpression,
                        projection,
                        function.Columns[index].ClrType)),
            rowProperties);
        return new ShapedQueryExpression(selectExpression, shaper);
    }

    private SqlExpression[]? TranslateSetReturningArguments(
        IReadOnlyList<Expression> expressions,
        IReadOnlyList<string?> argumentStoreTypes)
    {
        var arguments = new SqlExpression[expressions.Count];
        for (var index = 0; index < expressions.Count; index++)
        {
            var argumentStoreType = argumentStoreTypes[index];
            if (TranslateExpression(
                    expressions[index],
                    applyDefaultTypeMapping: false) is not { } argument)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL set-returning argument {index + 1} could not be translated.");
            }

            var argumentTypeMapping = argumentStoreType is null
                ? _typeMappingSource.FindMapping(expressions[index].Type, RelationalDependencies.Model)
                : _typeMappingSource.FindMapping(argumentStoreType);
            if (argumentTypeMapping is null)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL set-returning argument {index + 1} has no mapping for CLR type "
                    + $"'{expressions[index].Type.Name}'.");
            }

            arguments[index] = RelationalDependencies.SqlExpressionFactory.ApplyTypeMapping(
                argument,
                argumentTypeMapping);
        }

        return arguments;
    }

    private ShapedQueryExpression? TranslateJsonToRecordset(
        BlueTuskJsonToRecordsetQueryRootExpression function)
    {
        if (TranslateSetReturningArguments([function.Json], ["jsonb"]) is not { } arguments)
        {
            return null;
        }

        var tableName = function.EntityType.GetTableName();
        if (tableName is null
            || function.EntityType.Model.GetRelationalModel().FindTable(
                tableName,
                function.EntityType.GetSchema()) is not { } table)
        {
            return null;
        }

        var tableAlias = _queryCompilationContext.SqlAliasManager.GenerateTableAlias(
            "jsonb_to_recordset");
        var propertyExpressions = new Dictionary<IProperty, ColumnExpression>();
        var columnNames = new List<string>();
        var columnStoreTypes = new List<string?>();
        foreach (var property in function.EntityType.GetPropertiesInHierarchy())
        {
            var column = table.FindColumn(property);
            if (column is null)
            {
                return null;
            }

            var typeMapping = column.StoreTypeMapping;
            propertyExpressions[property] = new ColumnExpression(
                column.Name,
                tableAlias,
                Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType,
                typeMapping,
                property.IsNullable);
            columnNames.Add(column.Name);
            columnStoreTypes.Add(typeMapping.StoreType);
        }

        if (propertyExpressions.Count == 0)
        {
            return null;
        }

        var tableExpression = new BlueTuskSetReturningFunctionTableExpression(
            tableAlias,
            "jsonb_to_recordset",
            arguments,
            columnNames,
            columnStoreTypes,
            withOrdinality: false);
        var tableMap = new Dictionary<ITableBase, string> { [table] = tableAlias };
#pragma warning disable EF1001 // EF relational projections are provider-facing infrastructure.
        var projection = new StructuralTypeProjectionExpression(
            function.EntityType,
            propertyExpressions,
            tableMap);

        var selectExpression = new SelectExpression(
            [tableExpression],
            projection,
            identifier: [],
            _queryCompilationContext.SqlAliasManager);
#pragma warning restore EF1001
        return new ShapedQueryExpression(
            selectExpression,
            new RelationalStructuralTypeShaperExpression(
                function.EntityType,
                new ProjectionBindingExpression(
                    selectExpression,
                    new ProjectionMember(),
                    typeof(ValueBuffer)),
                nullable: false));
#pragma warning restore EF1001
    }

    private static Type? GetElementType(Type sequenceType)
    {
        if (sequenceType.IsArray)
        {
            return sequenceType.GetElementType();
        }

        return sequenceType
            .GetInterfaces()
            .Prepend(sequenceType)
            .FirstOrDefault(type =>
                type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }
}
