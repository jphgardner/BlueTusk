namespace BlueTusk.TypeSystem;

public sealed record BlueTuskCatalogueType
{
    public required BlueTuskTypeId Id { get; init; }

    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required char PostgreSqlKind { get; init; }

    public required char PostgreSqlCategory { get; init; }

    public BlueTuskTypeId? ElementType { get; init; }

    public BlueTuskTypeId? BaseType { get; init; }

    public BlueTuskTypeId? ArrayType { get; init; }

    public BlueTuskTypeId? RangeSubtype { get; init; }

    public BlueTuskTypeId? RangeType { get; init; }

    public BlueTuskTypeId? MultirangeType { get; init; }

    public char Delimiter { get; init; } = ',';

    public IReadOnlyList<string> EnumLabels { get; init; } = Array.Empty<string>();

    public IReadOnlyList<BlueTuskCompositeField> CompositeFields { get; init; } =
        Array.Empty<BlueTuskCompositeField>();
}

public static class BlueTuskTypeCatalogue
{
    public static BlueTuskTypeRegistry BuildRegistry(
        IEnumerable<BlueTuskCatalogueType> catalogueTypes,
        BlueTuskTypeRegistry? configuredTypes = null,
        BlueTuskMoneyFormat? moneyFormat = null)
    {
        ArgumentNullException.ThrowIfNull(catalogueTypes);
        var builder = new BlueTuskTypeRegistryBuilder();
        var descriptors = new Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor>();
        Dictionary<BlueTuskTypeId, IBlueTuskDeferredCodec> deferredCodecs =
            new Dictionary<BlueTuskTypeId, IBlueTuskDeferredCodec>();
        var hasDiscoveredTypes = false;
        foreach (var catalogueType in catalogueTypes)
        {
            ArgumentNullException.ThrowIfNull(catalogueType);
            hasDiscoveredTypes = true;
            var descriptor = CreateDescriptor(catalogueType);
            if (!descriptors.TryAdd(descriptor.Id, descriptor))
            {
                throw new InvalidOperationException($"PostgreSQL catalogue returned duplicate type OID {descriptor.Id}.");
            }

            builder.Register(descriptor);
        }

        var builtInTypes = BlueTuskBuiltInTypes.CreateRegistry();
        RegisterMissingDescriptors(builder, descriptors, builtInTypes);
        RegisterCodecs(builder, descriptors, builtInTypes, replace: false);
        if (moneyFormat is not null && descriptors.ContainsKey(BlueTuskBuiltInTypes.Money.Id))
        {
            builder.RegisterCodec(BlueTuskBuiltInTypes.Money.Id, new BlueTuskMoneyCodec(moneyFormat));
        }

        if (configuredTypes is not null)
        {
            RegisterMissingDescriptors(builder, descriptors, configuredTypes);
            RegisterCodecs(builder, descriptors, configuredTypes, replace: true);
            RegisterNamedCodecs(
                builder,
                descriptors,
                configuredTypes,
                requireResolution: hasDiscoveredTypes);
            if (hasDiscoveredTypes)
            {
                deferredCodecs = ResolveDeferredCodecs(descriptors, configuredTypes);
            }
        }

        RegisterEnumCodecs(builder, descriptors);
        RegisterDependentCodecs(builder, descriptors, deferredCodecs);
        RegisterAnonymousRecordCodec(builder, descriptors);

        return builder.Build();
    }

    public static BlueTuskTypeDescriptor CreateDescriptor(BlueTuskCatalogueType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new BlueTuskTypeDescriptor
        {
            Id = type.Id,
            Schema = type.Schema,
            Name = type.Name,
            Kind = type.PostgreSqlKind switch
            {
                'b' when type.PostgreSqlCategory == 'A' && type.ElementType is not null => BlueTuskTypeKind.Array,
                'b' => BlueTuskTypeKind.Base,
                'c' => BlueTuskTypeKind.Composite,
                'd' => BlueTuskTypeKind.Domain,
                'e' => BlueTuskTypeKind.Enum,
                'm' => BlueTuskTypeKind.Multirange,
                'p' => BlueTuskTypeKind.Pseudo,
                'r' => BlueTuskTypeKind.Range,
                _ => BlueTuskTypeKind.Unknown,
            },
            ElementType = type.ElementType,
            BaseType = type.BaseType,
            ArrayType = type.ArrayType,
            RangeSubtype = type.RangeSubtype,
            RangeType = type.RangeType,
            MultirangeType = type.MultirangeType,
            Delimiter = type.Delimiter,
            EnumLabels = type.EnumLabels.ToArray(),
            CompositeFields = type.CompositeFields.ToArray(),
        };
    }

