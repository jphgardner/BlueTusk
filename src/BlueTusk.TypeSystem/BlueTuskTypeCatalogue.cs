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
}

public static class BlueTuskTypeCatalogue
{
    public static BlueTuskTypeRegistry BuildRegistry(
        IEnumerable<BlueTuskCatalogueType> catalogueTypes,
        BlueTuskTypeRegistry? configuredTypes = null)
    {
        ArgumentNullException.ThrowIfNull(catalogueTypes);
        var builder = new BlueTuskTypeRegistryBuilder();
        var descriptors = new Dictionary<BlueTuskTypeId, BlueTuskTypeDescriptor>();
        foreach (var catalogueType in catalogueTypes)
        {
            ArgumentNullException.ThrowIfNull(catalogueType);
            var descriptor = CreateDescriptor(catalogueType);
            if (!descriptors.TryAdd(descriptor.Id, descriptor))
            {
                throw new InvalidOperationException($"PostgreSQL catalogue returned duplicate type OID {descriptor.Id}.");
            }

            builder.Register(descriptor);
        }

        var builtInTypes = BlueTuskBuiltInTypes.CreateRegistry();
        RegisterMissingDescriptors(builder, descriptors, builtInTypes);
        RegisterCodecs(builder, descriptors, builtInTypes);
        if (configuredTypes is not null)
        {
            RegisterMissingDescriptors(builder, descriptors, configuredTypes);
            RegisterCodecs(builder, descriptors, configuredTypes);
        }

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
        BlueTuskTypeRegistry source)
    {
        foreach (var registration in source.Codecs)
        {
            if (!descriptors.ContainsKey(registration.Key))
            {
                throw new InvalidOperationException(
                    $"Codec for PostgreSQL type OID {registration.Key} does not have a descriptor.");
            }

            builder.RegisterCodec(registration.Key, registration.Value);
        }
    }
}
