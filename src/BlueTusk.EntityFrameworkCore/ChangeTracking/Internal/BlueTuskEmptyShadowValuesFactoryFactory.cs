// Derived from Entity Framework Core 10.0.11 (src/EFCore/ChangeTracking/Internal/EmptyShadowValuesFactoryFactory.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
// BlueTusk changes: renamed provider copy of the EF Core snapshot factory.

#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;

internal class BlueTuskEmptyShadowValuesFactoryFactory : BlueTuskSnapshotFactoryFactory
{
    private BlueTuskEmptyShadowValuesFactoryFactory()
    {
    }
    public static readonly BlueTuskEmptyShadowValuesFactoryFactory Instance = new();
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
    protected override Expression CreateReadShadowValueExpression(Expression? parameter, IPropertyBase property)
        => Expression.Default(property.ClrType);
    protected override Expression CreateReadValueExpression(Expression? parameter, IPropertyBase property)
        => Expression.Default(property.ClrType);
}
