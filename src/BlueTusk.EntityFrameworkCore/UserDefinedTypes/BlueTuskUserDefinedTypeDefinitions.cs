namespace BlueTusk.EntityFrameworkCore.UserDefinedTypes;

/// <summary>The PostgreSQL user-defined schema object represented by a migration operation.</summary>
public enum BlueTuskUserDefinedTypeKind
{
    /// <summary>An enumerated type.</summary>
    Enum,

    /// <summary>A domain.</summary>
    Domain,

    /// <summary>A standalone composite type.</summary>
    Composite,

    /// <summary>A range type and its PostgreSQL-created multirange type.</summary>
    Range,
}

/// <summary>A PostgreSQL enumerated type and its ordered labels.</summary>
public sealed record BlueTuskEnumTypeDefinition(
    string Name,
    string? Schema,
    IReadOnlyList<string> Labels);

/// <summary>A named PostgreSQL domain check constraint.</summary>
public sealed record BlueTuskDomainConstraintDefinition(
    string Name,
    string CheckSql,
    bool IsValidated = true);

/// <summary>A PostgreSQL domain over an existing store type.</summary>
public sealed record BlueTuskDomainTypeDefinition(
    string Name,
    string? Schema,
    string BaseStoreType,
    string? Collation,
    string? DefaultSql,
    bool IsNotNull,
    IReadOnlyList<BlueTuskDomainConstraintDefinition> Constraints);

/// <summary>An attribute in a standalone PostgreSQL composite type.</summary>
public sealed record BlueTuskCompositeAttributeDefinition(
    string Name,
    string StoreType,
    string? Collation = null);

/// <summary>A standalone PostgreSQL composite type.</summary>
public sealed record BlueTuskCompositeTypeDefinition(
    string Name,
    string? Schema,
    IReadOnlyList<BlueTuskCompositeAttributeDefinition> Attributes);

/// <summary>A schema object name used by a PostgreSQL type definition.</summary>
public sealed record BlueTuskQualifiedName(string Name, string? Schema = null);

/// <summary>A PostgreSQL range type and its automatically created multirange type.</summary>
public sealed record BlueTuskRangeTypeDefinition(
    string Name,
    string? Schema,
    BlueTuskQualifiedName Subtype,
    BlueTuskQualifiedName? SubtypeOperatorClass,
    BlueTuskQualifiedName? Collation,
    BlueTuskQualifiedName? CanonicalFunction,
    BlueTuskQualifiedName? SubtypeDifferenceFunction,
    BlueTuskQualifiedName MultirangeType);

/// <summary>Provider-owned PostgreSQL enum, domain, composite, range, and multirange schema objects.</summary>
public sealed record BlueTuskUserDefinedTypeDefinitionSet(
    IReadOnlyList<BlueTuskEnumTypeDefinition> Enums,
    IReadOnlyList<BlueTuskDomainTypeDefinition> Domains,
    IReadOnlyList<BlueTuskCompositeTypeDefinition> Composites)
{
    /// <summary>Provider-owned range types and their paired multirange types.</summary>
    public IReadOnlyList<BlueTuskRangeTypeDefinition> Ranges { get; init; } = [];

    /// <summary>An empty definition set.</summary>
    public static BlueTuskUserDefinedTypeDefinitionSet Empty { get; } = new([], [], []);
}
