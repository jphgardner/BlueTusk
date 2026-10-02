// Derived from Entity Framework Core 10.0.11 (src/EFCore/ChangeTracking/Internal/SnapshotFactoryFactory`.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
// BlueTusk changes: renamed provider copy of the EF Core snapshot factory.

#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;

internal abstract class BlueTuskSnapshotFactoryFactory<TInput> : BlueTuskSnapshotFactoryFactory
{
    public virtual Func<TInput, ISnapshot> Create(IRuntimeTypeBase structuralType)
        => CreateExpression(structuralType).Compile();
    public virtual Expression<Func<TInput, ISnapshot>> CreateExpression(IRuntimeTypeBase structuralType)
    {
        var parameter = Expression.Parameter(typeof(TInput), "source");

        return Expression.Lambda<Func<TInput, ISnapshot>>(
            CreateConstructorExpression(structuralType, parameter),
            parameter);
    }
}
