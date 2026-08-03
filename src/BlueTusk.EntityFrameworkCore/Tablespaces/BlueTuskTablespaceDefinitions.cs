namespace BlueTusk.EntityFrameworkCore.Tablespaces;

/// <summary>A PostgreSQL tablespace option.</summary>
public sealed record BlueTuskTablespaceOptionDefinition(string Name, string Value);

/// <summary>A provider-owned PostgreSQL cluster-wide tablespace.</summary>
public sealed record BlueTuskTablespaceDefinition(
    string Name,
    string Location,
    string? Owner,
    IReadOnlyList<BlueTuskTablespaceOptionDefinition> Options,
    string? Comment);

/// <summary>All provider-owned PostgreSQL tablespaces in a model.</summary>
public sealed record BlueTuskTablespaceDefinitionSet(IReadOnlyList<BlueTuskTablespaceDefinition> Tablespaces)
{
    /// <summary>An empty tablespace definition set.</summary>
    public static BlueTuskTablespaceDefinitionSet Empty { get; } = new([]);
}
