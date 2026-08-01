namespace BlueTusk.EntityFrameworkCore.Extensions;

/// <summary>A provider-owned PostgreSQL extension installation.</summary>
public sealed record BlueTuskExtensionDefinition(
    string Name,
    string? Schema,
    string? Version,
    IReadOnlyList<string> Dependencies,
    bool InstallDependencies = false);

/// <summary>Provider-owned PostgreSQL extension installations.</summary>
public sealed record BlueTuskExtensionDefinitionSet(IReadOnlyList<BlueTuskExtensionDefinition> Extensions)
{
    /// <summary>An empty extension definition set.</summary>
    public static BlueTuskExtensionDefinitionSet Empty { get; } = new([]);
}
