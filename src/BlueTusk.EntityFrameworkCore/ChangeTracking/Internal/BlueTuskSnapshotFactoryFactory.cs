// Derived from Entity Framework Core 10.0.11 (src/EFCore/ChangeTracking/Internal/SnapshotFactoryFactory.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
// BlueTusk changes: complex-type shadow properties are snapshotted (EF Core substitutes defaults, dotnet/efcore#35613/#37337), and fixed-size (array) complex collections are snapshotted by index.

#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;

internal abstract class BlueTuskSnapshotFactoryFactory
{
    public virtual Func<ISnapshot> CreateEmpty(IRuntimeTypeBase structuralType)
        => CreateEmptyExpression(structuralType).Compile();
    public virtual Expression<Func<ISnapshot>> CreateEmptyExpression(IRuntimeTypeBase structuralType)
        => Expression.Lambda<Func<ISnapshot>>(CreateConstructorExpression(structuralType, null));
    public virtual Expression CreateConstructorExpression(
        IRuntimeTypeBase structuralType,
        Expression? parameter)
    {
        var count = GetPropertyCount(structuralType);
        if (count == 0)
        {
            return Expression.MakeMemberAccess(null, Snapshot.EmptyField);
        }

        var types = new Type[count];
        var propertyBases = new IPropertyBase?[count];

        var actualCount = 0;
        foreach (var propertyBase in structuralType.GetSnapshottableMembers())
        {
            var index = GetPropertyIndex(propertyBase);
            if (index >= 0)
            {

                types[index] = (propertyBase as IProperty)?.ClrType ?? typeof(object);
                propertyBases[index] = propertyBase;
                actualCount++;
            }
        }

        Expression constructorExpression;
        if (count > Snapshot.MaxGenericTypes)
        {
            var snapshotExpressions = new List<Expression>();

            for (var i = 0; i < count; i += Snapshot.MaxGenericTypes)
            {
                snapshotExpressions.Add(
                    CreateSnapshotExpression(
                        structuralType.ClrType,
                        parameter,
                        [.. types.Skip(i).Take(Snapshot.MaxGenericTypes)],
                        [.. propertyBases.Skip(i).Take(Snapshot.MaxGenericTypes)]));
            }

            constructorExpression =
                Expression.Convert(
                    Expression.New(
                        MultiSnapshotConstructor,
                        Expression.NewArrayInit(typeof(ISnapshot), snapshotExpressions)),
                    typeof(ISnapshot));
        }
        else
        {
            constructorExpression = CreateSnapshotExpression(structuralType.ClrType, parameter, types, propertyBases);
        }

        return constructorExpression;
    }
    protected virtual Expression CreateSnapshotExpression(
        Type? clrType,
        Expression? parameter,
        Type[] types,
        IList<IPropertyBase?> propertyBases)
    {
        var count = types.Length;
        var arguments = new Expression[count];

        var structuralTypeVariable = clrType == null
            ? null
            : Expression.Variable(clrType, "structuralType");
        var indicesExpression = parameter == null || !parameter.Type.IsAssignableTo(typeof(IInternalEntry))
            ? (Expression)Expression.Property(null, typeof(ReadOnlySpan<int>), nameof(ReadOnlySpan<>.Empty))
            : Expression.Call(parameter, PropertyAccessorsFactory.GetOrdinalsMethod);

        for (var i = 0; i < count; i++)
        {
            var propertyBase = propertyBases[i];

            switch (propertyBase)
            {
                case null:
                    arguments[i] = Expression.Constant(null);
                    types[i] = typeof(object);
                    continue;

                case IProperty property:

                    arguments[i] = CreateSnapshotValueExpression(CreateReadValueExpression(parameter, property), property);
                    continue;

                case var _ when propertyBase.IsShadowProperty():
                    arguments[i] = CreateSnapshotValueExpression(CreateReadShadowValueExpression(parameter, propertyBase), propertyBase);
                    continue;

                case IComplexProperty { IsCollection: false, IsNullable: true }:
                    // For nullable non-collection complex properties, convert to object to store the null reference
                    arguments[i] = Expression.Convert(
                        CreateSnapshotValueExpression(CreateReadValueExpression(parameter, propertyBase), propertyBase),
                        typeof(object));
                    continue;
            }

            arguments[i] = CreateSnapshotValueExpression(CreateReadValueExpression(parameter, propertyBase), propertyBase);
        }

        var constructorExpression = Expression.Convert(
            Expression.New(
                Snapshot.CreateSnapshotType(types).GetConstructor(types)!,
                arguments),
            typeof(ISnapshot));

        return UseEntityVariable
            && structuralTypeVariable != null
                ? Expression.Block(
                    new List<ParameterExpression> { structuralTypeVariable },
                    new List<Expression>
                    {
                        Expression.Assign(
                            structuralTypeVariable,
                            (IRuntimeTypeBase)propertyBases[0]!.DeclaringType switch
                            {
                                IComplexType { ComplexProperty.IsCollection: true } declaringComplexType => PropertyAccessorsFactory.CreateComplexCollectionElementAccess(
                                        declaringComplexType.ComplexProperty,
                                        Expression.Convert(
                                            Expression.Property(parameter!, nameof(IInternalEntry.Entity)),
                                            declaringComplexType.ComplexProperty.DeclaringType.ContainingEntityType.ClrType),
                                        indicesExpression,
                                        fromDeclaringType: false,
                                        fromEntity: true),
                                { ContainingEntryType: IComplexType collectionComplexType }
                                    => PropertyAccessorsFactory.CreateComplexCollectionElementAccess(
                                        collectionComplexType.ComplexProperty,
                                        Expression.Convert(
                                            Expression.Property(parameter!, nameof(IInternalEntry.Entity)),
                                            collectionComplexType.ComplexProperty.DeclaringType.ContainingEntityType.ClrType),
                                        indicesExpression,
                                        fromDeclaringType: false,
                                        fromEntity: true),
                                _
                                    => Expression.Convert(
                                        Expression.Property(parameter!, nameof(IInternalEntry.Entity)),
                                        structuralTypeVariable.Type)
                            }),
                        constructorExpression
                    })
                : constructorExpression;
    }