    private static void RegisterMissingDescriptors(
        BlueTuskTypeRegistryBuilder builder,
        Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors,
        BlueTuskTypeRegistry source)
    {
        foreach (var descriptor in source.Types)
        {
            if (descriptors.TryGetValue(descriptor.Id, out var discovered))
            {
                if (!string.Equals(discovered.Schema, descriptor.Schema, StringComparison.Ordinal) ||
                    !string.Equals(discovered.Name, descriptor.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Configured PostgreSQL type OID {descriptor.Id} is {descriptor.QualifiedName}, " +
                        $"but the catalogue reports {discovered.QualifiedName}.");
                }

                continue;
            }

            builder.Register(descriptor);
            descriptors.Add(descriptor.Id, descriptor);
        }
    }

    private static void RegisterCodecs(
        BlueTuskTypeRegistryBuilder builder,
        Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors,
        BlueTuskTypeRegistry source,
        bool replace)
    {
        foreach (var registration in source.Codecs)
        {
            if (!descriptors.ContainsKey(registration.Key))
            {
                throw new InvalidOperationException(
                    $"Codec for PostgreSQL type OID {registration.Key} does not have a descriptor.");
            }

            if (replace)
            {
                builder.RegisterOrReplaceCodec(registration.Key, registration.Value);
            }
            else
            {
                builder.RegisterCodec(registration.Key, registration.Value);
            }
        }
    }

