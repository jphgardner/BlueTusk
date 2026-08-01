using System.Data;
using System.Data.Common;

namespace BlueTusk.Data.Schema;

/// <summary>Discovers PostgreSQL 19 SQL/PGQ property graphs through documented information-schema views.</summary>
public sealed class BlueTuskPropertyGraphSchemaInspector
{
    private const int ResultSetCount = 9;

    private static readonly string[] CatalogueQueries =
    [
        """
        SELECT property_graph_catalog, property_graph_schema, property_graph_name
        FROM information_schema.property_graphs
        ORDER BY property_graph_catalog, property_graph_schema, property_graph_name
        """,
        """
        SELECT property_graph_catalog, property_graph_schema, property_graph_name,
               element_table_alias, element_table_kind,
               table_catalog, table_schema, table_name, element_table_definition
        FROM information_schema.pg_element_tables
        ORDER BY property_graph_catalog, property_graph_schema, property_graph_name, element_table_alias
        """,
        """
        SELECT property_graph_catalog, property_graph_schema, property_graph_name,
               element_table_alias, column_name, ordinal_position
        FROM information_schema.pg_element_table_key_columns
        ORDER BY property_graph_catalog, property_graph_schema, property_graph_name,
                 element_table_alias, ordinal_position
        """,
        """
        SELECT property_graph_catalog, property_graph_schema, property_graph_name,
               element_table_alias, label_name
        FROM information_schema.pg_element_table_labels
        ORDER BY property_graph_catalog, property_graph_schema, property_graph_name,
                 element_table_alias, label_name
        """,
        """
        SELECT property_graph_catalog, property_graph_schema, property_graph_name,
               element_table_alias, property_name, property_expression
        FROM information_schema.pg_element_table_properties
        ORDER BY property_graph_catalog, property_graph_schema, property_graph_name,
                 element_table_alias, property_name
        """,
        """
        SELECT property_graph_catalog, property_graph_schema, property_graph_name,
               edge_table_alias, vertex_table_alias, edge_end,
               edge_table_column_name, vertex_table_column_name, ordinal_position
        FROM information_schema.pg_edge_table_components
        ORDER BY property_graph_catalog, property_graph_schema, property_graph_name,
                 edge_table_alias, edge_end, ordinal_position
        """,
        """
        SELECT property_graph_catalog, property_graph_schema, property_graph_name, label_name
        FROM information_schema.pg_labels
        ORDER BY property_graph_catalog, property_graph_schema, property_graph_name, label_name
        """,
        """
        SELECT property_graph_catalog, property_graph_schema, property_graph_name,
               label_name, property_name
        FROM information_schema.pg_label_properties
        ORDER BY property_graph_catalog, property_graph_schema, property_graph_name,
                 label_name, property_name
        """,
        """
        SELECT property_graph_catalog, property_graph_schema, property_graph_name,
               property_name, data_type,
               character_maximum_length, character_octet_length,
               character_set_catalog, character_set_schema, character_set_name,
               collation_catalog, collation_schema, collation_name,
               numeric_precision, numeric_precision_radix, numeric_scale,
               datetime_precision, interval_type, interval_precision,
               user_defined_type_catalog, user_defined_type_schema, user_defined_type_name,
               scope_catalog, scope_schema, scope_name, maximum_cardinality, dtd_identifier
        FROM information_schema.pg_property_data_types
        ORDER BY property_graph_catalog, property_graph_schema, property_graph_name, property_name
        """,
    ];

    private readonly BlueTuskDataSource? _dataSource;
    private readonly BlueTuskConnection? _connection;

