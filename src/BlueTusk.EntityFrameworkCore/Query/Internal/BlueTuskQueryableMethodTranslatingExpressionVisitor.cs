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

        return base.VisitExtension(extensionExpression);
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
                return null;
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
        var pairProperties = new[]
        {
            function.ElementType.GetProperty(nameof(KeyValuePair<string, string>.Key))!,
            function.ElementType.GetProperty(nameof(KeyValuePair<string, string>.Value))!,
        };
        var projectionMembers = pairProperties
            .Select(property => new ProjectionMember().Append(property))
            .ToArray();
        selectExpression.ReplaceProjection(
            projectionMembers
                .Select((projection, index) => (projection, index))
                .ToDictionary(
                    item => item.projection,
                    item => (Expression)columns[item.index]));
        selectExpression.AppendOrdering(new OrderingExpression(ordinalityColumn, ascending: true));

        var constructor = function.ElementType.GetConstructor(
            function.ElementType.GetGenericArguments())!;
        var shaper = Expression.New(
            constructor,
            projectionMembers
                .Select((projection, index) =>
                    (Expression)new ProjectionBindingExpression(
                        selectExpression,
                        projection,
                        function.Columns[index].ClrType)),
            pairProperties);
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
