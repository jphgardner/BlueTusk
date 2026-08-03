namespace BlueTusk.EntityFrameworkCore.Graphs;

/// <summary>Distinguishes vertex and edge element tables in EF property-graph metadata.</summary>
public enum BlueTuskGraphElementKind
{
    Vertex,
    Edge,
}

/// <summary>Describes a property expression and its SQL/PGQ property name.</summary>
public sealed record BlueTuskGraphPropertyDefinition(
    string Expression,
    string Name,
    bool IsColumn);

/// <summary>Describes a SQL/PGQ label and its properties.</summary>
public sealed record BlueTuskGraphLabelDefinition(
    string Name,
    IReadOnlyList<BlueTuskGraphPropertyDefinition> Properties);

/// <summary>Describes an edge source or destination mapping.</summary>
public sealed record BlueTuskGraphEndpointDefinition(
    string VertexTableAlias,
    IReadOnlyList<string> EdgeKeyColumns,
    IReadOnlyList<string> VertexKeyColumns);

/// <summary>Describes one vertex or edge table in an EF property graph.</summary>
public sealed record BlueTuskGraphElementTableDefinition(
    string Alias,
    BlueTuskGraphElementKind Kind,
    string Table,
    string? Schema,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<BlueTuskGraphLabelDefinition> Labels,
    BlueTuskGraphEndpointDefinition? Source,
    BlueTuskGraphEndpointDefinition? Destination);

/// <summary>Describes a PostgreSQL 19 property graph in an EF model.</summary>
public sealed record BlueTuskPropertyGraphDefinition(
    string Name,
    string? Schema,
    IReadOnlyList<BlueTuskGraphElementTableDefinition> ElementTables);
