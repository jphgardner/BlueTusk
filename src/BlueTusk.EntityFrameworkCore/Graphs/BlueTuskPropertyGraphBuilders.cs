using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Graphs;

/// <summary>Builds one PostgreSQL property-graph definition in an EF model.</summary>
public sealed class BlueTuskPropertyGraphBuilder
{
    private readonly ModelBuilder _modelBuilder;
    private readonly List<ElementState> _elements = [];

    internal BlueTuskPropertyGraphBuilder(ModelBuilder modelBuilder, string name, string? schema)
    {
        _modelBuilder = modelBuilder;
        Name = name;
        Schema = schema;
    }

    internal string Name { get; }

    internal string? Schema { get; }

    public BlueTuskPropertyGraphBuilder Vertex<TEntity>(
        string alias,
        Action<BlueTuskPropertyGraphVertexBuilder<TEntity>> configure)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(configure);
        var state = AddElement<TEntity>(alias, BlueTuskGraphElementKind.Vertex);
        configure(new BlueTuskPropertyGraphVertexBuilder<TEntity>(state));
        return this;
    }

    public BlueTuskPropertyGraphBuilder Edge<TEntity>(
        string alias,
        Action<BlueTuskPropertyGraphEdgeBuilder<TEntity>> configure)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(configure);
        var state = AddElement<TEntity>(alias, BlueTuskGraphElementKind.Edge);
        configure(new BlueTuskPropertyGraphEdgeBuilder<TEntity>(state));
        return this;
    }

    internal BlueTuskPropertyGraphDefinition Build()
    {
        foreach (var edge in _elements.Where(element => element.Kind == BlueTuskGraphElementKind.Edge))
        {
            edge.ResolveEndpoints(_elements);
        }

        return new BlueTuskPropertyGraphDefinition(
            Name,
            Schema,
            _elements.Select(element => element.Build()).ToArray());
    }

    private ElementState AddElement<TEntity>(string alias, BlueTuskGraphElementKind kind)
        where TEntity : class
    {
        if (_elements.Any(element => string.Equals(element.Alias, alias, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Property graph '{Name}' already contains an element table with alias '{alias}'.");
        }

        var entityType = _modelBuilder.Entity<TEntity>().Metadata;
        var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)
            ?? throw new InvalidOperationException(
                $"Entity type '{entityType.DisplayName()}' is not mapped to a table.");
        var state = new ElementState(alias, kind, entityType, storeObject);
        _elements.Add(state);
        return state;
    }

    internal sealed class ElementState
    {
        private readonly IMutableEntityType _entityType;
        private readonly StoreObjectIdentifier _storeObject;
        private readonly List<string> _keyColumns = [];
        private readonly List<string> _labels = [];
        private readonly List<BlueTuskGraphPropertyDefinition> _properties = [];
        private PendingEndpoint? _source;
        private PendingEndpoint? _destination;
        private BlueTuskGraphEndpointDefinition? _resolvedSource;
        private BlueTuskGraphEndpointDefinition? _resolvedDestination;

        public ElementState(
            string alias,
            BlueTuskGraphElementKind kind,
            IMutableEntityType entityType,
            StoreObjectIdentifier storeObject)
        {
            Alias = alias;
            Kind = kind;
            _entityType = entityType;
            _storeObject = storeObject;
        }

        public string Alias { get; }

        public BlueTuskGraphElementKind Kind { get; }

        public Type ClrType => _entityType.ClrType;

        public void AddLabel(string label)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(label);
            if (!_labels.Contains(label, StringComparer.Ordinal))
            {
                _labels.Add(label);
            }
        }

        public void SetKey(LambdaExpression expression)
        {
            _keyColumns.Clear();
            _keyColumns.AddRange(GetColumns(expression));
        }

        public void SetProperties(LambdaExpression expression)
        {
            _properties.Clear();
            foreach (var property in GetProperties(expression))
            {
                var column = property.GetColumnName(_storeObject)
                    ?? throw new InvalidOperationException(
                        $"Property '{property.Name}' is not mapped to table '{_storeObject}'.");
                _properties.Add(new BlueTuskGraphPropertyDefinition(column, property.Name, IsColumn: true));
            }
        }

        public void SetSource(
            Type vertexType,
            string? vertexAlias,
            LambdaExpression edgeKey,
            LambdaExpression vertexKey)
        {
            EnsureEdge();
            _source = new PendingEndpoint(
                vertexType,
                vertexAlias,
                GetColumns(edgeKey),
                GetPropertyNames(vertexKey));
        }

        public void SetDestination(
            Type vertexType,
            string? vertexAlias,
            LambdaExpression edgeKey,
            LambdaExpression vertexKey)
        {
            EnsureEdge();
            _destination = new PendingEndpoint(
                vertexType,
                vertexAlias,
                GetColumns(edgeKey),
                GetPropertyNames(vertexKey));
        }

        public void ResolveEndpoints(IReadOnlyList<ElementState> elements)
        {
            if (_source is null || _destination is null)
            {
                throw new InvalidOperationException(
                    $"Edge table '{Alias}' must configure both source and destination endpoints.");
            }

            _resolvedSource = ResolveEndpoint(_source, elements);
            _resolvedDestination = ResolveEndpoint(_destination, elements);
        }

        public BlueTuskGraphElementTableDefinition Build()
        {
            var labels = _labels
                .Select(label => new BlueTuskGraphLabelDefinition(label, _properties.ToArray()))
                .ToArray();
            return new BlueTuskGraphElementTableDefinition(
                Alias,
                Kind,
                _storeObject.Name,
                _storeObject.Schema,
                _keyColumns.ToArray(),
                labels,
                _resolvedSource,
                _resolvedDestination);
        }

        private BlueTuskGraphEndpointDefinition ResolveEndpoint(
            PendingEndpoint endpoint,
            IReadOnlyList<ElementState> elements)
        {
            var candidates = elements
                .Where(element =>
                    element.Kind == BlueTuskGraphElementKind.Vertex &&
                    element.ClrType == endpoint.VertexType &&
                    (endpoint.VertexAlias is null ||
                     string.Equals(element.Alias, endpoint.VertexAlias, StringComparison.Ordinal)))
                .ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Edge table '{Alias}' endpoint for entity type '{endpoint.VertexType.Name}' " +
                    "must resolve to exactly one vertex alias.");
            }

            var vertex = candidates[0];
            var vertexColumns = endpoint.VertexPropertyNames
                .Select(propertyName =>
                {
                    var property = vertex._entityType.FindProperty(propertyName)
                        ?? throw new InvalidOperationException(
                            $"Entity type '{vertex._entityType.DisplayName()}' has no property '{propertyName}'.");
                    return property.GetColumnName(vertex._storeObject)
                        ?? throw new InvalidOperationException(
                            $"Property '{property.Name}' is not mapped to table '{vertex._storeObject}'.");
                })
                .ToArray();
            if (endpoint.EdgeColumns.Count != vertexColumns.Length)
            {
                throw new InvalidOperationException(
                    $"Edge table '{Alias}' endpoint key column counts do not match.");
            }

            return new BlueTuskGraphEndpointDefinition(
                vertex.Alias,
                endpoint.EdgeColumns,
                vertexColumns);
        }

        private string[] GetColumns(LambdaExpression expression) =>
            GetProperties(expression)
                .Select(property => property.GetColumnName(_storeObject)
                    ?? throw new InvalidOperationException(
                        $"Property '{property.Name}' is not mapped to table '{_storeObject}'."))
                .ToArray();

        private static string[] GetPropertyNames(LambdaExpression expression) =>
            GetMemberNames(expression.Body);

        private IMutableProperty[] GetProperties(LambdaExpression expression) =>
            GetMemberNames(expression.Body)
                .Select(name => _entityType.FindProperty(name)
                    ?? throw new InvalidOperationException(
                        $"Entity type '{_entityType.DisplayName()}' has no property '{name}'."))
                .ToArray();

        private static string[] GetMemberNames(Expression expression)
        {
            expression = UnwrapConvert(expression);
            if (expression is MemberExpression member && member.Expression is ParameterExpression)
            {
                return [member.Member.Name];
            }

            if (expression is NewExpression creation)
            {
                return creation.Arguments.Select(argument =>
                {
                    var item = UnwrapConvert(argument);
                    return item is MemberExpression itemMember && itemMember.Expression is ParameterExpression
                        ? itemMember.Member.Name
                        : throw new ArgumentException(
                            "Property-graph selectors must contain direct mapped-property accesses.");
                }).ToArray();
            }

            throw new ArgumentException(
                "Property-graph selectors must be a mapped property or an anonymous object of mapped properties.");
        }

        private static Expression UnwrapConvert(Expression expression) =>
            expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
                ? unary.Operand
                : expression;

        private void EnsureEdge()
        {
            if (Kind != BlueTuskGraphElementKind.Edge)
            {
                throw new InvalidOperationException("Only edge tables can configure graph endpoints.");
            }
        }

        private sealed record PendingEndpoint(
            Type VertexType,
            string? VertexAlias,
            IReadOnlyList<string> EdgeColumns,
            IReadOnlyList<string> VertexPropertyNames);
    }
}