    private Expression CreateSnapshotValueExpression(Expression expression, IPropertyBase propertyBase)
    {
        if (propertyBase is not IProperty property)
        {
            if (propertyBase.IsCollection)
            {
                expression = propertyBase is IComplexProperty complexProperty
                    ? Expression.Call(
                        null,
                        SnapshotComplexCollectionMethod,
                        expression.Type.IsAssignableTo(typeof(IList))
                            ? expression
                            : Expression.Convert(expression, typeof(IList)),
                        Expression.Constant(complexProperty))
                    : Expression.Call(
                        null,
                        SnapshotCollectionMethod,
                        expression.Type.IsAssignableTo(typeof(IEnumerable))
                            ? expression
                            : Expression.Convert(expression, typeof(IEnumerable)));
            }

            return expression;
        }

        if (GetValueComparer(property) is not { } comparer)
        {
            return expression;
        }

        if (expression.Type != comparer.Type)
        {
            expression = Expression.Convert(expression, comparer.Type);
        }

        var comparerExpression = Expression.Convert(
            Expression.Call(
                Expression.Constant(property),
                GetValueComparerMethod()!),
            typeof(ValueComparer<>).MakeGenericType(comparer.Type));

        Expression snapshotExpression = Expression.Call(
            comparerExpression,
            ValueComparer.GetGenericSnapshotMethod(comparer.Type),
            expression);

        if (snapshotExpression.Type != propertyBase.ClrType)
        {
            snapshotExpression = Expression.Convert(snapshotExpression, propertyBase.ClrType);
        }

        expression = IsNullableType(propertyBase.ClrType)
            ? Expression.Condition(
                Expression.Equal(expression, Expression.Constant(null, propertyBase.ClrType)),
                Expression.Constant(null, propertyBase.ClrType),
                snapshotExpression)
            : snapshotExpression;

        return expression;
    }
    protected abstract ValueComparer? GetValueComparer(IProperty property);
    protected abstract MethodInfo? GetValueComparerMethod();
    protected virtual Expression CreateReadShadowValueExpression(
        Expression? parameter,
        IPropertyBase property)
        => Expression.Call(
            parameter,
            MakeReadShadowValueMethod((property as IProperty)?.ClrType ?? typeof(object)),
            Expression.Constant(property.GetShadowIndex()));
    protected virtual Expression CreateReadValueExpression(
        Expression? parameter,
        IPropertyBase property)
        => Expression.Call(
            parameter,
            MakeGetCurrentValueMethod(property.ClrType),
            Expression.Constant(property, typeof(IPropertyBase)));
    protected abstract int GetPropertyIndex(IPropertyBase propertyBase);
    protected abstract int GetPropertyCount(IRuntimeTypeBase structuralType);
    protected virtual bool UseEntityVariable
        => true;

    private static readonly MethodInfo SnapshotCollectionMethod
        = typeof(BlueTuskSnapshotFactoryFactory).GetTypeInfo().GetDeclaredMethod(nameof(SnapshotCollection))!;
    public static HashSet<object>? SnapshotCollection(IEnumerable? collection)
    {
        if (collection is null)
        {
            return null;
        }

        var snapshot = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var item in collection)
        {
            snapshot.Add(item);
        }

        return snapshot;
    }

    private static readonly MethodInfo SnapshotComplexCollectionMethod
        = typeof(BlueTuskSnapshotFactoryFactory).GetTypeInfo().GetDeclaredMethod(nameof(SnapshotComplexCollection))!;
    public static IList? SnapshotComplexCollection(IList? list, IRuntimeComplexProperty complexProperty)
    {
        if (list == null)
        {
            return null;
        }

        var snapshot = (IList)complexProperty.GetIndexedCollectionAccessor().Create(list.Count);
        if (snapshot.IsFixedSize)
        {
            for (var i = 0; i < list.Count; i++)
            {
                snapshot[i] = list[i];
            }

            return snapshot;
        }

        foreach (var item in list)
        {
            // We need to preserve the original reference, these are only used to find moved items, not modified properties on them
            snapshot.Add(item);
        }

        return snapshot;
    }

    private static readonly ConstructorInfo MultiSnapshotConstructor
        = typeof(MultiSnapshot).GetConstructor([typeof(ISnapshot[])])!;

    private static readonly MethodInfo ReadShadowValueMethod
        = typeof(IInternalEntry).GetMethod(nameof(IInternalEntry.ReadShadowValue))!;

    private static readonly MethodInfo GetCurrentValueMethod
        = typeof(IInternalEntry).GetMethods().Single(m => m.IsGenericMethod && m.Name == nameof(IInternalEntry.GetCurrentValue));

    private static MethodInfo MakeReadShadowValueMethod(Type type)
        => ReadShadowValueMethod.MakeGenericMethod(type);

    private static MethodInfo MakeGetCurrentValueMethod(Type type)
        => GetCurrentValueMethod.MakeGenericMethod(type);

    private static bool IsNullableType(Type type)
        => !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
}
