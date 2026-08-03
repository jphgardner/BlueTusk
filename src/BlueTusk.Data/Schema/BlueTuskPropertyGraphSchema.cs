namespace BlueTusk.Data.Schema;

/// <summary>Identifies a PostgreSQL catalogue object.</summary>
public sealed record BlueTuskSchemaObjectName(string Catalog, string Schema, string Name);

/// <summary>Distinguishes vertex and edge element tables.</summary>
public enum BlueTuskPropertyGraphElementKind
{
    Vertex,
    Edge,
}

/// <summary>Distinguishes the source and destination endpoint of an edge table.</summary>
public enum BlueTuskPropertyGraphEdgeEnd
{
    Source,
    Destination,
}

/// <summary>Describes one key column on a property-graph element table.</summary>
public sealed record BlueTuskPropertyGraphKeyColumn(string Name, int OrdinalPosition);

/// <summary>Maps an edge-table column to the corresponding vertex-table key column.</summary>
public sealed record BlueTuskPropertyGraphEdgeColumnMapping(
    string EdgeTableColumn,
    string VertexTableColumn,
    int OrdinalPosition);

/// <summary>Describes one source or destination endpoint of an edge table.</summary>
public sealed class BlueTuskPropertyGraphEdgeEndpoint
{
    internal BlueTuskPropertyGraphEdgeEndpoint(
        BlueTuskPropertyGraphEdgeEnd end,
        string vertexTableAlias,
        IEnumerable<BlueTuskPropertyGraphEdgeColumnMapping> columns)
    {
        End = end;
        VertexTableAlias = vertexTableAlias;
        Columns = Array.AsReadOnly(columns.ToArray());
    }

    public BlueTuskPropertyGraphEdgeEnd End { get; }

    public string VertexTableAlias { get; }

    public IReadOnlyList<BlueTuskPropertyGraphEdgeColumnMapping> Columns { get; }
}

/// <summary>Describes a property projected by an element table.</summary>
public sealed record BlueTuskPropertyGraphElementProperty(string Name, string Expression);

/// <summary>Describes an element table participating in a property graph.</summary>
public sealed class BlueTuskPropertyGraphElementTable
{
    internal BlueTuskPropertyGraphElementTable(
        string alias,
        BlueTuskPropertyGraphElementKind kind,
        BlueTuskSchemaObjectName table,
        string? definition,
        IEnumerable<BlueTuskPropertyGraphKeyColumn> keyColumns,
        IEnumerable<string> labels,
        IEnumerable<BlueTuskPropertyGraphElementProperty> properties,
        IEnumerable<BlueTuskPropertyGraphEdgeEndpoint> endpoints)
    {
        Alias = alias;
        Kind = kind;
        Table = table;
        Definition = definition;
        KeyColumns = Array.AsReadOnly(keyColumns.ToArray());
        Labels = Array.AsReadOnly(labels.ToArray());
        Properties = Array.AsReadOnly(properties.ToArray());
        Endpoints = Array.AsReadOnly(endpoints.ToArray());
    }

    public string Alias { get; }

    public BlueTuskPropertyGraphElementKind Kind { get; }

    public BlueTuskSchemaObjectName Table { get; }

    public string? Definition { get; }

    public IReadOnlyList<BlueTuskPropertyGraphKeyColumn> KeyColumns { get; }

    public IReadOnlyList<string> Labels { get; }

    public IReadOnlyList<BlueTuskPropertyGraphElementProperty> Properties { get; }

    public IReadOnlyList<BlueTuskPropertyGraphEdgeEndpoint> Endpoints { get; }
}

/// <summary>Describes a label and the properties it exposes.</summary>
public sealed class BlueTuskPropertyGraphLabel
{
    internal BlueTuskPropertyGraphLabel(string name, IEnumerable<string> properties)
    {
        Name = name;
        Properties = Array.AsReadOnly(properties.ToArray());
    }

    public string Name { get; }

    public IReadOnlyList<string> Properties { get; }
}

/// <summary>
/// Describes the SQL data type shared by a named property in a property graph.
/// The facets mirror PostgreSQL 19's <c>information_schema.pg_property_data_types</c> view.
/// </summary>
public sealed record BlueTuskPropertyGraphPropertyDataType(
    string PropertyName,
    string DataType,
    int? CharacterMaximumLength,
    int? CharacterOctetLength,
    string? CharacterSetCatalog,
    string? CharacterSetSchema,
    string? CharacterSetName,
    string? CollationCatalog,
    string? CollationSchema,
    string? CollationName,
    int? NumericPrecision,
    int? NumericPrecisionRadix,
    int? NumericScale,
    int? DateTimePrecision,
    string? IntervalType,
    int? IntervalPrecision,
    string? UserDefinedTypeCatalog,
    string? UserDefinedTypeSchema,
    string? UserDefinedTypeName,
    string? ScopeCatalog,
    string? ScopeSchema,
    string? ScopeName,
    long? MaximumCardinality,
    string DtdIdentifier);

/// <summary>Describes a PostgreSQL 19 SQL/PGQ property graph.</summary>
public sealed class BlueTuskPropertyGraphSchema
{
    internal BlueTuskPropertyGraphSchema(
        BlueTuskSchemaObjectName name,
        IEnumerable<BlueTuskPropertyGraphElementTable> elementTables,
        IEnumerable<BlueTuskPropertyGraphLabel> labels,
        IEnumerable<BlueTuskPropertyGraphPropertyDataType> propertyDataTypes)
    {
        Name = name;
        ElementTables = Array.AsReadOnly(elementTables.ToArray());
        Labels = Array.AsReadOnly(labels.ToArray());
        PropertyDataTypes = Array.AsReadOnly(propertyDataTypes.ToArray());
    }

    public BlueTuskSchemaObjectName Name { get; }

    public IReadOnlyList<BlueTuskPropertyGraphElementTable> ElementTables { get; }

    public IReadOnlyList<BlueTuskPropertyGraphLabel> Labels { get; }

    public IReadOnlyList<BlueTuskPropertyGraphPropertyDataType> PropertyDataTypes { get; }
}

/// <summary>Filters property-graph catalogue discovery.</summary>
public sealed record BlueTuskPropertyGraphInspectionOptions
{
    public string? Catalog { get; init; }

    public string? Schema { get; init; }

    public string? Name { get; init; }
}
