// Derived from Entity Framework Core 10.0.11 (src/EFCore/ChangeTracking/Internal/RelationshipSnapshotFactoryFactory.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
// BlueTusk changes: renamed provider copy of the EF Core snapshot factory.

#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;

internal class BlueTuskRelationshipSnapshotFactoryFactory : BlueTuskSnapshotFactoryFactory<IInternalEntry>
{
    private static readonly MethodInfo _getValueComparerMethod
        = typeof(IProperty).GetMethod(nameof(IProperty.GetKeyValueComparer))!;

    private BlueTuskRelationshipSnapshotFactoryFactory()
    {
    }
    public static readonly BlueTuskRelationshipSnapshotFactoryFactory Instance = new();
    protected override int GetPropertyIndex(IPropertyBase propertyBase)
        => propertyBase.GetRelationshipIndex();
    protected override int GetPropertyCount(IRuntimeTypeBase structuralType)
        => ((IRuntimeEntityType)structuralType).RelationshipPropertyCount;
    protected override ValueComparer? GetValueComparer(IProperty property)
        => property.GetKeyValueComparer();
    protected override MethodInfo? GetValueComparerMethod()
        => _getValueComparerMethod;
}
