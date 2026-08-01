namespace BlueTusk.EntityFrameworkCore.TableInheritance;

/// <summary>A direct parent in a PostgreSQL table-inheritance hierarchy.</summary>
public sealed record BlueTuskInheritedTableDefinition(
    string Name,
    string? Schema = null);

/// <summary>PostgreSQL table-inheritance metadata for one child table.</summary>
public sealed record BlueTuskTableInheritanceDefinition(
    IReadOnlyList<BlueTuskInheritedTableDefinition> Parents);

/// <summary>A named PostgreSQL child table and its direct inheritance parents.</summary>
public sealed record BlueTuskTableInheritanceTableDefinition(
    string Name,
    string? Schema,
    BlueTuskTableInheritanceDefinition Inheritance);
