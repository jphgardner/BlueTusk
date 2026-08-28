using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.Live;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.ContinuousGraph;

[Flags]
public enum ContinuousGraphMaintenanceCapabilities
{
    None = 0,
    AuthoritativeRepair = 1,
    AuthoritativeDelta = 2,
    TrustedCdcProjection = 4,
}

/// <summary>Immutable compiler evidence used to classify graph changes safely.</summary>
public sealed class ContinuousGraphImpactPlan
{
    private readonly ReadOnlyCollection<string> _patternElements;
    private readonly ReadOnlyCollection<string> _projections;

    internal ContinuousGraphImpactPlan(
        string graphName,
        string? graphSchema,
        string fingerprint,
        string resultKeyProperty,
        string? resultKeyElementAlias,
        string? resultKeyColumn,
        IEnumerable<string> patternElements,
        IEnumerable<string> projections,
        string canonicalQuery)
    {
        GraphName = graphName;
        GraphSchema = graphSchema;
        Fingerprint = fingerprint;
        ResultKeyProperty = resultKeyProperty;
        ResultKeyElementAlias = resultKeyElementAlias;
        ResultKeyColumn = resultKeyColumn;
        _patternElements = Array.AsReadOnly(patternElements.ToArray());
        _projections = Array.AsReadOnly(projections.ToArray());
        CanonicalQuery = canonicalQuery;
    }

    public string GraphName { get; }

    public string? GraphSchema { get; }

    public string Fingerprint { get; }

    public string ResultKeyProperty { get; }

    public string? ResultKeyElementAlias { get; }

    public string? ResultKeyColumn { get; }

    public IReadOnlyList<string> PatternElements => _patternElements;

    public IReadOnlyList<string> Projections => _projections;

    public string CanonicalQuery { get; }
}

/// <summary>Raised when a registered continuous graph query is invalid or cannot be translated.</summary>
public class ContinuousGraphQueryRegistrationException : Exception
{
    public ContinuousGraphQueryRegistrationException(string message)
        : base(message)
    {
    }

    public ContinuousGraphQueryRegistrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when the configured database cannot execute SQL/PGQ graph queries.</summary>
public sealed class ContinuousGraphCapabilityException : ContinuousGraphQueryRegistrationException
{
    public ContinuousGraphCapabilityException(string message)
        : base(message)
    {
    }
}

/// <summary>Validates that a registration context is connected to a SQL/PGQ-capable PostgreSQL server.</summary>
public interface IContinuousGraphCapabilityProbe
{
    ValueTask EnsureSupportedAsync(
        DbContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Uses the BlueTusk connection capability handshake to require PostgreSQL 19 SQL/PGQ support.</summary>
public sealed class PostgreSql19ContinuousGraphCapabilityProbe : IContinuousGraphCapabilityProbe
{
    public async ValueTask EnsureSupportedAsync(
        DbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State is not ConnectionState.Open;
        try
        {
            if (openedHere)
            {
                await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            if (connection is not BlueTuskConnection { SupportsSqlPgq: true })
            {
                throw new ContinuousGraphCapabilityException(
                    "Continuous Graph requires PostgreSQL 19 or later with SQL/PGQ support reported by BlueTusk.");
            }
        }
        finally
        {
            if (openedHere && connection.State is ConnectionState.Open)
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>A trusted, bounded SQL/PGQ query registration.</summary>
public sealed class ContinuousGraphQueryDefinition<TContext, TResult, TKey>
    where TContext : DbContext
    where TResult : class
    where TKey : notnull
{
    private readonly ReadOnlyCollection<string> _elementTableAliases;
    private readonly ReadOnlyCollection<LiveQueryParameter> _parameters;

    public ContinuousGraphQueryDefinition(
        string name,
        string databaseIdentity,
        string version,
        string graphName,
        string? graphSchema,
        IEnumerable<string> elementTableAliases,
        IEnumerable<LiveQueryParameter> parameters,
        IReadOnlyDictionary<string, object?> validationArguments,
        int maximumResultCount,
        Func<TContext, LiveQueryArguments, IQueryable<TResult>> queryFactory,
        Expression<Func<TResult, TKey>> keySelector,
        IEqualityComparer<TResult> rowComparer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphName);
        ArgumentNullException.ThrowIfNull(elementTableAliases);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(validationArguments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResultCount);
        ArgumentNullException.ThrowIfNull(queryFactory);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(rowComparer);

        var aliases = elementTableAliases.ToArray();
        if (aliases.Length == 0 || aliases.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A continuous graph query must declare at least one non-empty graph element alias.",
                nameof(elementTableAliases));
        }

        if (aliases.Distinct(StringComparer.Ordinal).Count() != aliases.Length)
        {
            throw new ArgumentException(
                "Continuous graph element aliases must be unique.",
                nameof(elementTableAliases));
        }

        var parameterArray = parameters.ToArray();
        Name = name;
        DatabaseIdentity = databaseIdentity;
        Version = version;
        GraphName = graphName;
        GraphSchema = graphSchema;
        _elementTableAliases = Array.AsReadOnly(aliases);
        _parameters = Array.AsReadOnly(parameterArray);
        ValidationArguments = LiveQueryArguments.Create(parameterArray, validationArguments);
        MaximumResultCount = maximumResultCount;
        QueryFactory = queryFactory;
        KeySelector = keySelector;
        RowComparer = rowComparer;
    }

    public string Name { get; }

    public string DatabaseIdentity { get; }

    public string Version { get; }

    public string GraphName { get; }

    public string? GraphSchema { get; }

    public IReadOnlyList<string> ElementTableAliases => _elementTableAliases;

    public IReadOnlyList<LiveQueryParameter> Parameters => _parameters;

    public LiveQueryArguments ValidationArguments { get; }

    public int MaximumResultCount { get; }

    public Func<TContext, LiveQueryArguments, IQueryable<TResult>> QueryFactory { get; }

    public Expression<Func<TResult, TKey>> KeySelector { get; }

    public IEqualityComparer<TResult> RowComparer { get; }
}

/// <summary>A compiled graph registration backed by the ordinary Live correctness engine.</summary>
public sealed class ContinuousGraphQueryPlan<TResult, TKey>
    where TResult : class
    where TKey : notnull
{
    private readonly ReadOnlyCollection<string> _elementTableAliases;
    private readonly Func<
        IContinuousGraphCdcProjector<TResult, TKey>?,
        ContinuousGraphIncrementalOptions<TResult, TKey>,
        IContinuousGraphIncrementalEvaluator<TResult, TKey>>?
        _automaticEvaluatorFactory;

    internal ContinuousGraphQueryPlan(
        string graphName,
        string? graphSchema,
        IEnumerable<string> elementTableAliases,
        LiveQueryPlan<TResult, TKey> livePlan)
        : this(
            graphName,
            graphSchema,
            elementTableAliases,
            livePlan,
            ContinuousGraphMaintenanceCapabilities.AuthoritativeRepair,
            new ContinuousGraphImpactPlan(
                graphName,
                graphSchema,
                livePlan.Fingerprint,
                string.Empty,
                null,
                null,
                elementTableAliases,
                [],
                string.Empty),
            null)
    {
    }

    internal ContinuousGraphQueryPlan(
        string graphName,
        string? graphSchema,
        IEnumerable<string> elementTableAliases,
        LiveQueryPlan<TResult, TKey> livePlan,
        ContinuousGraphMaintenanceCapabilities maintenanceCapabilities,
        ContinuousGraphImpactPlan impactPlan,
        Func<
            IContinuousGraphCdcProjector<TResult, TKey>?,
            ContinuousGraphIncrementalOptions<TResult, TKey>,
            IContinuousGraphIncrementalEvaluator<TResult, TKey>>?
            automaticEvaluatorFactory)
    {
        GraphName = graphName;
        GraphSchema = graphSchema;
        _elementTableAliases = Array.AsReadOnly(elementTableAliases.ToArray());
        LivePlan = livePlan;
        MaintenanceCapabilities = maintenanceCapabilities;
        ImpactPlan = impactPlan;
        _automaticEvaluatorFactory = automaticEvaluatorFactory;
    }

    public string GraphName { get; }

    public string? GraphSchema { get; }

    public IReadOnlyList<string> ElementTableAliases => _elementTableAliases;

    public LiveQueryPlan<TResult, TKey> LivePlan { get; }

    public ContinuousGraphMaintenanceCapabilities MaintenanceCapabilities { get; }

    public ContinuousGraphImpactPlan ImpactPlan { get; }

    public string Name => LivePlan.Name;

    public string Fingerprint => LivePlan.Fingerprint;

    public IReadOnlyList<LiveTableDependency> Dependencies => LivePlan.Dependencies;

    public LiveQueryArguments Bind(IReadOnlyDictionary<string, object?> values) =>
        LivePlan.Bind(values);

    public LiveQuerySession<TResult, TKey> CreateSession(
        LiveQueryArguments arguments,
        LiveSecurityScope securityScope,
        ILiveInvalidationLog invalidationLog,
        int? resultLimit = null,
        LiveQuerySessionOptions? options = null) =>
        new(LivePlan, arguments, securityScope, invalidationLog, resultLimit, options);

    public ContinuousGraphIncrementalSession<TResult, TKey>
        CreateIncrementalSession(
            LiveQueryArguments arguments,
            LiveSecurityScope securityScope,
            IContinuousGraphIncrementalEvaluator<TResult, TKey> evaluator,
            ContinuousGraphIncrementalOptions<TResult, TKey> options,
            int? resultLimit = null) =>
        new(
            this,
            arguments,
            securityScope,
            evaluator,
            options,
            resultLimit);

    /// <summary>
    /// Creates an automatic three-tier session. Direct CDC projection is used only
    /// when an explicit projector supplies a complete, matching trust contract.
    /// </summary>
    public ContinuousGraphIncrementalSession<TResult, TKey>
        CreateIncrementalSession(
            LiveQueryArguments arguments,
            LiveSecurityScope securityScope,
            ContinuousGraphIncrementalOptions<TResult, TKey> options) =>
        CreateAutomaticIncrementalSession(
            arguments,
            securityScope,
            options,
            resultLimit: null,
            trustedProjector: null);

    public ContinuousGraphIncrementalSession<TResult, TKey>
        CreateAutomaticIncrementalSession(
            LiveQueryArguments arguments,
            LiveSecurityScope securityScope,
            ContinuousGraphIncrementalOptions<TResult, TKey> options,
            int? resultLimit,
            IContinuousGraphCdcProjector<TResult, TKey>? trustedProjector)
    {
        if (_automaticEvaluatorFactory is null)
        {
            throw new NotSupportedException(
                "This graph plan cannot prove an automatic affected-key mapping. Use the custom evaluator overload or full authoritative sessions.");
        }

        return new ContinuousGraphIncrementalSession<TResult, TKey>(
            this,
            arguments,
            securityScope,
            _automaticEvaluatorFactory(trustedProjector, options),
            options,
            resultLimit);
    }
}

/// <summary>Compiles registered typed SQL/PGQ queries into bounded authoritative Live plans.</summary>
public static class ContinuousGraphQueryCompiler
{
    public static async ValueTask<ContinuousGraphQueryPlan<TResult, TKey>>
        CompileAsync<TContext, TResult, TKey>(
            IDbContextFactory<TContext> contextFactory,
            ContinuousGraphQueryDefinition<TContext, TResult, TKey> definition,
            IContinuousGraphCapabilityProbe? capabilityProbe = null,
            CancellationToken cancellationToken = default)
        where TContext : DbContext
        where TResult : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(definition);
        capabilityProbe ??= new PostgreSql19ContinuousGraphCapabilityProbe();

        await using var context =
            await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await capabilityProbe.EnsureSupportedAsync(context, cancellationToken).ConfigureAwait(false);
        var graph = FindGraph(context, definition);
        var dependencies = ResolveDependencies(context, graph, definition.ElementTableAliases);
        var keyProperty = RequireDirectMember(definition.KeySelector.Body, "result key selector");
        var query = definition.QueryFactory(context, definition.ValidationArguments) ??
            throw new ContinuousGraphQueryRegistrationException(
                $"Continuous graph query '{definition.Name}' returned null during registration.");
        var capturedImpact = BlueTuskGraphQueryCapture.Consume(context);
        var shape = ContinuousGraphQueryShape.Validate(
            query.Expression,
            keyProperty,
            definition.MaximumResultCount);

        string translatedSql;
        try
        {
            translatedSql = query.ToQueryString();
        }
        catch (Exception exception)
        {
            throw new ContinuousGraphQueryRegistrationException(
                $"Continuous graph query '{definition.Name}' could not be translated by EF at registration time.",
                exception);
        }

        if (!translatedSql.Contains("GRAPH_TABLE", StringComparison.OrdinalIgnoreCase))
        {
            throw new ContinuousGraphQueryRegistrationException(
                $"Continuous graph query '{definition.Name}' must translate through GRAPH_TABLE.");
        }

        var canonicalPlan = string.Join(
            '\n',
            typeof(TContext).AssemblyQualifiedName,
            typeof(TResult).AssemblyQualifiedName,
            typeof(TKey).AssemblyQualifiedName,
            graph.Schema,
            graph.Name,
            string.Join(',', definition.ElementTableAliases.Order(StringComparer.Ordinal)),
            string.Join(',', dependencies.Select(static dependency => dependency.ToString())),
            keyProperty,
            definition.MaximumResultCount.ToString(CultureInfo.InvariantCulture),
            shape.CanonicalExpression,
            definition.RowComparer.GetType().AssemblyQualifiedName);
        var fingerprint = LiveQueryFingerprint.Create(
            definition.Name,
            definition.Version,
            Encoding.UTF8.GetBytes(canonicalPlan));
        var capabilities =
            LiveQueryCapabilities.TenantFilter |
            LiveQueryCapabilities.DeterministicOrdering |
            LiveQueryCapabilities.BoundedTake;
        if (shape.HasPredicate)
        {
            capabilities |= LiveQueryCapabilities.ParameterizedPredicate;
        }

        if (dependencies.Length == 1)
        {
            capabilities |= LiveQueryCapabilities.SingleTable;
        }

        var livePlan = new LiveQueryPlan<TResult, TKey>(
            definition.Name,
            definition.DatabaseIdentity,
            fingerprint,
            capabilities,
            dependencies,
            definition.Parameters,
            definition.MaximumResultCount,
            async (execution, token) =>
            {
                await using var executionContext =
                    await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
                var executionQuery =
                    definition.QueryFactory(executionContext, execution.Arguments) ??
                    throw new ContinuousGraphQueryRegistrationException(
                        $"Continuous graph query '{definition.Name}' returned null during execution.");
                return await executionQuery.AsNoTracking().ToListAsync(token).ConfigureAwait(false);
            },
            definition.KeySelector.Compile(),
            definition.RowComparer);

        var automaticImpacts = TryBuildAutomaticImpacts(
            capturedImpact,
            keyProperty,
            graph.Schema ?? context.Model.GetDefaultSchema() ?? "public",
            out var keyProjection);
        var impactPlan = new ContinuousGraphImpactPlan(
            graph.Name,
            graph.Schema,
            fingerprint,
            keyProperty,
            keyProjection?.ElementAlias,
            keyProjection?.ColumnName,
            capturedImpact?.Elements.Select(element => string.Join(
                ':',
                element.Variable,
                element.Alias,
                element.Kind,
                element.Schema ?? graph.Schema ?? context.Model.GetDefaultSchema() ?? "public",
                element.Table,
                string.Join(',', element.KeyColumns))) ?? definition.ElementTableAliases,
            capturedImpact?.Projections.Select(projection => string.Join(
                ':',
                projection.ResultProperty,
                projection.Variable,
                projection.GraphProperty,
                projection.ColumnName ?? "<expression>")) ?? [],
            shape.CanonicalExpression);
        Func<
            IContinuousGraphCdcProjector<TResult, TKey>?,
            ContinuousGraphIncrementalOptions<TResult, TKey>,
            IContinuousGraphIncrementalEvaluator<TResult, TKey>>?
            automaticEvaluatorFactory = null;
        var maintenanceCapabilities =
            ContinuousGraphMaintenanceCapabilities.AuthoritativeRepair;
        if (automaticImpacts is not null)
        {
            maintenanceCapabilities |=
                ContinuousGraphMaintenanceCapabilities.AuthoritativeDelta |
                ContinuousGraphMaintenanceCapabilities.TrustedCdcProjection;
            automaticEvaluatorFactory = (projector, options) =>
                new ContinuousGraphTieredEvaluator<TResult, TKey>(
                    fingerprint,
                    automaticImpacts,
                    async (keys, execution, token) =>
                    {
                        await using var scopedContext =
                            await contextFactory.CreateDbContextAsync(token)
                                .ConfigureAwait(false);
                        var scopedQuery = definition.QueryFactory(
                            scopedContext,
                            execution.Arguments) ??
                            throw new ContinuousGraphQueryRegistrationException(
                                $"Continuous graph query '{definition.Name}' returned null during scoped execution.");
                        return await ContinuousGraphKeyScope.Apply(
                                scopedQuery,
                                definition.KeySelector,
                                keys)
                            .AsNoTracking()
                            .ToListAsync(token)
                            .ConfigureAwait(false);
                    },
                    options.MaximumAffectedKeys,
                    livePlan.KeyComparer,
                    projector);
        }

        return new ContinuousGraphQueryPlan<TResult, TKey>(
            graph.Name,
            graph.Schema,
            definition.ElementTableAliases,
            livePlan,
            maintenanceCapabilities,
            impactPlan,
            automaticEvaluatorFactory);
    }

    private static ReadOnlyCollection<ContinuousGraphAutomaticTableImpact>?
        TryBuildAutomaticImpacts(
            BlueTuskGraphQueryImpactPlan? capturedImpact,
            string keyProperty,
            string defaultSchema,
            out BlueTuskGraphQueryImpactProjection? keyProjection)
    {
        var projectionForKey = capturedImpact?.Projections.SingleOrDefault(projection =>
            string.Equals(
                projection.ResultProperty,
                keyProperty,
                StringComparison.Ordinal));
        keyProjection = projectionForKey;
        if (capturedImpact is null || projectionForKey?.ColumnName is null)
        {
            return null;
        }

        var keyElement = capturedImpact.Elements.SingleOrDefault(element =>
            string.Equals(
                element.Variable,
                projectionForKey.Variable,
                StringComparison.Ordinal));
        if (keyElement is null ||
            keyElement.KeyColumns.Count != 1 ||
            !string.Equals(
                keyElement.KeyColumns[0],
                projectionForKey.ColumnName,
                StringComparison.Ordinal))
        {
            return null;
        }

        var impacts = new List<ContinuousGraphAutomaticTableImpact>(
            capturedImpact.Elements.Count);
        foreach (var element in capturedImpact.Elements)
        {
            IReadOnlyList<string>? columns = null;
            if (string.Equals(
                    element.Variable,
                    keyElement.Variable,
                    StringComparison.Ordinal))
            {
                columns = [projectionForKey.ColumnName];
            }
            else if (element.Kind is BlueTuskGraphElementKind.Edge)
            {
                var mapped = new List<string>(2);
                if (string.Equals(
                        element.SourceVariable,
                        keyElement.Variable,
                        StringComparison.Ordinal))
                {
                    AddEndpointColumn(element.Source, keyElement.Alias, projectionForKey.ColumnName, mapped);
                }

                if (string.Equals(
                        element.DestinationVariable,
                        keyElement.Variable,
                        StringComparison.Ordinal))
                {
                    AddEndpointColumn(element.Destination, keyElement.Alias, projectionForKey.ColumnName, mapped);
                }
                if (mapped.Count > 0)
                {
                    columns = mapped;
                }
            }

            impacts.Add(new ContinuousGraphAutomaticTableImpact(
                element.Schema ?? capturedImpact.GraphSchema ?? defaultSchema,
                element.Table,
                columns ?? [],
                columns is not null));
        }

        return impacts.AsReadOnly();
    }

    private static void AddEndpointColumn(
        BlueTuskGraphEndpointDefinition? endpoint,
        string keyElementAlias,
        string keyColumn,
        List<string> mapped)
    {
        if (endpoint is null ||
            !string.Equals(
                endpoint.VertexTableAlias,
                keyElementAlias,
                StringComparison.Ordinal))
        {
            return;
        }

        for (var index = 0; index < endpoint.VertexKeyColumns.Count; index++)
        {
            if (string.Equals(
                    endpoint.VertexKeyColumns[index],
                    keyColumn,
                    StringComparison.Ordinal) &&
                index < endpoint.EdgeKeyColumns.Count)
            {
                mapped.Add(endpoint.EdgeKeyColumns[index]);
            }
        }
    }

    private static BlueTuskPropertyGraphDefinition FindGraph<TContext, TResult, TKey>(
        TContext context,
        ContinuousGraphQueryDefinition<TContext, TResult, TKey> definition)
        where TContext : DbContext
        where TResult : class
        where TKey : notnull
    {
        var matches = context.Model.GetPropertyGraphs()
            .Where(graph =>
                string.Equals(graph.Name, definition.GraphName, StringComparison.Ordinal) &&
                (definition.GraphSchema is null ||
                 string.Equals(graph.Schema, definition.GraphSchema, StringComparison.Ordinal)))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ContinuousGraphQueryRegistrationException(
                $"Property graph '{definition.GraphName}' is not configured in the EF model."),
            _ => throw new ContinuousGraphQueryRegistrationException(
                $"Property graph name '{definition.GraphName}' is ambiguous; specify its schema."),
        };
    }

    private static LiveTableDependency[] ResolveDependencies(
        DbContext context,
        BlueTuskPropertyGraphDefinition graph,
        IReadOnlyList<string> aliases)
    {
        var elementsByAlias = graph.ElementTables.ToDictionary(
            element => element.Alias,
            StringComparer.Ordinal);
        var dependencies = new List<LiveTableDependency>(aliases.Count);
        foreach (var alias in aliases)
        {
            if (!elementsByAlias.TryGetValue(alias, out var element))
            {
                throw new ContinuousGraphQueryRegistrationException(
                    $"Property graph '{graph.Name}' has no element table alias '{alias}'.");
            }

            dependencies.Add(new LiveTableDependency(
                element.Schema ?? graph.Schema ?? context.Model.GetDefaultSchema() ?? "public",
                element.Table));
        }

        return dependencies
            .Distinct()
            .OrderBy(static dependency => dependency.Schema, StringComparer.Ordinal)
            .ThenBy(static dependency => dependency.Table, StringComparer.Ordinal)
            .ToArray();
    }

    private static string RequireDirectMember(Expression expression, string role)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        return expression is MemberExpression { Expression: ParameterExpression } member
            ? member.Member.Name
            : throw new ContinuousGraphQueryRegistrationException(
                $"A continuous graph {role} must be a direct result property access.");
    }

    private sealed record QueryShape(bool HasPredicate, string CanonicalExpression);

    private sealed class ContinuousGraphQueryShape : ExpressionVisitor
    {
        private readonly string _keyProperty;
        private readonly int _maximumResultCount;
        private readonly HashSet<string> _orderProperties = new(StringComparer.Ordinal);
        private bool _hasPredicate;
        private bool _hasTake;

        private ContinuousGraphQueryShape(string keyProperty, int maximumResultCount)
        {
            _keyProperty = keyProperty;
            _maximumResultCount = maximumResultCount;
        }

        public static QueryShape Validate(
            Expression expression,
            string keyProperty,
            int maximumResultCount)
        {
            var validator = new ContinuousGraphQueryShape(keyProperty, maximumResultCount);
            validator.Visit(expression);
            if (!validator._hasTake)
            {
                throw new ContinuousGraphQueryRegistrationException(
                    "A continuous graph query must contain one bounded Take operation.");
            }

            if (!validator._orderProperties.Contains(validator._keyProperty))
            {
                throw new ContinuousGraphQueryRegistrationException(
                    $"A continuous graph query must have deterministic ordering that includes result key '{keyProperty}'.");
            }

            return new QueryShape(validator._hasPredicate, expression.ToString());
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable))
            {
                switch (node.Method.Name)
                {
                    case nameof(Queryable.Where):
                        _hasPredicate = true;
                        break;
                    case nameof(Queryable.OrderBy):
                    case nameof(Queryable.OrderByDescending):
                    case nameof(Queryable.ThenBy):
                    case nameof(Queryable.ThenByDescending):
                        _orderProperties.Add(RequireDirectMember(
                            Unquote(node.Arguments[1]).Body,
                            "ordering selector"));
                        break;
                    case nameof(Queryable.Take):
                        if (_hasTake)
                        {
                            throw new ContinuousGraphQueryRegistrationException(
                                "A continuous graph query must contain exactly one bounded Take operation.");
                        }

                        _hasTake = true;
                        var take = EvaluateTake(node.Arguments[1]);
                        if (take <= 0 || take > _maximumResultCount)
                        {
                            throw new ContinuousGraphQueryRegistrationException(
                                $"Continuous graph query Take({take}) must be between 1 and {_maximumResultCount}.");
                        }

                        break;
                    case nameof(Queryable.Select):
                        break;
                    default:
                        throw new ContinuousGraphQueryRegistrationException(
                            $"Queryable method '{node.Method.Name}' is not supported by the Continuous Graph preview.");
                }
            }
            else if (node.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions) &&
                     node.Method.Name != nameof(EntityFrameworkQueryableExtensions.AsNoTracking))
            {
                throw new ContinuousGraphQueryRegistrationException(
                    $"EF query method '{node.Method.Name}' is not supported by the Continuous Graph preview.");
            }

            return base.VisitMethodCall(node);
        }

        private static LambdaExpression Unquote(Expression expression)
        {
            while (expression.NodeType == ExpressionType.Quote)
            {
                expression = ((UnaryExpression)expression).Operand;
            }

            return expression as LambdaExpression ??
                throw new ContinuousGraphQueryRegistrationException(
                    "A continuous graph query operator requires a lambda expression.");
        }

        private static int EvaluateTake(Expression expression)
        {
            try
            {
                return Expression.Lambda<Func<int>>(expression).Compile().Invoke();
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                throw new ContinuousGraphQueryRegistrationException(
                    "A continuous graph Take bound must be a fixed registration-time integer.",
                    exception);
            }
        }
    }
}
