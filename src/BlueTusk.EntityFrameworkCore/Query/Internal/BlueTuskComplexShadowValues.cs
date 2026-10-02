#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using BlueTusk.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

// Shadow properties of JSON-mapped complex types are read by the streaming JSON shaper while the complex instance is
// materialized, before the owning entity is tracked. The materializer records them against the complex instance; they
// are merged into the entity's shadow snapshot before tracking, or written to complex collection element entries
// after tracking.
internal static class BlueTuskComplexShadowValues
{
    private const string EntityPlanAnnotation = "BlueTusk:JsonComplexShadowPlan";

    private static readonly ConditionalWeakTable<object, object?[]> Recorded = new();

    internal static readonly MethodInfo RecordMethod
        = typeof(BlueTuskComplexShadowValues).GetMethod(nameof(Record))!;

    internal static readonly MethodInfo MergeEntityShadowValuesMethod
        = typeof(BlueTuskComplexShadowValues).GetMethod(nameof(MergeEntityShadowValues))!;

    internal static readonly MethodInfo ApplyCollectionElementShadowValuesMethod
        = typeof(BlueTuskComplexShadowValues).GetMethod(nameof(ApplyCollectionElementShadowValues))!;

    public static void Record(object instance, object?[] values)
        => Recorded.AddOrUpdate(instance, values);

    internal static IReadOnlyList<IProperty> GetRecordedProperties(IComplexType complexType)
    {
        if (!complexType.IsMappedToJson() || complexType.ClrType.IsValueType)
        {
            return [];
        }

        var discriminator = complexType.FindDiscriminatorProperty();
        return [.. complexType.GetProperties().Where(p => p.IsShadowProperty() && p != discriminator)];
    }

    internal static bool HasRecordedProperties(IEntityType entityType)
        => entityType.GetDerivedTypesInclusive().Any(t => HasRecordedProperties((ITypeBase)t));

    private static bool HasRecordedProperties(ITypeBase structuralType)
        => structuralType.GetComplexProperties().Any(p =>
            GetRecordedProperties(p.ComplexType).Count > 0 || HasRecordedProperties(p.ComplexType));

    internal static bool HasRecordedEntityShadowValues(IEntityType entityType)
        => entityType.GetDerivedTypesInclusive().Any(t => GetPlan((IRuntimeEntityType)t).Items.Length > 0);

    internal static bool IsRecordedEntityShadowProperty(IPropertyBase property)
        => property is IProperty { DeclaringType: IComplexType complexType } scalar
            && GetRecordedProperties(complexType).Contains(scalar);

    public static ISnapshot MergeEntityShadowValues(IEntityType? entityType, object? entity, ISnapshot snapshot)
    {
        if (entityType is not IRuntimeEntityType runtimeEntityType || entity is null)
        {
            return snapshot;
        }

        var plan = GetPlan(runtimeEntityType);
        if (plan.Items.Length == 0)
        {
            return snapshot;
        }

        var values = new object?[runtimeEntityType.ShadowPropertyCount];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = snapshot[i];
        }

        foreach (var (shadowIndex, complexProperty, position) in plan.Items)
        {
            var container = complexProperty.GetGetter().GetClrValueUsingContainingEntity(entity);
            if (container is not null && Recorded.TryGetValue(container, out var recorded))
            {
                values[shadowIndex] = recorded[position];
            }
        }

        return plan.Factory(values);
    }

    public static void ApplyCollectionElementShadowValues(IInternalEntry? entry)
    {
        if (entry is not null)
        {
            ApplyCollectionElementShadowValues(entry, entry.StructuralType);
        }
    }

    private static void ApplyCollectionElementShadowValues(IInternalEntry entry, ITypeBase structuralType)
    {
        foreach (var complexProperty in structuralType.GetComplexProperties())
        {
            if (!complexProperty.IsCollection)
            {
                ApplyCollectionElementShadowValues(entry, complexProperty.ComplexType);
                continue;
            }

            if (entry.GetCurrentValue(complexProperty) is not IList elements)
            {
                continue;
            }

            var recordedProperties = GetRecordedProperties(complexProperty.ComplexType);
            for (var ordinal = 0; ordinal < elements.Count; ordinal++)
            {
                var element = elements[ordinal];
                if (element is null)
                {
                    continue;
                }

                var elementEntry = entry.GetComplexCollectionEntry(complexProperty, ordinal);
                if (recordedProperties.Count > 0 && Recorded.TryGetValue(element, out var recorded))
                {
                    for (var position = 0; position < recordedProperties.Count; position++)
                    {
                        elementEntry.SetProperty(recordedProperties[position], recorded[position], isMaterialization: true, setModified: false);
                        elementEntry.SetOriginalValue(recordedProperties[position], recorded[position]);
                    }
                }

                ApplyCollectionElementShadowValues(elementEntry, complexProperty.ComplexType);
            }
        }
    }

    private static EntityPlan GetPlan(IRuntimeEntityType entityType)
        => entityType.GetOrAddRuntimeAnnotationValue(EntityPlanAnnotation, static type => CreatePlan(type!), entityType);

    private static EntityPlan CreatePlan(IRuntimeEntityType entityType)
    {
        var items = new List<(int, IComplexProperty, int)>();
        AddItems(entityType, items);
        if (items.Count == 0)
        {
            return new EntityPlan([], static values => throw new InvalidOperationException());
        }

        var parameter = Expression.Parameter(typeof(object?[]), "values");
        var body = BlueTuskShadowValuesFactoryFactory.Instance.CreateConstructorExpression(
            entityType,
            Expression.NewArrayInit(
                typeof(object),
                Enumerable.Range(0, entityType.ShadowPropertyCount)
                    .Select(i => Expression.Convert(Expression.ArrayIndex(parameter, Expression.Constant(i)), typeof(object)))));
        return new EntityPlan([.. items], Expression.Lambda<Func<object?[], ISnapshot>>(body, parameter).Compile());

        static void AddItems(ITypeBase structuralType, List<(int, IComplexProperty, int)> items)
        {
            foreach (var complexProperty in structuralType.GetComplexProperties())
            {
                if (complexProperty.IsCollection)
                {
                    continue;
                }

                var recordedProperties = GetRecordedProperties(complexProperty.ComplexType);
                for (var position = 0; position < recordedProperties.Count; position++)
                {
                    items.Add((recordedProperties[position].GetShadowIndex(), complexProperty, position));
                }

                AddItems(complexProperty.ComplexType, items);
            }
        }
    }

    private sealed record EntityPlan((int ShadowIndex, IComplexProperty ComplexProperty, int Position)[] Items, Func<object?[], ISnapshot> Factory);
}
