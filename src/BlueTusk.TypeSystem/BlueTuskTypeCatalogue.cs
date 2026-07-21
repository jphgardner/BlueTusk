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

    public char Delimiter { get; init; } = ',';
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
        }

        RegisterArrayCodecs(builder, descriptors);

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
            Delimiter = type.Delimiter,
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

            builder.RegisterOrReplaceCodec(matches[0].Id, registration.Value);
        }
    }

    private static void RegisterArrayCodecs(
        BlueTuskTypeRegistryBuilder builder,
        IReadOnlyDictionary<BlueTuskTypeId, BlueTuskTypeDescriptor> descriptors)
    {
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
        }
    }
}
