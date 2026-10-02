#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Metadata.Internal;

// EF Core's setter factory cannot write a member of a value-type complex collection element (dotnet/efcore#31411): it
// applies reference equality to the struct and treats the element access as a member access. For every property declared
// on a complex type that is reached through a value-type collection element, BlueTusk installs setters that copy each
// value-type level out, assign the member, and write the copies back up to the containing entity.
internal static class BlueTuskValueTypeCollectionAccessors
{
    private static readonly MethodInfo SetSetterMethod = typeof(RuntimePropertyBase).GetMethods()
        .Single(m => m.Name == nameof(RuntimePropertyBase.SetSetter) && m.GetGenericArguments().Length == 3);

    private static readonly MethodInfo SetMaterializationSetterMethod = typeof(RuntimePropertyBase).GetMethods()
        .Single(m => m.Name == nameof(RuntimePropertyBase.SetMaterializationSetter) && m.GetGenericArguments().Length == 3);

    private static readonly MethodInfo InstallTypedMethod = typeof(BlueTuskValueTypeCollectionAccessors)
        .GetMethod(nameof(InstallTyped), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static bool HasValueTypeCollection(ITypeBase structuralType)
        => structuralType.GetComplexProperties().Any(p =>
            (p.IsCollection && p.ComplexType.ClrType.IsValueType) || HasValueTypeCollection(p.ComplexType));

    internal static void Install(IEntityType entityType)
        => Install(entityType, [], insideValueTypeElement: false);

    private static void Install(ITypeBase structuralType, IReadOnlyList<IComplexProperty> chain, bool insideValueTypeElement)
    {
        foreach (var complexProperty in structuralType.GetComplexProperties())
        {
            if (insideValueTypeElement)
            {
                InstallSetters(complexProperty, chain);
            }

            var childChain = chain.Append(complexProperty).ToArray();
            Install(
                complexProperty.ComplexType,
                childChain,
                insideValueTypeElement || (complexProperty.IsCollection && complexProperty.ComplexType.ClrType.IsValueType));
        }

        if (!insideValueTypeElement)
        {
            return;
        }

        foreach (var property in structuralType.GetProperties())
        {
            if (!property.IsShadowProperty())
            {
                InstallSetters(property, chain);
            }
        }
    }

    private static void InstallSetters(IPropertyBase property, IReadOnlyList<IComplexProperty> chain)
        => InstallTypedMethod
            .MakeGenericMethod(property.DeclaringType.ContainingEntityType.ClrType, property.DeclaringType.ClrType, property.ClrType)
            .Invoke(null, [property, chain]);

    private static void InstallTyped<TEntity, TStructural, TValue>(IPropertyBase property, IReadOnlyList<IComplexProperty> chain)
        where TEntity : class
    {
        var leaf = property.GetMemberInfo(forMaterialization: false, forSet: true);
        var materializationLeaf = property.GetMemberInfo(forMaterialization: true, forSet: true);
        var levels = chain.Select(p => new Level(
                p,
                p.GetMemberInfo(forMaterialization: false, forSet: false),
                p.GetMemberInfo(forMaterialization: false, forSet: true)))
            .ToArray();

        var runtimeProperty = (RuntimePropertyBase)property;
        SetSetterMethod.MakeGenericMethod(typeof(TEntity), typeof(TStructural), typeof(TValue)).Invoke(
            runtimeProperty,
            [
                (Action<TEntity, IReadOnlyList<int>, TValue>)((entity, indices, value) => SetThroughChain(entity, levels, indices, leaf, value)),
                (Func<TStructural, TValue, TStructural>)((instance, value) => (TStructural)SetMember(instance!, leaf, value)),
            ]);
        SetMaterializationSetterMethod.MakeGenericMethod(typeof(TEntity), typeof(TStructural), typeof(TValue)).Invoke(
            runtimeProperty,
            [
                (Action<TEntity, IReadOnlyList<int>, TValue>)((entity, indices, value) =>
                    SetThroughChain(entity, levels, indices, materializationLeaf, value)),
                (Func<TStructural, TValue, TStructural>)((instance, value) => (TStructural)SetMember(instance!, materializationLeaf, value)),
            ]);
    }

    private static void SetThroughChain(object entity, Level[] levels, IReadOnlyList<int> indices, MemberInfo leaf, object? value)
    {
        var indexPosition = 0;
        _ = SetAt(entity, 0);

        object SetAt(object container, int depth)
        {
            if (depth == levels.Length)
            {
                return SetMember(container, leaf, value);
            }

            var level = levels[depth];
            var child = GetMember(container, level.Getter)
                ?? throw new InvalidOperationException(
                    $"The complex property '{level.Property.Name}' is null, so a value inside it cannot be set.");
            if (level.Property.IsCollection)
            {
                var list = (IList)child;
                var ordinal = indices[indexPosition++];
                list[ordinal] = SetAt(list[ordinal]!, depth + 1);
                return container;
            }

            var updatedChild = SetAt(child, depth + 1);
            return child.GetType().IsValueType ? SetMember(container, level.Setter, updatedChild) : container;
        }
    }

    // Instances are boxed, so value-type members are assigned on the box and the box is returned.
    private static object SetMember(object instance, MemberInfo member, object? value)
    {
        switch (member)
        {
            case FieldInfo field:
                field.SetValue(instance, value);
                break;
            case PropertyInfo property:
                property.SetValue(instance, value);
                break;
            default:
                throw new InvalidOperationException($"Cannot set member '{member.Name}'.");
        }

        return instance;
    }

    private static object? GetMember(object instance, MemberInfo member)
        => member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property => property.GetValue(instance),
            _ => throw new InvalidOperationException($"Cannot read member '{member.Name}'."),
        };

    private sealed record Level(IComplexProperty Property, MemberInfo Getter, MemberInfo Setter);
}
