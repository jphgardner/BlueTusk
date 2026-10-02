// Derived from Entity Framework Core 10.0.11 (src/EFCore/ChangeTracking/Internal/TemporaryValuesFactoryFactory.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
// BlueTusk changes: renamed provider copy of the EF Core snapshot factory.

#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;

internal class BlueTuskTemporaryValuesFactoryFactory : BlueTuskSidecarValuesFactoryFactory
{
    private BlueTuskTemporaryValuesFactoryFactory()
    {
    }
    public static new readonly BlueTuskTemporaryValuesFactoryFactory Instance = new();
    protected override Expression CreateSnapshotExpression(
        Type? entityType,
        Expression? parameter,
        Type[] types,
        IList<IPropertyBase?> propertyBases)
    {
        var constructorExpression = Expression.Convert(
            Expression.New(
                Snapshot.CreateSnapshotType(types).GetConstructor(types)!,
                types.Select(Expression.Default).ToArray()),
            typeof(ISnapshot));

        return constructorExpression;
    }
}
