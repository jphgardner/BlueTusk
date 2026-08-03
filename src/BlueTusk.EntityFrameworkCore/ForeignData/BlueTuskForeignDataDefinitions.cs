namespace BlueTusk.EntityFrameworkCore.ForeignData;

/// <summary>An arbitrary option validated by a PostgreSQL foreign-data wrapper.</summary>
public sealed record BlueTuskForeignOptionDefinition(string Name, string Value);

/// <summary>A provider-owned PostgreSQL foreign-data wrapper.</summary>
public sealed record BlueTuskForeignDataWrapperDefinition(
    string Name,
    string? HandlerFunction,
    string? ValidatorFunction,
    string? ConnectionFunction,
    IReadOnlyList<BlueTuskForeignOptionDefinition> Options);

/// <summary>A provider-owned PostgreSQL foreign server.</summary>
public sealed record BlueTuskForeignServerDefinition(
    string Name,
    string ForeignDataWrapper,
    string? Type,
    string? Version,
    IReadOnlyList<BlueTuskForeignOptionDefinition> Options);

/// <summary>A local role or PUBLIC mapping to a PostgreSQL foreign server.</summary>
public sealed record BlueTuskUserMappingDefinition(
    string ServerName,
    string? UserName,
    IReadOnlyList<BlueTuskForeignOptionDefinition> Options,
    bool OptionsRedacted = false);

/// <summary>Provider-owned foreign-data wrappers, servers, and user mappings.</summary>
public sealed record BlueTuskForeignDataDefinitionSet(
    IReadOnlyList<BlueTuskForeignDataWrapperDefinition> Wrappers,
    IReadOnlyList<BlueTuskForeignServerDefinition> Servers,
    IReadOnlyList<BlueTuskUserMappingDefinition> UserMappings)
{
    /// <summary>An empty foreign-data definition set.</summary>
    public static BlueTuskForeignDataDefinitionSet Empty { get; } = new([], [], []);
}

/// <summary>Foreign-data-wrapper options attached to one foreign-table column.</summary>
public sealed record BlueTuskForeignColumnDefinition(
    string Name,
    IReadOnlyList<BlueTuskForeignOptionDefinition> Options);

/// <summary>Identifies an EF table mapping as a PostgreSQL foreign table.</summary>
public sealed record BlueTuskForeignTableDefinition(
    string ServerName,
    IReadOnlyList<BlueTuskForeignOptionDefinition> Options,
    IReadOnlyList<BlueTuskForeignColumnDefinition> Columns);