    private static void RegisterNamedCodecs(
        BlueTuskTypeRegistryBuilder builder,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors,
        BlueTuskTypeRegistry source,
        bool requireResolution)
    {
        foreach (var registration in source.NamedCodecs)
        {
            var matches = descriptors.Values
                .Where(type =>
                    string.Equals(type.Schema, registration.Key.Schema, StringComparison.Ordinal) &&
                    string.Equals(type.Name, registration.Key.Name, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                if (!requireResolution && matches.Length == 0)
                {
                    builder.RegisterNamedCodec(registration.Key, registration.Value);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Named codec registration for PostgreSQL type {registration.Key} resolved to {matches.Length} catalogue types.");
            }

            if (registration.Value is not IBlueTuskDeferredCodec)
            {
                builder.RegisterOrReplaceCodec(matches[0].Id, registration.Value);
            }
        }
    }

    private static Dictionary<BlueTuskTypeId, IBlueTuskDeferredCodec> ResolveDeferredCodecs(
        Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors,
        BlueTuskTypeRegistry source)
    {
        var result = new Dictionary<BlueTuskTypeId, IBlueTuskDeferredCodec>();
        foreach (var registration in source.NamedCodecs)
        {
            if (registration.Value is not IBlueTuskDeferredCodec deferred)
            {
                continue;
            }

            var match = descriptors.Values.Single(type =>
                string.Equals(type.Schema, registration.Key.Schema, StringComparison.Ordinal) &&
                string.Equals(type.Name, registration.Key.Name, StringComparison.Ordinal));
            result.Add(match.Id, deferred);
        }

        return result;
    }

    private static void RegisterEnumCodecs(
        BlueTuskTypeRegistryBuilder builder,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors)
    {
        foreach (var enumType in descriptors.Values.Where(type => type.Kind == BlueTuskTypeKind.Enum))
        {
            if (!builder.ContainsCodec(enumType.Id))
            {
                builder.RegisterCodec(enumType.Id, new BlueTuskEnumValueCodec());
            }
        }
    }

    private static void RegisterDependentCodecs(
        BlueTuskTypeRegistryBuilder builder,
        Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors,
        Dictionary<BlueTuskTypeId, IBlueTuskDeferredCodec> deferredCodecs)
    {
        var unresolved = new Dictionary<BlueTuskTypeId, IBlueTuskDeferredCodec>(deferredCodecs);
        bool changed;
        do
        {
            changed = RegisterDomainCodecs(builder, descriptors);
            changed |= RegisterDeferredCodecs(builder, descriptors, unresolved);
            changed |= RegisterCompositeCodecs(builder, descriptors, deferredCodecs);
            changed |= RegisterRangeCodecs(builder, descriptors);
            changed |= RegisterMultirangeCodecs(builder, descriptors);
            changed |= RegisterArrayCodecs(builder, descriptors);
        }
        while (changed);

        if (unresolved.Count != 0)
        {
            var names = string.Join(
                ", ",
                unresolved.Keys.Select(id => descriptors[id].QualifiedName));
            throw new InvalidOperationException(
                $"CLR composite mappings could not resolve codecs for every field of: {names}.");
        }
    }

    private static bool RegisterDeferredCodecs(
        BlueTuskTypeRegistryBuilder builder,
        Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors,
        Dictionary<BlueTuskTypeId, IBlueTuskDeferredCodec> unresolved)
    {
        var changed = false;
        foreach (var registration in unresolved.ToArray())
        {
            if (!registration.Value.TryBind(
                    descriptors[registration.Key],
                    descriptors,
                    builder,
                    out var codec))
            {
                continue;
            }

            builder.RegisterOrReplaceCodec(registration.Key, codec);
            unresolved.Remove(registration.Key);
            changed = true;
        }

        return changed;
    }

    private static bool RegisterDomainCodecs(
        BlueTuskTypeRegistryBuilder builder,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors)
    {
        var changed = false;
        foreach (var domainType in descriptors.Values.Where(type => type.Kind == BlueTuskTypeKind.Domain))
        {
            if (builder.ContainsCodec(domainType.Id) ||
                domainType.BaseType is not { } baseTypeId ||
                !descriptors.TryGetValue(baseTypeId, out var baseType) ||
                !builder.TryGetCodec(baseTypeId, out var baseCodec) ||
                baseCodec is null)
            {
                continue;
            }

            builder.RegisterCodec(domainType.Id, new BlueTuskDomainCodec(baseType, baseCodec));
            changed = true;
        }

        return changed;
    }

    private static bool RegisterCompositeCodecs(
        BlueTuskTypeRegistryBuilder builder,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors,
        Dictionary<BlueTuskTypeId, IBlueTuskDeferredCodec> mappedTypes)
    {
        var changed = false;
        foreach (var compositeType in descriptors.Values.Where(type => type.Kind == BlueTuskTypeKind.Composite))
        {
            if (mappedTypes.ContainsKey(compositeType.Id) ||
                builder.ContainsCodec(compositeType.Id) ||
                !BlueTuskRecordCodec.TryCreate(compositeType, descriptors, builder, out var codec))
            {
                continue;
            }

            builder.RegisterCodec(compositeType.Id, codec);
            changed = true;
        }

        return changed;
    }

    private static bool RegisterArrayCodecs(
        BlueTuskTypeRegistryBuilder builder,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors)
    {
        var changed = false;
        foreach (var arrayType in descriptors.Values.Where(type => type.Kind == BlueTuskTypeKind.Array))
        {
            if (builder.ContainsCodec(arrayType.Id) ||
                arrayType.ElementType is not { } elementTypeId ||
                !descriptors.TryGetValue(elementTypeId, out var elementType) ||
                !builder.TryGetCodec(elementTypeId, out var elementCodec) ||
                elementCodec is null)
            {
                continue;
            }

            builder.RegisterCodec(arrayType.Id, new BlueTuskArrayCodec(elementType, elementCodec));
            changed = true;
        }

        return changed;
    }

    private static bool RegisterRangeCodecs(
        BlueTuskTypeRegistryBuilder builder,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors)
    {
        var changed = false;
        foreach (var rangeType in descriptors.Values.Where(type => type.Kind == BlueTuskTypeKind.Range))
        {
            if (builder.ContainsCodec(rangeType.Id) ||
                rangeType.RangeSubtype is not { } subtypeId ||
                !descriptors.TryGetValue(subtypeId, out var subtype) ||
                !builder.TryGetCodec(subtypeId, out var subtypeCodec) ||
                subtypeCodec is not IBlueTuskRangeCodecFactory factory)
            {
                continue;
            }

            var codec = factory.CreateRangeCodec(subtype, subtypeCodec);
            if (codec is null)
            {
                continue;
            }

            builder.RegisterCodec(rangeType.Id, codec);
            changed = true;
        }

        return changed;
    }

    private static bool RegisterMultirangeCodecs(
        BlueTuskTypeRegistryBuilder builder,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors)
    {
        var changed = false;
        foreach (var multirangeType in descriptors.Values.Where(
                     type => type.Kind == BlueTuskTypeKind.Multirange))
        {
            if (builder.ContainsCodec(multirangeType.Id) ||
                multirangeType.RangeType is not { } rangeTypeId ||
                !descriptors.TryGetValue(rangeTypeId, out var rangeType) ||
                !builder.TryGetCodec(rangeTypeId, out var rangeCodec) ||
                rangeCodec is not IBlueTuskMultirangeCodecFactory factory)
            {
                continue;
            }

            builder.RegisterCodec(
                multirangeType.Id,
                factory.CreateMultirangeCodec(rangeType));
            changed = true;
        }

        return changed;
    }

    private static void RegisterAnonymousRecordCodec(
        BlueTuskTypeRegistryBuilder builder,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors)
    {
        var recordTypes = descriptors.Values
            .Where(type =>
                type.Kind == BlueTuskTypeKind.Pseudo &&
                string.Equals(type.Schema, "pg_catalog", StringComparison.Ordinal) &&
                string.Equals(type.Name, "record", StringComparison.Ordinal))
            .ToArray();
        if (recordTypes.Length == 1 && !builder.ContainsCodec(recordTypes[0].Id))
        {
            builder.RegisterCodec(
                recordTypes[0].Id,
                BlueTuskRecordCodec.CreateAnonymous(descriptors, builder));
        }
    }
}