    public BlueTuskPropertyGraphSchemaInspector(BlueTuskDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <summary>
    /// Creates an inspector over an already-open connection. The connection remains owned by the caller,
    /// allowing discovery of temporary property graphs in the same PostgreSQL session.
    /// </summary>
    public BlueTuskPropertyGraphSchemaInspector(BlueTuskConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public IReadOnlyList<BlueTuskPropertyGraphSchema> Inspect(
        BlueTuskPropertyGraphInspectionOptions? options = null)
    {
        if (_connection is not null)
        {
            return InspectConnection(_connection, options);
        }

        using var connection = _dataSource!.OpenConnection();
        return InspectConnection(connection, options);
    }

    public async Task<IReadOnlyList<BlueTuskPropertyGraphSchema>> InspectAsync(
        BlueTuskPropertyGraphInspectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            return await InspectConnectionAsync(_connection, options, cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await InspectConnectionAsync(connection, options, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<BlueTuskPropertyGraphSchema> InspectConnection(
        BlueTuskConnection connection,
        BlueTuskPropertyGraphInspectionOptions? options)
    {
        EnsureOpen(connection);
        if (connection.ServerCapabilities is not { SupportsSqlPgq: true })
        {
            return Array.Empty<BlueTuskPropertyGraphSchema>();
        }

        using var batch = CreateBatch(connection);
        using var reader = batch.ExecuteReader();
        var catalogue = new PropertyGraphCatalogue();
        var resultIndex = 0;
        do
        {
            while (reader.Read())
            {
                catalogue.Add(resultIndex, reader);
            }

            resultIndex++;
        }
        while (reader.NextResult());

        EnsureComplete(resultIndex);
        return catalogue.Build(options);
    }

    private static async Task<IReadOnlyList<BlueTuskPropertyGraphSchema>> InspectConnectionAsync(
        BlueTuskConnection connection,
        BlueTuskPropertyGraphInspectionOptions? options,
        CancellationToken cancellationToken)
    {
        EnsureOpen(connection);
        if (connection.ServerCapabilities is not { SupportsSqlPgq: true })
        {
            return Array.Empty<BlueTuskPropertyGraphSchema>();
        }

        await using var batch = CreateBatch(connection);
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var catalogue = new PropertyGraphCatalogue();
        var resultIndex = 0;
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                catalogue.Add(resultIndex, reader);
            }

            resultIndex++;
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        EnsureComplete(resultIndex);
        return catalogue.Build(options);
    }

    private static BlueTuskBatch CreateBatch(BlueTuskConnection connection)
    {
        var batch = connection.CreateBatch();
        foreach (var query in CatalogueQueries)
        {
            batch.BatchCommands.Add(query);
        }

        return batch;
    }

    private static void EnsureOpen(BlueTuskConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "A connection-based property-graph inspector requires an open connection.");
        }
    }

    private static void EnsureComplete(int resultSetCount)
    {
        if (resultSetCount != ResultSetCount)
        {
            throw new InvalidOperationException(
                $"Property-graph discovery expected {ResultSetCount} catalogue result sets but received {resultSetCount}.");
        }
    }

    private sealed class PropertyGraphCatalogue
    {
        private readonly List<GraphRow> _graphs = [];
        private readonly List<ElementRow> _elements = [];
        private readonly List<KeyRow> _keys = [];
        private readonly List<ElementLabelRow> _elementLabels = [];
        private readonly List<ElementPropertyRow> _elementProperties = [];
        private readonly List<EdgeComponentRow> _edgeComponents = [];
        private readonly List<LabelRow> _labels = [];
        private readonly List<LabelPropertyRow> _labelProperties = [];
        private readonly List<PropertyTypeRow> _propertyTypes = [];

        public void Add(int resultIndex, DbDataReader reader)
        {
            switch (resultIndex)
            {
                case 0:
                    _graphs.Add(new GraphRow(ReadGraphKey(reader)));
                    break;
                case 1:
                    _elements.Add(new ElementRow(
                        ReadGraphKey(reader),
                        reader.GetString(3),
                        ParseElementKind(reader.GetString(4)),
                        new BlueTuskSchemaObjectName(reader.GetString(5), reader.GetString(6), reader.GetString(7)),
                        GetNullableString(reader, 8)));
                    break;
                case 2:
                    _keys.Add(new KeyRow(
                        ReadGraphKey(reader),
                        reader.GetString(3),
                        new BlueTuskPropertyGraphKeyColumn(reader.GetString(4), reader.GetInt32(5))));
                    break;
                case 3:
                    _elementLabels.Add(new ElementLabelRow(
                        ReadGraphKey(reader), reader.GetString(3), reader.GetString(4)));
                    break;
                case 4:
                    _elementProperties.Add(new ElementPropertyRow(
                        ReadGraphKey(reader),
                        reader.GetString(3),
                        new BlueTuskPropertyGraphElementProperty(reader.GetString(4), reader.GetString(5))));
                    break;
                case 5:
                    _edgeComponents.Add(new EdgeComponentRow(
                        ReadGraphKey(reader),
                        reader.GetString(3),
                        reader.GetString(4),
                        ParseEdgeEnd(reader.GetString(5)),
                        new BlueTuskPropertyGraphEdgeColumnMapping(
                            reader.GetString(6), reader.GetString(7), reader.GetInt32(8))));
                    break;
                case 6:
                    _labels.Add(new LabelRow(ReadGraphKey(reader), reader.GetString(3)));
                    break;
                case 7:
                    _labelProperties.Add(new LabelPropertyRow(
                        ReadGraphKey(reader), reader.GetString(3), reader.GetString(4)));
                    break;
                case 8:
                    _propertyTypes.Add(new PropertyTypeRow(
                        ReadGraphKey(reader),
                        new BlueTuskPropertyGraphPropertyDataType(
                            reader.GetString(3),
                            reader.GetString(4),
                            GetNullableInt32(reader, 5),
                            GetNullableInt32(reader, 6),
                            GetNullableString(reader, 7),
                            GetNullableString(reader, 8),
                            GetNullableString(reader, 9),
                            GetNullableString(reader, 10),
                            GetNullableString(reader, 11),
                            GetNullableString(reader, 12),
                            GetNullableInt32(reader, 13),
                            GetNullableInt32(reader, 14),
                            GetNullableInt32(reader, 15),
                            GetNullableInt32(reader, 16),
                            GetNullableString(reader, 17),
                            GetNullableInt32(reader, 18),
                            GetNullableString(reader, 19),
                            GetNullableString(reader, 20),
                            GetNullableString(reader, 21),
                            GetNullableString(reader, 22),
                            GetNullableString(reader, 23),
                            GetNullableString(reader, 24),
                            GetNullableInt64(reader, 25),
                            reader.GetString(26))));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Property-graph discovery returned an unexpected result set at index {resultIndex}.");
            }
        }

        public System.Collections.ObjectModel.ReadOnlyCollection<BlueTuskPropertyGraphSchema> Build(
            BlueTuskPropertyGraphInspectionOptions? options)
        {
            var graphs = _graphs
                .Where(row => Matches(row.Key, options))
                .Select(row => BuildGraph(row.Key))
                .ToArray();
            return Array.AsReadOnly(graphs);
        }

        private BlueTuskPropertyGraphSchema BuildGraph(GraphKey key)
        {
            var elements = _elements
                .Where(row => row.Key == key)
                .Select(BuildElement)
                .ToArray();
            var labels = _labels
                .Where(row => row.Key == key)
                .Select(row => new BlueTuskPropertyGraphLabel(
                    row.Name,
                    _labelProperties
                        .Where(property => property.Key == key && property.LabelName == row.Name)
                        .Select(property => property.PropertyName)))
                .ToArray();
            var propertyTypes = _propertyTypes
                .Where(row => row.Key == key)
                .Select(row => row.DataType)
                .ToArray();

            return new BlueTuskPropertyGraphSchema(
                new BlueTuskSchemaObjectName(key.Catalog, key.Schema, key.Name),
                elements,
                labels,
                propertyTypes);
        }

        private BlueTuskPropertyGraphElementTable BuildElement(ElementRow row)
        {
            var endpoints = _edgeComponents
                .Where(component => component.Key == row.Key && component.EdgeAlias == row.Alias)
                .GroupBy(component => new { component.End, component.VertexAlias })
                .Select(group => new BlueTuskPropertyGraphEdgeEndpoint(
                    group.Key.End,
                    group.Key.VertexAlias,
                    group.Select(component => component.Mapping)))
                .OrderBy(endpoint => endpoint.End)
                .ToArray();

            return new BlueTuskPropertyGraphElementTable(
                row.Alias,
                row.Kind,
                row.Table,
                row.Definition,
                _keys
                    .Where(key => key.Key == row.Key && key.ElementAlias == row.Alias)
                    .Select(key => key.Column),
                _elementLabels
                    .Where(label => label.Key == row.Key && label.ElementAlias == row.Alias)
                    .Select(label => label.LabelName),
                _elementProperties
                    .Where(property => property.Key == row.Key && property.ElementAlias == row.Alias)
                    .Select(property => property.Property),
                endpoints);
        }

        private static bool Matches(GraphKey key, BlueTuskPropertyGraphInspectionOptions? options) =>
            options is null ||
            (Matches(key.Catalog, options.Catalog) &&
             Matches(key.Schema, options.Schema) &&
             Matches(key.Name, options.Name));

        private static bool Matches(string value, string? filter) =>
            filter is null || string.Equals(value, filter, StringComparison.Ordinal);

        private static GraphKey ReadGraphKey(DbDataReader reader) =>
            new(reader.GetString(0), reader.GetString(1), reader.GetString(2));

        private static string? GetNullableString(DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        private static int? GetNullableInt32(DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

        private static long? GetNullableInt64(DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

        private static BlueTuskPropertyGraphElementKind ParseElementKind(string value) => value switch
        {
            "VERTEX" => BlueTuskPropertyGraphElementKind.Vertex,
            "EDGE" => BlueTuskPropertyGraphElementKind.Edge,
            _ => throw new NotSupportedException($"PostgreSQL returned unknown property-graph element kind '{value}'."),
        };

        private static BlueTuskPropertyGraphEdgeEnd ParseEdgeEnd(string value) => value switch
        {
            "SOURCE" => BlueTuskPropertyGraphEdgeEnd.Source,
            "DESTINATION" => BlueTuskPropertyGraphEdgeEnd.Destination,
            _ => throw new NotSupportedException($"PostgreSQL returned unknown property-graph edge end '{value}'."),
        };

        private sealed record GraphKey(string Catalog, string Schema, string Name);

        private sealed record GraphRow(GraphKey Key);

        private sealed record ElementRow(
            GraphKey Key,
            string Alias,
            BlueTuskPropertyGraphElementKind Kind,
            BlueTuskSchemaObjectName Table,
            string? Definition);

        private sealed record KeyRow(
            GraphKey Key,
            string ElementAlias,
            BlueTuskPropertyGraphKeyColumn Column);

        private sealed record ElementLabelRow(GraphKey Key, string ElementAlias, string LabelName);

        private sealed record ElementPropertyRow(
            GraphKey Key,
            string ElementAlias,
            BlueTuskPropertyGraphElementProperty Property);

        private sealed record EdgeComponentRow(
            GraphKey Key,
            string EdgeAlias,
            string VertexAlias,
            BlueTuskPropertyGraphEdgeEnd End,
            BlueTuskPropertyGraphEdgeColumnMapping Mapping);

        private sealed record LabelRow(GraphKey Key, string Name);

        private sealed record LabelPropertyRow(GraphKey Key, string LabelName, string PropertyName);

        private sealed record PropertyTypeRow(
            GraphKey Key,
            BlueTuskPropertyGraphPropertyDataType DataType);
    }
}
