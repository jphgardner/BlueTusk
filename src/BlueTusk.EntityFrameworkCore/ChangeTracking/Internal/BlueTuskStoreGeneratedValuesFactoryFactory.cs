// Derived from Entity Framework Core 10.0.11 (src/EFCore/ChangeTracking/Internal/StoreGeneratedValuesFactoryFactory.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
// BlueTusk changes: renamed provider copy of the EF Core snapshot factory.

#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;

internal class BlueTuskStoreGeneratedValuesFactoryFactory : BlueTuskSidecarValuesFactoryFactory
{
    private BlueTuskStoreGeneratedValuesFactoryFactory()
    {
    }
    public static new readonly BlueTuskStoreGeneratedValuesFactoryFactory Instance = new();
    protected override bool UseEntityVariable
        => false;
    protected override Expression CreateReadShadowValueExpression(Expression? parameter, IPropertyBase property)
        => Expression.Default(property.ClrType);
    protected override Expression CreateReadValueExpression(Expression? parameter, IPropertyBase property)
        => Expression.Default(property.ClrType);
}