/// <summary>Configures a vertex element table.</summary>
public sealed class BlueTuskPropertyGraphVertexBuilder<TEntity>
    where TEntity : class
{
    private readonly BlueTuskPropertyGraphBuilder.ElementState _state;

    internal BlueTuskPropertyGraphVertexBuilder(BlueTuskPropertyGraphBuilder.ElementState state)
    {
        _state = state;
    }

    public BlueTuskPropertyGraphVertexBuilder<TEntity> HasLabel(string label)
    {
        _state.AddLabel(label);
        return this;
    }

    public BlueTuskPropertyGraphVertexBuilder<TEntity> HasKey(
        Expression<Func<TEntity, object?>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _state.SetKey(key);
        return this;
    }

    public BlueTuskPropertyGraphVertexBuilder<TEntity> Properties(
        Expression<Func<TEntity, object?>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        _state.SetProperties(properties);
        return this;
    }
}

/// <summary>Configures an edge element table.</summary>
public sealed class BlueTuskPropertyGraphEdgeBuilder<TEntity>
    where TEntity : class
{
    private readonly BlueTuskPropertyGraphBuilder.ElementState _state;

    internal BlueTuskPropertyGraphEdgeBuilder(BlueTuskPropertyGraphBuilder.ElementState state)
    {
        _state = state;
    }

    public BlueTuskPropertyGraphEdgeBuilder<TEntity> HasLabel(string label)
    {
        _state.AddLabel(label);
        return this;
    }

    public BlueTuskPropertyGraphEdgeBuilder<TEntity> HasKey(
        Expression<Func<TEntity, object?>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _state.SetKey(key);
        return this;
    }

    public BlueTuskPropertyGraphEdgeBuilder<TEntity> Properties(
        Expression<Func<TEntity, object?>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        _state.SetProperties(properties);
        return this;
    }

    public BlueTuskPropertyGraphEdgeBuilder<TEntity> HasSource<TVertex>(
        Expression<Func<TEntity, object?>> edgeKey,
        Expression<Func<TVertex, object?>> vertexKey,
        string? vertexAlias = null)
        where TVertex : class
    {
        ArgumentNullException.ThrowIfNull(edgeKey);
        ArgumentNullException.ThrowIfNull(vertexKey);
        _state.SetSource(typeof(TVertex), vertexAlias, edgeKey, vertexKey);
        return this;
    }

    public BlueTuskPropertyGraphEdgeBuilder<TEntity> HasDestination<TVertex>(
        Expression<Func<TEntity, object?>> edgeKey,
        Expression<Func<TVertex, object?>> vertexKey,
        string? vertexAlias = null)
        where TVertex : class
    {
        ArgumentNullException.ThrowIfNull(edgeKey);
        ArgumentNullException.ThrowIfNull(vertexKey);
        _state.SetDestination(typeof(TVertex), vertexAlias, edgeKey, vertexKey);
        return this;
    }
}
