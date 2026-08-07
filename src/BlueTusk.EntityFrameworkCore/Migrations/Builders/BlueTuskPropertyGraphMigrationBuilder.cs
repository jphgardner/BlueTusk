using BlueTusk.EntityFrameworkCore.Graphs;

namespace BlueTusk.EntityFrameworkCore.Migrations.Builders;

/// <summary>Builds a readable PostgreSQL property-graph migration definition.</summary>
public sealed class BlueTuskPropertyGraphMigrationBuilder
{
    private readonly string _name;
    private readonly string? _schema;
    private readonly List<ElementState> _elements = [];

    internal BlueTuskPropertyGraphMigrationBuilder(string name, string? schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _schema = schema;
    }

    public BlueTuskPropertyGraphMigrationBuilder Vertex(
        string alias,
        string table,
        string? schema,
        Action<BlueTuskPropertyGraphVertexMigrationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var state = Add(alias, table, schema, BlueTuskGraphElementKind.Vertex);
        configure(new BlueTuskPropertyGraphVertexMigrationBuilder(state));
        return this;
    }

    public BlueTuskPropertyGraphMigrationBuilder Edge(
        string alias,
        string table,
        string? schema,
        Action<BlueTuskPropertyGraphEdgeMigrationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var state = Add(alias, table, schema, BlueTuskGraphElementKind.Edge);
        configure(new BlueTuskPropertyGraphEdgeMigrationBuilder(state));
        return this;
    }

    internal BlueTuskPropertyGraphDefinition Build()
    {
        if (_elements.Count == 0)
        {
            throw new InvalidOperationException(
                $"Property graph '{_name}' must contain at least one vertex or edge table.");
        }

        var vertexAliases = _elements
            .Where(element => element.Kind == BlueTuskGraphElementKind.Vertex)
            .Select(element => element.Alias)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var edge in _elements.Where(element => element.Kind == BlueTuskGraphElementKind.Edge))
        {
            edge.ValidateEndpoint(edge.Source, "source", vertexAliases);
            edge.ValidateEndpoint(edge.Destination, "destination", vertexAliases);
        }

