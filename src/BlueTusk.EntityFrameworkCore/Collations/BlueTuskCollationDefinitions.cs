namespace BlueTusk.EntityFrameworkCore.Collations;

/// <summary>A PostgreSQL collation provider.</summary>
public enum BlueTuskCollationProvider
{
    /// <summary>The operating system C library.</summary>
    Libc,

    /// <summary>The International Components for Unicode library.</summary>
    Icu,

    /// <summary>PostgreSQL's built-in locale provider, available from PostgreSQL 17.</summary>
    Builtin,
}

/// <summary>A provider-owned PostgreSQL collation.</summary>
public sealed record BlueTuskCollationDefinition(
    string Name,
    string? Schema,
    BlueTuskCollationProvider? Provider,
    string? Locale,
    string? LcCollate,
    string? LcCtype,
    bool? IsDeterministic,
    string? Rules,
    string? Version);

/// <summary>Provider-owned PostgreSQL collations.</summary>
public sealed record BlueTuskCollationDefinitionSet(IReadOnlyList<BlueTuskCollationDefinition> Collations)
{
    /// <summary>An empty collation definition set.</summary>
    public static BlueTuskCollationDefinitionSet Empty { get; } = new([]);
}
