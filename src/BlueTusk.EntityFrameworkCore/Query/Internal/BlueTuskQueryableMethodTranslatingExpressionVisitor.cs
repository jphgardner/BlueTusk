using System.Linq.Expressions;
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
        if (extensionExpression is BlueTuskGenerateSeriesQueryRootExpression series)
        {
            return TranslateGenerateSeries(series) ?? base.VisitExtension(extensionExpression);
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

    private ShapedQueryExpression? TranslateGenerateSeries(
        BlueTuskGenerateSeriesQueryRootExpression series)
    {
        var elementTypeMapping = _typeMappingSource.FindMapping(
            series.ElementType,
            RelationalDependencies.Model);
        if (elementTypeMapping is null)
        {
            return null;
        }

        var arguments = new SqlExpression[series.Arguments.Count];
        for (var index = 0; index < series.Arguments.Count; index++)
        {
            if (TranslateExpression(series.Arguments[index]) is not { } argument)
            {
                return null;
            }

            arguments[index] = RelationalDependencies.SqlExpressionFactory.ApplyTypeMapping(
                argument,
                elementTypeMapping);
        }

        var tableAlias = _queryCompilationContext.SqlAliasManager.GenerateTableAlias("generate_series");

        var valueColumn = new ColumnExpression(
            "value",
            tableAlias,
            series.ElementType,
            elementTypeMapping,
            nullable: false);

#pragma warning disable EF1001 // SelectExpression constructors are provider-facing infrastructure.
        var selectExpression = new SelectExpression(
            [new BlueTuskGenerateSeriesTableExpression(tableAlias, arguments)],
            valueColumn,
            identifier: [(valueColumn, (ValueComparer)elementTypeMapping.Comparer)],
            _queryCompilationContext.SqlAliasManager);
#pragma warning restore EF1001

        return new ShapedQueryExpression(
            selectExpression,
            new ProjectionBindingExpression(
                selectExpression,
                new ProjectionMember(),
                series.ElementType));
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
