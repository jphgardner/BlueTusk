using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.EntityFrameworkCore.Graphs;

/// <summary>Raised when a typed graph construct cannot be translated safely to SQL/PGQ.</summary>
public sealed class BlueTuskGraphTranslationException : InvalidOperationException
{
    public BlueTuskGraphTranslationException(string message)
        : base(message)
    {
    }
}

/// <summary>Specifies the direction in which a pattern traverses an edge.</summary>
public enum BlueTuskGraphEdgeDirection
{
    Outgoing,
    Incoming,
    Undirected,
}

/// <summary>An immutable SQL/PGQ label expression with OR semantics.</summary>
public sealed class BlueTuskGraphLabelExpression
{
    private readonly IReadOnlyList<string> _labels;

    private BlueTuskGraphLabelExpression(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var resolved = labels.ToArray();
        if (resolved.Length is 0 or > 8 || resolved.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A graph label expression must contain between one and eight non-empty labels.",
                nameof(labels));
        }

        if (resolved.Distinct(StringComparer.Ordinal).Count() != resolved.Length)
        {
            throw new ArgumentException(
                "A graph label expression cannot contain duplicate labels.",
                nameof(labels));
        }

        _labels = Array.AsReadOnly(resolved);
    }

    public IReadOnlyList<string> Labels => _labels;

    public static BlueTuskGraphLabelExpression AnyOf(params string[] labels) =>
        new(labels);
}

/// <summary>A typed vertex in a linear SQL/PGQ graph pattern.</summary>
public sealed record BlueTuskGraphVertexPattern(
    Type EntityType,
    string Variable,
    string? ElementTableAlias,
    LambdaExpression? Predicate)
{
    public BlueTuskGraphLabelExpression? LabelExpression { get; init; }
}

/// <summary>A typed edge in a linear SQL/PGQ graph pattern.</summary>
public sealed record BlueTuskGraphEdgePattern(
    Type EntityType,
    string Variable,
    string? ElementTableAlias,
    BlueTuskGraphEdgeDirection Direction)
{
    public BlueTuskGraphLabelExpression? LabelExpression { get; init; }

    public int MinimumHops { get; init; } = 1;

    public int MaximumHops { get; init; } = 1;
}

/// <summary>Builds a safe, linear, typed graph pattern.</summary>
public sealed class BlueTuskGraphPatternBuilder
{
    private readonly List<object> _steps = [];

    internal IReadOnlyList<object> Steps => _steps;

    public BlueTuskGraphPatternBuilder Vertex<TEntity>(
        string variable,
        Expression<Func<TEntity, bool>>? predicate = null)
        where TEntity : class =>
        AddVertex(variable, elementTableAlias: null, predicate);

    public BlueTuskGraphPatternBuilder Vertex<TEntity>(
        string variable,
        string elementTableAlias,
        Expression<Func<TEntity, bool>>? predicate = null)
        where TEntity : class =>
        AddVertex(variable, elementTableAlias, predicate);

    private BlueTuskGraphPatternBuilder AddVertex<TEntity>(
        string variable,
        string? elementTableAlias,
        Expression<Func<TEntity, bool>>? predicate)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        if (elementTableAlias is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(elementTableAlias);
        }

        if (_steps.Count % 2 != 0)
        {
            throw new BlueTuskGraphTranslationException(
                "A graph vertex must be preceded by an edge or start the pattern.");
        }

