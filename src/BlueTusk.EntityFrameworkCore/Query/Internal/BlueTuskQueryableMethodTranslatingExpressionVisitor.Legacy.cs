#if !BLUETUSK_EF9_OR_GREATER
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed partial class BlueTuskQueryableMethodTranslatingExpressionVisitor
{
    private readonly LegacyAliasState _legacyAliases = new();

    private sealed class LegacyAliasState
    {
        internal int Count;
    }

    private string GenerateLegacyAlias(string name) =>
        name[0].ToString().ToLowerInvariant() + (++_legacyAliases.Count).ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<IProperty> GetLegacyProperties(IEntityType entityType) =>
        entityType.GetProperties().Concat(entityType.GetDerivedTypes().SelectMany(derived => derived.GetDeclaredProperties()));

#if BLUETUSK_EF8_OR_GREATER
    private ShapedQueryExpression? TranslateLegacyJsonCollection(MethodCallExpression call)
    {
        if (call.Method.DeclaringType != typeof(EF) || call.Method.Name != nameof(EF.Property)
            || call.Arguments[0] is not StructuralTypeShaperExpression
            {
                ValueBufferExpression: JsonQueryExpression json,
            }
            || call.Arguments[1] is not ConstantExpression { Value: string name }
            || json.EntityType.FindProperty(name) is not { IsPrimitiveCollection: true } property)
        {
            return null;
        }

        var collection = json.BindProperty(property);
        return TranslatePrimitiveCollection(collection, property, GenerateLegacyAlias(name));
    }

    protected override ShapedQueryExpression TransformJsonQueryToTable(JsonQueryExpression jsonQueryExpression)
    {
        var mapping = jsonQueryExpression.JsonColumn.TypeMapping
            ?? throw new InvalidOperationException("A PostgreSQL JSON column requires a relational type mapping.");
        var functionName = mapping.StoreType switch
        {
            "jsonb" => "jsonb_to_recordset",
            "json" => "json_to_recordset",
            _ => throw new InvalidOperationException($"The store type '{mapping.StoreType}' is not a JSON recordset."),
        };
        var names = new List<string>();
        var storeTypes = new List<string?>();
        foreach (var property in GetLegacyProperties(jsonQueryExpression.EntityType))
        {
            if (property.GetJsonPropertyName() is { } name)
            {
                names.Add(name);
                storeTypes.Add(property.GetRelationalTypeMapping().StoreType);
            }
        }

        var navigations = jsonQueryExpression.EntityType.GetNavigations().Concat(
            jsonQueryExpression.EntityType.GetDerivedTypes().SelectMany(derived => derived.GetDeclaredNavigations()));
        foreach (var navigation in navigations.Where(navigation => navigation.ForeignKey.IsOwnership
            && navigation.TargetEntityType.IsMappedToJson()
            && navigation.ForeignKey.PrincipalToDependent == navigation))
        {
            if (navigation.TargetEntityType.GetJsonPropertyName() is { } name)
            {
                names.Add(name);
                storeTypes.Add(mapping.StoreType);
            }
        }

        var json = new JsonScalarExpression(jsonQueryExpression.JsonColumn, jsonQueryExpression.Path,
            typeof(string), mapping, jsonQueryExpression.IsNullable);
        var table = new BlueTuskSetReturningFunctionTableExpression(GenerateLegacyAlias(functionName),
            functionName, [json], names, storeTypes, withOrdinality: true);
#pragma warning disable EF1001 // EF8 exposes its provider JSON select constructor as infrastructure.
        var select = new SelectExpression(jsonQueryExpression, table, "ordinality", typeof(int),
            _typeMappingSource.FindMapping(typeof(int))!);
#pragma warning restore EF1001
        return new ShapedQueryExpression(select, new RelationalStructuralTypeShaperExpression(
            jsonQueryExpression.EntityType,
            new ProjectionBindingExpression(select, new ProjectionMember(), typeof(ValueBuffer)), nullable: false));
    }

    protected override ShapedQueryExpression? TranslatePrimitiveCollection(
        SqlExpression sqlExpression, IProperty? property, string tableAlias)
    {
        var elementClrType = property?.GetElementType()?.ClrType ?? GetElementType(sqlExpression.Type);
        if (elementClrType is null)
        {
            return null;
        }

        var elementType = Nullable.GetUnderlyingType(elementClrType) ?? elementClrType;
        var elementMapping = (RelationalTypeMapping?)sqlExpression.TypeMapping?.ElementTypeMapping
            ?? _typeMappingSource.FindMapping(elementType);
        if (elementMapping is null)
        {
            return null;
        }

        var nullable = property?.GetElementType()?.IsNullable
            ?? (!elementClrType.IsValueType || Nullable.GetUnderlyingType(elementClrType) is not null);
        var ordinalMapping = _typeMappingSource.FindMapping(typeof(int))!;
        TableExpressionBase table;
        SelectExpression select;
        if (sqlExpression is JsonScalarExpression jsonScalar
            && jsonScalar.Json.TypeMapping is { StoreType: "json" or "jsonb" } jsonMapping)
        {
            var json = new JsonScalarExpression(jsonScalar.Json, jsonScalar.Path, typeof(string), jsonMapping,
                jsonScalar.IsNullable);
            table = new BlueTuskSetReturningFunctionTableExpression(tableAlias,
                jsonMapping.StoreType == "jsonb" ? "jsonb_array_elements_text" : "json_array_elements_text",
                [json], ["value"], withOrdinality: true);
            var stringMapping = _typeMappingSource.FindMapping(typeof(string))!;
            select = CreateLegacyScalarSelect(table, "value", typeof(string), stringMapping, nullable,
                "ordinality", typeof(int), ordinalMapping);
            SqlExpression value = select.CreateColumnExpression(table, "value", typeof(string), stringMapping, nullable);
            if (!elementMapping.StoreType.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                value = RelationalDependencies.SqlExpressionFactory.Convert(value, elementType, elementMapping);
            }

            select.ReplaceProjection(new Dictionary<ProjectionMember, Expression> { [new ProjectionMember()] = value });
        }
        else
        {
            table = new BlueTuskUnnestExpression(tableAlias, sqlExpression);
            select = CreateLegacyScalarSelect(table, "value", elementType, elementMapping, nullable,
                "ordinality", typeof(int), ordinalMapping);
        }

        select.AppendOrdering(new OrderingExpression(
            select.CreateColumnExpression(table, "ordinality", typeof(int), ordinalMapping, false), ascending: true));
        Expression shaper = new ProjectionBindingExpression(select, new ProjectionMember(),
            elementClrType.IsValueType && Nullable.GetUnderlyingType(elementClrType) is null
                ? typeof(Nullable<>).MakeGenericType(elementClrType) : elementClrType);
        if (shaper.Type != elementClrType)
        {
            shaper = Expression.Convert(shaper, elementClrType);
        }

        return new ShapedQueryExpression(select, shaper);
    }

#endif

    private ShapedQueryExpression? TranslateSetReturningFunction(BlueTuskSetReturningFunctionQueryRootExpression function)
    {
        var mapping = function.ResultStoreType is null
            ? FindStoreTypeMapping(function.ElementType)
            : _typeMappingSource.FindMapping(function.ResultStoreType);
        var arguments = TranslateSetReturningArguments(function.Arguments, function.ArgumentStoreTypes);
        if (mapping is null || arguments is null)
        {
            return null;
        }

        var table = new BlueTuskSetReturningFunctionTableExpression(GenerateLegacyAlias(function.Name),
            function.Name, arguments, ["value"], function.WithOrdinality);
        var ordinalMapping = _typeMappingSource.FindMapping(typeof(long))!;
        var select = CreateLegacyScalarSelect(table, "value", function.ElementType, mapping, function.IsNullable,
            function.WithOrdinality ? "ordinality" : "value", function.WithOrdinality ? typeof(long) : function.ElementType,
            function.WithOrdinality ? ordinalMapping : mapping);
        if (function.WithOrdinality)
        {
            select.AppendOrdering(new OrderingExpression(
                select.CreateColumnExpression(table, "ordinality", typeof(long), ordinalMapping, false), ascending: true));
        }

        return new ShapedQueryExpression(select,
            new ProjectionBindingExpression(select, new ProjectionMember(), function.ElementType));
    }

    private ShapedQueryExpression? TranslateRecordSetReturningFunction(BlueTuskRecordSetReturningFunctionQueryRootExpression function)
    {
        var arguments = TranslateSetReturningArguments(function.Arguments, function.ArgumentStoreTypes);
        if (arguments is null)
        {
            return null;
        }

        var table = new BlueTuskSetReturningFunctionTableExpression(GenerateLegacyAlias(function.Name),
            function.Name, arguments, function.Columns.Select(column => column.Name).ToArray(), withOrdinality: true);
        var mappings = function.Columns.Select(column => column.StoreType is null
            ? FindStoreTypeMapping(Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType)
            : _typeMappingSource.FindMapping(column.StoreType)).ToArray();
        if (mappings.Any(mapping => mapping is null))
        {
            throw new InvalidOperationException("A PostgreSQL set-returning column has no relational type mapping.");
        }

        var first = function.Columns[0];
        var ordinalMapping = _typeMappingSource.FindMapping(typeof(long))!;
        var select = CreateLegacyScalarSelect(table, first.Name,
            Nullable.GetUnderlyingType(first.ClrType) ?? first.ClrType, mappings[0]!, first.IsNullable,
            "ordinality", typeof(long), ordinalMapping);
        var constructor = function.ElementType.GetConstructor(function.Columns.Select(column => column.ClrType).ToArray())
            ?? throw new InvalidOperationException($"The set-returning row '{function.ElementType.Name}' has no matching constructor.");
        var properties = constructor.GetParameters().Select(parameter => function.ElementType.GetProperty(
            parameter.Name!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
            ?? throw new InvalidOperationException($"The set-returning row has no property '{parameter.Name}'.")).ToArray();
        var members = properties.Select(propertyInfo => new ProjectionMember().Append(propertyInfo)).ToArray();
        var projections = new Dictionary<ProjectionMember, Expression>();
        for (var index = 0; index < members.Length; index++)
        {
            var column = function.Columns[index];
            projections[members[index]] = select.CreateColumnExpression(table, column.Name,
                Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType, mappings[index]!, column.IsNullable);
        }

#if BLUETUSK_EF6_OR_GREATER
        select.ReplaceProjection(projections);
#else
        select.ReplaceProjectionMapping(projections);
#endif
        select.AppendOrdering(new OrderingExpression(
            select.CreateColumnExpression(table, "ordinality", typeof(long), ordinalMapping, false), ascending: true));
        return new ShapedQueryExpression(select, Expression.New(constructor, members.Select((member, index) =>
            (Expression)new ProjectionBindingExpression(select, member, function.Columns[index].ClrType)), properties));
    }

    private ShapedQueryExpression? TranslateJsonToRecordset(BlueTuskJsonToRecordsetQueryRootExpression function)
    {
        if (TranslateSetReturningArguments([function.Json], ["jsonb"]) is not { } arguments)
        {
            return null;
        }

        var shaped = CreateShapedQueryExpression(function.EntityType);
        if (shaped.QueryExpression is not SelectExpression { Tables: [TableExpression table] } select)
        {
            throw new InvalidOperationException("A PostgreSQL JSON recordset requires one mapped table.");
        }

        var storeObject = StoreObjectIdentifier.Table(table.Name, table.Schema);
        var properties = GetLegacyProperties(function.EntityType).ToArray();
        var names = properties.Select(property => property.GetColumnName(storeObject)
            ?? throw new InvalidOperationException($"JSON recordset property '{property.Name}' has no column.")).ToArray();
        var storeTypes = properties.Select(property => (string?)property.GetRelationalTypeMapping().StoreType).ToArray();
        var functionTable = new BlueTuskSetReturningFunctionTableExpression(table.Alias,
            "jsonb_to_recordset", arguments, names, storeTypes, withOrdinality: false);
#if BLUETUSK_EF6_OR_GREATER
        ((List<TableExpressionBase>)select.Tables)[0] = functionTable;
        return shaped;
#else
        var functionSelect = RelationalDependencies.SqlExpressionFactory.Select(function.EntityType, functionTable);
        return new ShapedQueryExpression(functionSelect, new RelationalEntityShaperExpression(
            function.EntityType,
            new ProjectionBindingExpression(functionSelect, new ProjectionMember(), typeof(ValueBuffer)),
            nullable: false));
#endif
    }

#if BLUETUSK_EF8_OR_GREATER
    private static SelectExpression CreateLegacyScalarSelect(TableExpressionBase table, string name, Type type,
#else
    private SelectExpression CreateLegacyScalarSelect(TableExpressionBase table, string name, Type type,
#endif
        RelationalTypeMapping mapping, bool nullable, string identifierName, Type identifierType, RelationalTypeMapping identifierMapping)
    {
#pragma warning disable EF1001 // EF8's scalar query constructor is the provider extension point.
#if BLUETUSK_EF8_OR_GREATER
        return new SelectExpression(table, name, type, mapping, nullable, identifierName, identifierType, identifierMapping);
#else
        return BlueTuskLegacySelectFactory.Create(_sqlExpressionFactory, table, name, type, mapping, nullable,
            identifierName, identifierType, identifierMapping);
#endif
#pragma warning restore EF1001
    }

#if !BLUETUSK_EF7_OR_GREATER
    private SqlExpression? TranslateExpression(Expression expression) => _legacySqlTranslator.Translate(expression);

    private SqlExpression? TranslateLambdaExpression(ShapedQueryExpression source, LambdaExpression lambda) =>
        TranslateExpression(ReplacingExpressionVisitor.Replace(lambda.Parameters[0], source.ShaperExpression, lambda.Body));
#endif
}
#endif
