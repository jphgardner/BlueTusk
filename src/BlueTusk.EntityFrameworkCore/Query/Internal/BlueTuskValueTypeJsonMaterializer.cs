#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage.Json;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

// EF Core's streaming JSON shaper materializes complex collections through client methods constrained to reference
// element types, so a JSON document holding a value-type complex collection cannot be queried (dotnet/efcore#31411).
// For a JSON-mapped complex property whose value contains such a collection, BlueTusk reads the document with EF Core's
// JSON reader and each property's JSON value reader/writer, creates every complex value through its constructor
// binding and assigns the remaining members through EF Core's materialization setters, as the generated shaper does.
internal static class BlueTuskValueTypeJsonMaterializer
{
    private const string PlanAnnotation = "BlueTusk:ValueTypeJsonPlan";

    internal static readonly MethodInfo IncludeMethod
        = typeof(BlueTuskValueTypeJsonMaterializer).GetMethod(nameof(Include))!;

    internal static bool ContainsValueTypeCollection(IComplexProperty complexProperty)
        => (complexProperty.IsCollection && complexProperty.ComplexType.ClrType.IsValueType)
            || complexProperty.ComplexType.GetComplexProperties().Any(ContainsValueTypeCollection);

    internal static bool ContainsValueTypeCollection(ITypeBase structuralType)
        => (structuralType is IEntityType entityType ? entityType.GetDerivedTypesInclusive().Cast<ITypeBase>() : [structuralType])
            .Any(type => type.GetComplexProperties().Any(p => p.ComplexType.IsMappedToJson() && ContainsValueTypeCollection(p)));

    internal static readonly MethodInfo MaterializeCollectionMethod
        = typeof(BlueTuskValueTypeJsonMaterializer).GetMethod(nameof(MaterializeCollection))!;

    internal static readonly MethodInfo MaterializeComplexValueMethod
        = typeof(BlueTuskValueTypeJsonMaterializer).GetMethod(nameof(MaterializeComplexValue))!;

    // Builds and caches the plans for the whole complex value up front, so unsupported mappings fail at query compilation.
    internal static void Prepare(IComplexProperty complexProperty)
        => _ = GetMemberPlan(complexProperty);

    internal static void Prepare(IComplexType complexType)
        => _ = GetPlan(complexType);

    // A complex collection projected on its own.
    public static object? MaterializeCollection(
        QueryContext queryContext,
        JsonReaderData? jsonReaderData,
        IPropertyBase structuralProperty)
    {
        if (jsonReaderData is null)
        {
            return null;
        }

        var manager = new Utf8JsonReaderManager(jsonReaderData, queryContext.QueryLogger);
        var value = ReadComplexValue(ref manager, GetMemberPlan((IComplexProperty)structuralProperty));
        manager.CaptureState();
        return value;
    }