        EnsureVariableAvailable(variable);
        _steps.Add(new BlueTuskGraphVertexPattern(
            typeof(TEntity), variable, elementTableAlias, predicate));
        return this;
    }

    public BlueTuskGraphPatternBuilder Outgoing<TEdge>(
        string variable,
        string? elementTableAlias = null)
        where TEdge : class =>
        Edge<TEdge>(variable, elementTableAlias, BlueTuskGraphEdgeDirection.Outgoing, 1, 1);

    public BlueTuskGraphPatternBuilder OutgoingPath<TEdge>(
        string variable,
        int minimumHops,
        int maximumHops,
        string? elementTableAlias = null)
        where TEdge : class =>
        Edge<TEdge>(
            variable,
            elementTableAlias,
            BlueTuskGraphEdgeDirection.Outgoing,
            minimumHops,
            maximumHops);

    public BlueTuskGraphPatternBuilder Incoming<TEdge>(
        string variable,
        string? elementTableAlias = null)
        where TEdge : class =>
        Edge<TEdge>(variable, elementTableAlias, BlueTuskGraphEdgeDirection.Incoming, 1, 1);

    public BlueTuskGraphPatternBuilder IncomingPath<TEdge>(
        string variable,
        int minimumHops,
        int maximumHops,
        string? elementTableAlias = null)
        where TEdge : class =>
        Edge<TEdge>(
            variable,
            elementTableAlias,
            BlueTuskGraphEdgeDirection.Incoming,
            minimumHops,
            maximumHops);

    public BlueTuskGraphPatternBuilder Undirected<TEdge>(
        string variable,
        string? elementTableAlias = null)
        where TEdge : class =>
        Edge<TEdge>(variable, elementTableAlias, BlueTuskGraphEdgeDirection.Undirected, 1, 1);

    public BlueTuskGraphPatternBuilder UndirectedPath<TEdge>(
        string variable,
        int minimumHops,
        int maximumHops,
        string? elementTableAlias = null)
        where TEdge : class =>
        Edge<TEdge>(
            variable,
            elementTableAlias,
            BlueTuskGraphEdgeDirection.Undirected,
            minimumHops,
            maximumHops);

    private BlueTuskGraphPatternBuilder Edge<TEdge>(
        string variable,
        string? elementTableAlias,
        BlueTuskGraphEdgeDirection direction,
        int minimumHops,
        int maximumHops)
        where TEdge : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        if (elementTableAlias is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(elementTableAlias);
        }

        if (_steps.Count == 0 || _steps.Count % 2 == 0)
        {
            throw new BlueTuskGraphTranslationException(
                "A graph edge must follow a vertex and be followed by another vertex.");
        }

        if (minimumHops <= 0 || maximumHops < minimumHops || maximumHops > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumHops),
                "A bounded graph path must use 1 <= minimumHops <= maximumHops <= 8.");
        }

        EnsureVariableAvailable(variable);
        _steps.Add(new BlueTuskGraphEdgePattern(
            typeof(TEdge), variable, elementTableAlias, direction)
        {
            MinimumHops = minimumHops,
            MaximumHops = maximumHops,
        });
        return this;
    }

    /// <summary>Applies an OR label expression to the most recently added vertex or edge.</summary>
    public BlueTuskGraphPatternBuilder LabelsAnyOf(params string[] labels)
    {
        if (_steps.Count == 0)
        {
            throw new BlueTuskGraphTranslationException(
                "A label expression must follow a graph vertex or edge.");
        }

        var expression = BlueTuskGraphLabelExpression.AnyOf(labels);
        _steps[^1] = _steps[^1] switch
        {
            BlueTuskGraphVertexPattern vertex => vertex with { LabelExpression = expression },
            BlueTuskGraphEdgePattern edge => edge with { LabelExpression = expression },
            _ => throw new BlueTuskGraphTranslationException(
                "A label expression must follow a graph vertex or edge."),
        };
        return this;
    }

    private void EnsureVariableAvailable(string variable)
    {
        if (variable.StartsWith("__bluetusk_", StringComparison.Ordinal))
        {
            throw new BlueTuskGraphTranslationException(
                "Graph variables beginning with '__bluetusk_' are reserved for bounded path expansion.");
        }

        if (_steps.Any(step => step switch
            {
                BlueTuskGraphVertexPattern vertex => vertex.Variable == variable,
                BlueTuskGraphEdgePattern edge => edge.Variable == variable,
                _ => false,
            }))
        {
            throw new BlueTuskGraphTranslationException(
                $"Graph variable '{variable}' is already used in this pattern.");
        }
    }
}

