// Derived from Entity Framework Core 10.0.11 (src/EFCore/ChangeTracking/Internal/ShadowValuesFactoryFactory.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
// BlueTusk changes: renamed provider copy of the EF Core snapshot factory.

#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;

internal class BlueTuskShadowValuesFactoryFactory : BlueTuskSnapshotFactoryFactory<IDictionary<string, object?>>
{
    private BlueTuskShadowValuesFactoryFactory()
    {
    }
    public static readonly BlueTuskShadowValuesFactoryFactory Instance = new();
    protected override int GetPropertyIndex(IPropertyBase propertyBase)
        => propertyBase.GetShadowIndex();
    protected override int GetPropertyCount(IRuntimeTypeBase structuralType)
        => structuralType.ShadowPropertyCount;
    protected override ValueComparer? GetValueComparer(IProperty property)
        => null;
    protected override MethodInfo? GetValueComparerMethod()
        => null;
    protected override bool UseEntityVariable
        => false;

    private static readonly PropertyInfo DictionaryIndexer
        = typeof(IDictionary<string, object?>).GetRuntimeProperties().Single(p => p.GetIndexParameters().Length > 0);
    protected override Expression CreateReadShadowValueExpression(
        Expression? parameter,
        IPropertyBase property)
    {
        if (parameter == null)
        {
            return Expression.Default(property.ClrType);
        }

        if (parameter is NewArrayExpression newArrayExpression)
        {
            var valueExpression = newArrayExpression.Expressions[property.GetShadowIndex()];
            valueExpression = ((UnaryExpression)valueExpression).Operand; // Unwrap cast
            return valueExpression.Type == property.ClrType
                ? valueExpression
                : Expression.Convert(
                    valueExpression,
                    property.ClrType);
        }

        return Expression.Condition(
            Expression.Call(parameter, PropertyAccessorsFactory.ContainsKeyMethod, Expression.Constant(property.Name)),
            Expression.Convert(
                Expression.MakeIndex(
                    parameter,
                    DictionaryIndexer,
                    [Expression.Constant(property.Name)]),
                property.ClrType),
            Expression.Constant(property.Sentinel, property.ClrType));
    }
    protected override Expression CreateReadValueExpression(
        Expression? parameter,
        IPropertyBase property)
        => CreateReadShadowValueExpression(parameter, property);
}
