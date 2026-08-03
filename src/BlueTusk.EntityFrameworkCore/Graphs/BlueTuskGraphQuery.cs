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
}

/// <summary>A typed vertex in a linear SQL/PGQ graph pattern.</summary>
public sealed record BlueTuskGraphVertexPattern(
    Type EntityType,
    string Variable,
    string? ElementTableAlias,
    LambdaExpression? Predicate);

/// <summary>A typed edge in a linear SQL/PGQ graph pattern.</summary>
public sealed record BlueTuskGraphEdgePattern(
    Type EntityType,
    string Variable,
    string? ElementTableAlias,
    BlueTuskGraphEdgeDirection Direction);

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
        Edge<TEdge>(variable, elementTableAlias, BlueTuskGraphEdgeDirection.Outgoing);

    public BlueTuskGraphPatternBuilder Incoming<TEdge>(
        string variable,
        string? elementTableAlias = null)
        where TEdge : class =>
        Edge<TEdge>(variable, elementTableAlias, BlueTuskGraphEdgeDirection.Incoming);

    private BlueTuskGraphPatternBuilder Edge<TEdge>(
        string variable,
        string? elementTableAlias,
        BlueTuskGraphEdgeDirection direction)
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

        EnsureVariableAvailable(variable);
        _steps.Add(new BlueTuskGraphEdgePattern(
            typeof(TEdge), variable, elementTableAlias, direction));
        return this;
    }

    private void EnsureVariableAvailable(string variable)
    {
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