/// <summary>Builds typed graph result columns for an unmapped result type.</summary>
public sealed class BlueTuskGraphProjectionBuilder<TResult>
    where TResult : class
{
    private readonly List<BlueTuskGraphProjection> _projections = [];

    internal IReadOnlyList<BlueTuskGraphProjection> Projections => _projections;

    public BlueTuskGraphProjectionBuilder<TResult> Property<TElement, TValue>(
        string variable,
        Expression<Func<TElement, TValue>> graphProperty,
        Expression<Func<TResult, TValue>> resultProperty)
        where TElement : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        ArgumentNullException.ThrowIfNull(graphProperty);
        ArgumentNullException.ThrowIfNull(resultProperty);
        var sourceName = GetMemberName(graphProperty.Body, "graph property");
        var resultName = GetMemberName(resultProperty.Body, "result property");
        if (_projections.Any(projection =>
            string.Equals(projection.ResultProperty, resultName, StringComparison.Ordinal)))
        {
            throw new BlueTuskGraphTranslationException(
                $"Result property '{resultName}' is projected more than once.");
        }

        _projections.Add(new BlueTuskGraphProjection(
            typeof(TElement), variable, sourceName, resultName));
        return this;
    }

    private static string GetMemberName(Expression expression, string description)
    {
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        return expression is MemberExpression { Expression: ParameterExpression } member
            ? member.Member.Name
            : throw new BlueTuskGraphTranslationException(
                $"A {description} selector must be a direct property access.");
    }
}

internal sealed record BlueTuskGraphProjection(
    Type ElementType,
    string Variable,
    string GraphProperty,
    string ResultProperty);

/// <summary>A configured typed graph match ready for projection.</summary>
public sealed class BlueTuskGraphMatch
{
    private readonly DbContext _context;
    private readonly BlueTuskPropertyGraphDefinition _graph;
    private readonly BlueTuskGraphPatternBuilder _pattern;

    internal BlueTuskGraphMatch(
        DbContext context,
        BlueTuskPropertyGraphDefinition graph,
        BlueTuskGraphPatternBuilder pattern)
    {
        _context = context;
        _graph = graph;
        _pattern = pattern;
    }

    /// <summary>
    /// Projects graph properties into an unmapped result type and returns a composable EF query.
    /// </summary>
    public IQueryable<TResult> Select<TResult>(
        Action<BlueTuskGraphProjectionBuilder<TResult>> configure)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var projection = new BlueTuskGraphProjectionBuilder<TResult>();
        configure(projection);
        var translation = BlueTuskGraphSqlTranslator.Translate(
            _context, _graph, _pattern.Steps, projection.Projections, typeof(TResult));
        BlueTuskGraphQueryCapture.Record(_context, translation.ImpactPlan);
        return _context.Model.FindEntityType(typeof(TResult)) is null
            ? _context.Database.SqlQueryRaw<TResult>(translation.Sql, translation.Parameters)
            : _context.Set<TResult>().FromSqlRaw(translation.Sql, translation.Parameters);
    }
}

/// <summary>A typed EF query root for one property graph.</summary>
public sealed class BlueTuskGraphQueryRoot
{
    private readonly DbContext _context;
    private readonly BlueTuskPropertyGraphDefinition _graph;

    internal BlueTuskGraphQueryRoot(DbContext context, BlueTuskPropertyGraphDefinition graph)
    {
        _context = context;
        _graph = graph;
    }

    public BlueTuskGraphMatch Match(
        Func<BlueTuskGraphPatternBuilder, BlueTuskGraphPatternBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var pattern = configure(new BlueTuskGraphPatternBuilder())
            ?? throw new BlueTuskGraphTranslationException("The graph pattern builder cannot be null.");
        if (pattern.Steps.Count == 0 || pattern.Steps.Count % 2 == 0)
        {
            throw new BlueTuskGraphTranslationException(
                "A graph pattern must contain a vertex or end with a vertex.");
        }

        return new BlueTuskGraphMatch(_context, _graph, pattern);
    }
}