    // A complex value projected on its own.
    public static object? MaterializeComplexValue(
        QueryContext queryContext,
        JsonReaderData? jsonReaderData,
        IComplexType complexType)
    {
        if (jsonReaderData is null)
        {
            return null;
        }

        var manager = new Utf8JsonReaderManager(jsonReaderData, queryContext.QueryLogger);
        var tokenType = manager.CurrentReader.TokenType;
        var value = tokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.StartObject => ReadObject(ref manager, GetPlan(complexType)),
            _ => throw InvalidToken(tokenType),
        };
        manager.CaptureState();
        return value;
    }

    public static object? Include(
        QueryContext queryContext,
        JsonReaderData? jsonReaderData,
        object? instance,
        IPropertyBase structuralProperty)
    {
        var complexProperty = (IComplexProperty)structuralProperty;
        if (jsonReaderData is null
            || instance is null
            || !complexProperty.DeclaringType.ClrType.IsInstanceOfType(instance))
        {
            return instance;
        }

        var manager = new Utf8JsonReaderManager(jsonReaderData, queryContext.QueryLogger);
        if (manager.CurrentReader.TokenType == JsonTokenType.Null)
        {
            return instance;
        }

        var value = ReadComplexValue(ref manager, GetMemberPlan(complexProperty));
        manager.CaptureState();
        return ((IRuntimePropertyBase)complexProperty).MaterializationSetter.SetClrValue(instance, value);
    }

    private static object? ReadComplexValue(ref Utf8JsonReaderManager manager, MemberPlan member)
    {
        var tokenType = manager.CurrentReader.TokenType;
        if (tokenType == JsonTokenType.Null)
        {
            return null;
        }

        var plan = member.Nested!;
        if (!member.IsCollection)
        {
            return tokenType == JsonTokenType.StartObject
                ? ReadObject(ref manager, plan)
                : throw InvalidToken(tokenType);
        }

        if (tokenType != JsonTokenType.StartArray)
        {
            throw InvalidToken(tokenType);
        }

        var elements = new List<object>();
        while ((tokenType = manager.MoveNext()) != JsonTokenType.EndArray)
        {
            elements.Add(tokenType == JsonTokenType.StartObject ? ReadObject(ref manager, plan) : throw InvalidToken(tokenType));
        }

        return member.CreateCollection!(elements);
    }

    private static object ReadObject(ref Utf8JsonReaderManager manager, TypePlan plan)
    {
        var values = new object?[plan.Members.Length];
        var present = new bool[plan.Members.Length];
        JsonTokenType tokenType;
        while ((tokenType = manager.MoveNext()) != JsonTokenType.EndObject)
        {
            if (tokenType != JsonTokenType.PropertyName)
            {
                throw InvalidToken(tokenType);
            }

            var index = plan.FindMember(ref manager);
            if (index < 0)
            {
                manager.Skip();
                continue;
            }

            var member = plan.Members[index];
            manager.MoveNext();
            values[index] = member.Nested is null
                ? ReadScalar(ref manager, member)
                : ReadComplexValue(ref manager, member);
            present[index] = true;
        }

        return plan.Create(values, present);
    }

    private static object? ReadScalar(ref Utf8JsonReaderManager manager, MemberPlan member)
        => member.IsNullable && manager.CurrentReader.TokenType == JsonTokenType.Null
            ? null
            : member.ReaderWriter!.FromJson(ref manager);

    private static InvalidOperationException InvalidToken(JsonTokenType tokenType)
        => new(CoreStrings.JsonReaderInvalidTokenType(tokenType.ToString()));

    private static object? DefaultOf(Type type)
        => type.IsValueType && Nullable.GetUnderlyingType(type) is null ? RuntimeHelpers.GetUninitializedObject(type) : null;

    private static MemberPlan GetMemberPlan(IComplexProperty complexProperty)
        => complexProperty.GetOrAddRuntimeAnnotationValue(
            PlanAnnotation,
            static property => MemberPlan.ForComplex(property!),
            complexProperty);

    private static TypePlan GetPlan(IComplexType complexType)
        => complexType.GetOrAddRuntimeAnnotationValue(
            PlanAnnotation,
            static type => new TypePlan(type!),
            complexType);

    private sealed class TypePlan
    {
        private readonly IComplexType _complexType;
        private readonly Func<object?[], bool[], object> _instantiate;
        private readonly int[] _assignedMembers;
        private readonly IReadOnlyList<IProperty> _recordedShadowProperties;
        private readonly int[] _recordedShadowMembers;

        public TypePlan(IComplexType complexType)
        {
            _complexType = complexType;
            var members = new List<MemberPlan>();
            foreach (var property in complexType.GetProperties())
            {
                members.Add(MemberPlan.ForScalar(property));
            }

            foreach (var complexProperty in complexType.GetComplexProperties())
            {
                members.Add(MemberPlan.ForComplex(complexProperty));
            }

            Members = [.. members];
            (_instantiate, var consumed) = CreateInstantiation(complexType, Members);
            _assignedMembers = [.. Enumerable.Range(0, Members.Length)
                .Where(i => !consumed.Contains(i) && !Members[i].Property.IsShadowProperty())];
            _recordedShadowProperties = BlueTuskComplexShadowValues.GetRecordedProperties(complexType);
            _recordedShadowMembers = [.. _recordedShadowProperties.Select(p => Array.FindIndex(Members, m => m.Property == p))];
        }

        public MemberPlan[] Members { get; }

        public int FindMember(ref Utf8JsonReaderManager manager)
        {
            for (var i = 0; i < Members.Length; i++)
            {
                if (Members[i].Utf8JsonName is { } name && manager.CurrentReader.ValueTextEquals(name))
                {
                    return i;
                }
            }

            return -1;
        }

        public object Create(object?[] values, bool[] present)
        {
            var instance = _instantiate(values, present);
            foreach (var i in _assignedMembers)
            {
                var member = Members[i];
                instance = member.Setter.SetClrValue(instance, present[i] ? values[i] : member.DefaultValue);
            }

            if (_recordedShadowProperties.Count > 0)
            {
                BlueTuskComplexShadowValues.Record(
                    instance,
                    [.. _recordedShadowMembers.Select((member, position) => present[member]
                        ? values[member]
                        : DefaultOf(_recordedShadowProperties[position].ClrType))]);
            }

            return instance;
        }

        private static (Func<object?[], bool[], object> Instantiate, HashSet<int> Consumed) CreateInstantiation(
            IComplexType complexType,
            MemberPlan[] members)
        {
            var clrType = complexType.ClrType;
            switch (((IRuntimeComplexType)complexType).ConstructorBinding)
            {
                case ConstructorBinding { ParameterBindings.Count: > 0 } constructorBinding:
                    var arguments = constructorBinding.ParameterBindings.Select(binding =>
                        {
                            if (binding is not PropertyParameterBinding { ConsumedProperties: [var consumedProperty] })
                            {
                                throw new InvalidOperationException(
                                    $"BlueTusk cannot bind constructor parameter of type '{binding.ParameterType.ShortDisplayName()}' "
                                    + $"on complex type '{complexType.DisplayName()}' inside a value-type complex collection.");
                            }

                            return (Member: Array.FindIndex(members, m => m.Property == consumedProperty),
                                Default: DefaultOf(binding.ParameterType));
                        })
                        .ToArray();
                    var constructor = constructorBinding.Constructor;
                    return ((values, present) => constructor.Invoke(
                            [.. arguments.Select(a => present[a.Member] ? values[a.Member] ?? a.Default : a.Default)]),
                        [.. arguments.Select(a => a.Member)]);

                case ConstructorBinding constructorBinding:
                    var parameterless = constructorBinding.Constructor;
                    return ((_, _) => parameterless.Invoke(null), []);

                case DefaultValueBinding when clrType.IsValueType:
                    return ((_, _) => Activator.CreateInstance(clrType)!, []);

                default:
                    throw new InvalidOperationException(
                        $"BlueTusk cannot instantiate complex type '{complexType.DisplayName()}' inside a value-type complex "
                        + "collection with its configured instantiation binding.");
            }
        }
    }

    private sealed class MemberPlan
    {
        private MemberPlan(IPropertyBase property, string? jsonName)
        {
            Property = property;
            Utf8JsonName = jsonName is null ? null : Encoding.UTF8.GetBytes(jsonName);
            Setter = ((IRuntimePropertyBase)property).MaterializationSetter;
            DefaultValue = DefaultOf(property.ClrType);
        }

        public IPropertyBase Property { get; }

        public byte[]? Utf8JsonName { get; }

        public IClrPropertySetter Setter { get; }

        public object? DefaultValue { get; }

        public bool IsNullable { get; private init; }

        public bool IsCollection { get; private init; }

        public JsonValueReaderWriter? ReaderWriter { get; private init; }

        public TypePlan? Nested => NestedType is null ? null : GetPlan(NestedType);

        public Func<List<object>, object>? CreateCollection { get; private init; }

        private IComplexType? NestedType { get; init; }

        public static MemberPlan ForScalar(IProperty property)
            => new(property, property.GetJsonPropertyName())
            {
                IsNullable = property.IsNullable,
                ReaderWriter = property.GetJsonValueReaderWriter() ?? property.GetTypeMapping().JsonValueReaderWriter
                    ?? throw new InvalidOperationException(
                        $"Property '{property.DeclaringType.DisplayName()}.{property.Name}' has no JSON value reader/writer."),
            };

        public static MemberPlan ForComplex(IComplexProperty complexProperty)
        {
            var plan = new MemberPlan(complexProperty, complexProperty.GetJsonPropertyName())
            {
                IsNullable = complexProperty.IsNullable,
                IsCollection = complexProperty.IsCollection,
                NestedType = complexProperty.ComplexType,
                CreateCollection = complexProperty.IsCollection ? CreateCollectionFactory(complexProperty) : null,
            };
            _ = plan.Nested;
            return plan;
        }

        private static Func<List<object>, object> CreateCollectionFactory(IComplexProperty complexProperty)
        {
            var elementType = complexProperty.ComplexType.ClrType;
            var collectionType = complexProperty.IsShadowProperty()
                ? complexProperty.ClrType
                : complexProperty.GetMemberInfo(forMaterialization: true, forSet: true) switch
                {
                    FieldInfo field => field.FieldType,
                    PropertyInfo property => property.PropertyType,
                    _ => complexProperty.ClrType,
                };
            if (collectionType.IsArray)
            {
                return elements =>
                {
                    var array = Array.CreateInstance(elementType, elements.Count);
                    for (var i = 0; i < elements.Count; i++)
                    {
                        array.SetValue(elements[i], i);
                    }

                    return array;
                };
            }

            var concreteType = !collectionType.IsInterface && !collectionType.IsAbstract
                ? collectionType
                : typeof(ISet<>).MakeGenericType(elementType).IsAssignableFrom(collectionType)
                    ? typeof(HashSet<>).MakeGenericType(elementType)
                    : typeof(List<>).MakeGenericType(elementType);
            if (!collectionType.IsAssignableFrom(concreteType) || concreteType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException(
                    $"BlueTusk cannot create collection type '{collectionType.ShortDisplayName()}' for complex collection "
                    + $"'{complexProperty.DeclaringType.DisplayName()}.{complexProperty.Name}'.");
            }

            if (typeof(IList).IsAssignableFrom(concreteType))
            {
                return elements =>
                {
                    var list = (IList)Activator.CreateInstance(concreteType)!;
                    foreach (var element in elements)
                    {
                        list.Add(element);
                    }

                    return list;
                };
            }

            var add = typeof(ICollection<>).MakeGenericType(elementType).GetMethod(nameof(ICollection<object>.Add))!;
            return elements =>
            {
                var collection = Activator.CreateInstance(concreteType)!;
                foreach (var element in elements)
                {
                    add.Invoke(collection, [element]);
                }

                return collection;
            };
        }
    }
}
