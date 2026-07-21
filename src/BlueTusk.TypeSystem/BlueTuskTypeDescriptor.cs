namespace BlueTusk.TypeSystem;

public sealed record BlueTuskCompositeField
{
    public required int Position { get; init; }

    public required string Name { get; init; }

    public required BlueTuskTypeId Type { get; init; }
}

/// <summary>Catalogue-derived metadata for a PostgreSQL type.</summary>
public sealed record BlueTuskTypeDescriptor
{
    public required BlueTuskTypeId Id { get; init; }

    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required BlueTuskTypeKind Kind { get; init; }

    public BlueTuskTypeId? ElementType { get; init; }

    public BlueTuskTypeId? BaseType { get; init; }

    public BlueTuskTypeId? ArrayType { get; init; }

    public BlueTuskTypeId? RangeSubtype { get; init; }

    public char Delimiter { get; init; } = ',';

    public IReadOnlyList<string> EnumLabels { get; init; } = Array.Empty<string>();

    public IReadOnlyList<BlueTuskCompositeField> CompositeFields { get; init; } =
        Array.Empty<BlueTuskCompositeField>();

    public string QualifiedName => $"{Schema}.{Name}";
}