        return new BlueTuskPropertyGraphDefinition(
            _name,
            _schema,
            _elements.Select(element => element.Build()).ToArray());
    }

    private ElementState Add(
        string alias,
        string table,
        string? schema,
        BlueTuskGraphElementKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (_elements.Any(element => string.Equals(element.Alias, alias, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Property graph '{_name}' already contains alias '{alias}'.");
        }

        var state = new ElementState(alias, kind, table, schema);
        _elements.Add(state);
        return state;
    }

    internal sealed class ElementState(
        string alias,
        BlueTuskGraphElementKind kind,
        string table,
        string? schema)
    {
        private readonly List<string> _keys = [];
        private readonly List<BlueTuskGraphLabelDefinition> _labels = [];

        public string Alias { get; } = alias;

        public BlueTuskGraphElementKind Kind { get; } = kind;

        public string Table { get; } = table;

        public string? Schema { get; } = schema;

        public BlueTuskGraphEndpointDefinition? Source { get; set; }

        public BlueTuskGraphEndpointDefinition? Destination { get; set; }

        public void SetKeys(IEnumerable<string> columns)
        {
            var materialized = RequiredNames(columns, "key column");
            _keys.Clear();
            _keys.AddRange(materialized);
        }

        public void AddLabel(string name, Action<BlueTuskPropertyGraphLabelMigrationBuilder>? configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (_labels.Any(label => string.Equals(label.Name, name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Element table '{Alias}' already contains label '{name}'.");
            }

            var builder = new BlueTuskPropertyGraphLabelMigrationBuilder();
            configure?.Invoke(builder);
            _labels.Add(new BlueTuskGraphLabelDefinition(name, builder.Build()));
        }

        public BlueTuskGraphElementTableDefinition Build() => new(
            Alias,
            Kind,
            Table,
            Schema,
            _keys.ToArray(),
            _labels.ToArray(),
            Source,
            Destination);

        public void ValidateEndpoint(
            BlueTuskGraphEndpointDefinition? endpoint,
            string endpointName,
            IReadOnlySet<string> vertexAliases)
        {
            if (endpoint is null)
            {
                throw new InvalidOperationException(
                    $"Edge table '{Alias}' must configure its {endpointName} endpoint.");
            }
            if (!vertexAliases.Contains(endpoint.VertexTableAlias))
            {
                throw new InvalidOperationException(
                    $"Edge table '{Alias}' references unknown vertex alias '{endpoint.VertexTableAlias}'.");
            }
            if (endpoint.EdgeKeyColumns.Count == 0 ||
                endpoint.EdgeKeyColumns.Count != endpoint.VertexKeyColumns.Count)
            {
                throw new InvalidOperationException(
                    $"Edge table '{Alias}' {endpointName} keys must be non-empty and have matching counts.");
            }
        }

        public static string[] RequiredNames(IEnumerable<string> values, string description)
        {
            ArgumentNullException.ThrowIfNull(values);
            var result = values.ToArray();
            if (result.Length == 0 || result.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException($"At least one non-empty {description} is required.", nameof(values));
            }
            if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
            {
                throw new ArgumentException($"Duplicate {description}s are not allowed.", nameof(values));
            }
            return result;
        }
    }
}

public sealed class BlueTuskPropertyGraphVertexMigrationBuilder
{
    private readonly BlueTuskPropertyGraphMigrationBuilder.ElementState _state;

    internal BlueTuskPropertyGraphVertexMigrationBuilder(
        BlueTuskPropertyGraphMigrationBuilder.ElementState state) => _state = state;

    public BlueTuskPropertyGraphVertexMigrationBuilder HasKey(params string[] columns)
    {
        _state.SetKeys(columns);
        return this;
    }

    public BlueTuskPropertyGraphVertexMigrationBuilder HasLabel(
        string name,
        Action<BlueTuskPropertyGraphLabelMigrationBuilder>? configure = null)
    {
        _state.AddLabel(name, configure);
        return this;
    }
}

public sealed class BlueTuskPropertyGraphEdgeMigrationBuilder
{
    private readonly BlueTuskPropertyGraphMigrationBuilder.ElementState _state;

    internal BlueTuskPropertyGraphEdgeMigrationBuilder(
        BlueTuskPropertyGraphMigrationBuilder.ElementState state) => _state = state;

    public BlueTuskPropertyGraphEdgeMigrationBuilder HasKey(params string[] columns)
    {
        _state.SetKeys(columns);
        return this;
    }

    public BlueTuskPropertyGraphEdgeMigrationBuilder HasLabel(
        string name,
        Action<BlueTuskPropertyGraphLabelMigrationBuilder>? configure = null)
    {
        _state.AddLabel(name, configure);
        return this;
    }

    public BlueTuskPropertyGraphEdgeMigrationBuilder HasSource(
        string vertexAlias,
        string[] edgeKeyColumns,
        string[] vertexKeyColumns)
    {
        _state.Source = Endpoint(vertexAlias, edgeKeyColumns, vertexKeyColumns);
        return this;
    }

    public BlueTuskPropertyGraphEdgeMigrationBuilder HasDestination(
        string vertexAlias,
        string[] edgeKeyColumns,
        string[] vertexKeyColumns)
    {
        _state.Destination = Endpoint(vertexAlias, edgeKeyColumns, vertexKeyColumns);
        return this;
    }

    private static BlueTuskGraphEndpointDefinition Endpoint(
        string vertexAlias,
        IEnumerable<string> edgeKeyColumns,
        IEnumerable<string> vertexKeyColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexAlias);
        return new BlueTuskGraphEndpointDefinition(
            vertexAlias,
            BlueTuskPropertyGraphMigrationBuilder.ElementState.RequiredNames(
                edgeKeyColumns, "edge endpoint column"),
            BlueTuskPropertyGraphMigrationBuilder.ElementState.RequiredNames(
                vertexKeyColumns, "vertex endpoint column"));
    }
}

public sealed class BlueTuskPropertyGraphLabelMigrationBuilder
{
    private readonly List<BlueTuskGraphPropertyDefinition> _properties = [];

    public BlueTuskPropertyGraphLabelMigrationBuilder Property(
        string column,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        Add(new BlueTuskGraphPropertyDefinition(column, name ?? column, IsColumn: true));
        return this;
    }

    public BlueTuskPropertyGraphLabelMigrationBuilder Expression(string expression, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Add(new BlueTuskGraphPropertyDefinition(expression, name, IsColumn: false));
        return this;
    }

    internal IReadOnlyList<BlueTuskGraphPropertyDefinition> Build() => _properties.ToArray();

    private void Add(BlueTuskGraphPropertyDefinition property)
    {
        if (_properties.Any(item => string.Equals(item.Name, property.Name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Label already contains property name '{property.Name}'.");
        }
        _properties.Add(property);
    }
}
