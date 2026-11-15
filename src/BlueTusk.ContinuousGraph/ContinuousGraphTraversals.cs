using System.Text.Json;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.Live;
using BlueTusk.Streams;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.ContinuousGraph;

public static partial class ContinuousGraphQueryCompiler
{
    /// <summary>Registers reachable vertices with authoritative Live requery under a caller-scoped context.</summary>
    public static ContinuousGraphQueryPlan<BlueTuskGraphTraversalRow, string> CompileReachability<TContext>(
        TContext modelContext,
        string name,
        string databaseIdentity,
        string version,
        string graphName,
        string? graphSchema,
        IEnumerable<LiveQueryParameter> parameters,
        Func<LiveQueryArguments, BlueTuskGraphIdentity> anchor,
        Func<LiveQueryExecutionContext, CancellationToken, ValueTask<TContext>> authorizedContextFactory,
        BlueTuskGraphTraversalOptions? options = null)
        where TContext : DbContext
    {
        BlueTuskGuard.NotNull(anchor);
        options ??= new BlueTuskGraphTraversalOptions();
        return CompileTraversal(modelContext, name, databaseIdentity, version, graphName, graphSchema,
            parameters, authorizedContextFactory, options, "reachable", options.MaximumResults,
            (root, arguments, token) => root.ReachableAsync(anchor(arguments), options, token),
            static result => result.Rows.OrderBy(static row => row.Hops)
                .ThenBy(static row => row.Vertex.EncodeIdentity(), StringComparer.Ordinal).ToArray(),
            static row => row.Vertex.EncodeIdentity(), TraversalRowComparer.Instance);
    }

    /// <summary>Registers the first bounded neighbor page with authoritative Live requery.</summary>
    public static ContinuousGraphQueryPlan<BlueTuskGraphTraversalRow, string> CompileNeighbors<TContext>(
        TContext modelContext,
        string name,
        string databaseIdentity,
        string version,
        string graphName,
        string? graphSchema,
        IEnumerable<LiveQueryParameter> parameters,
        Func<LiveQueryArguments, BlueTuskGraphIdentity> anchor,
        Func<LiveQueryExecutionContext, CancellationToken, ValueTask<TContext>> authorizedContextFactory,
        BlueTuskGraphTraversalOptions? options = null)
        where TContext : DbContext
    {
        BlueTuskGuard.NotNull(anchor);
        options ??= new BlueTuskGraphTraversalOptions();
        return CompileTraversal(modelContext, name, databaseIdentity, version, graphName, graphSchema,
            parameters, authorizedContextFactory, options, "neighbors", options.MaximumResults,
            (root, arguments, token) => root.NeighborsAsync(anchor(arguments), options, cancellationToken: token),
            static result => result.Rows.OrderBy(static row => row.Edge!.EncodeIdentity(), StringComparer.Ordinal)
                .ToArray(),
            static row => row.Edge!.EncodeIdentity(), TraversalRowComparer.Instance);
    }

    /// <summary>Registers the canonical path between two anchors with authoritative Live requery.</summary>
    public static ContinuousGraphQueryPlan<BlueTuskGraphPath, string> CompileShortestPath<TContext>(
        TContext modelContext,
        string name,
        string databaseIdentity,
        string version,
        string graphName,
        string? graphSchema,
        IEnumerable<LiveQueryParameter> parameters,
        Func<LiveQueryArguments, BlueTuskGraphIdentity> anchor,
        Func<LiveQueryArguments, BlueTuskGraphIdentity> target,
        Func<LiveQueryExecutionContext, CancellationToken, ValueTask<TContext>> authorizedContextFactory,
        BlueTuskGraphTraversalOptions? options = null)
        where TContext : DbContext
    {
        BlueTuskGuard.NotNull(anchor);
        BlueTuskGuard.NotNull(target);
        options ??= new BlueTuskGraphTraversalOptions();
        return CompileTraversal(modelContext, name, databaseIdentity, version, graphName, graphSchema,
            parameters, authorizedContextFactory, options, "shortest_path", 1,
            (root, arguments, token) => root.ShortestPathAsync(anchor(arguments), target(arguments), options, token),
            static result => result.Path is { } path ? [path] : [],
            static _ => "path", TraversalPathComparer.Instance);
    }

    private static ContinuousGraphQueryPlan<TResult, string> CompileTraversal<TContext, TResult>(
        TContext modelContext,
        string name,
        string databaseIdentity,
        string version,
        string graphName,
        string? graphSchema,
        IEnumerable<LiveQueryParameter> parameters,
        Func<LiveQueryExecutionContext, CancellationToken, ValueTask<TContext>> authorizedContextFactory,
        BlueTuskGraphTraversalOptions options,
        string operation,
        int maximumResultCount,
        Func<BlueTuskGraphQueryRoot, LiveQueryArguments, CancellationToken, Task<BlueTuskGraphTraversalResult>> execute,
        Func<BlueTuskGraphTraversalResult, IReadOnlyList<TResult>> resultSelector,
        Func<TResult, string> keySelector,
        IEqualityComparer<TResult> rowComparer)
        where TContext : DbContext
        where TResult : class
    {
        BlueTuskGuard.NotNull(modelContext);
        BlueTuskGuard.NotNullOrWhiteSpace(version);
        BlueTuskGuard.NotNull(authorizedContextFactory);
        options.Validate();

        var graph = modelContext.PropertyGraph(graphName, graphSchema).Definition;
        var aliases = graph.ElementTables.Select(static element => element.Alias)
            .OrderBy(static alias => alias, StringComparer.Ordinal).ToArray();
        var dependencies = ResolveDependencies(modelContext, graph, aliases);
        var defaultSchema = modelContext.Model.GetDefaultSchema() ?? "public";
        var graphShape = JsonSerializer.Serialize(graph);
        var canonical = JsonSerializer.Serialize(new
        {
            Operation = operation,
            Graph = graph,
            Tables = dependencies,
            options.Direction,
            options.MaximumDepth,
            options.MaximumEdges,
            options.MaximumFrontier,
            options.MaximumResults,
            options.MaximumBytes,
        });
        if (options.EdgeAliases is { } selected)
            canonical += "|" + JsonSerializer.Serialize(selected.Distinct(StringComparer.Ordinal).OrderBy(static alias => alias, StringComparer.Ordinal));
        var fingerprint = CreateFingerprint(name, version, canonical);
        var live = new LiveQueryPlan<TResult, string>(
            name, databaseIdentity, fingerprint,
            LiveQueryCapabilities.DeterministicOrdering | LiveQueryCapabilities.BoundedTake |
            LiveQueryCapabilities.ParameterizedPredicate,
            dependencies, parameters, maximumResultCount,
            async (execution, token) =>
            {
                await using var context = await authorizedContextFactory(execution, token).ConfigureAwait(false);
                var root = context.PropertyGraph(graph.Name, graph.Schema);
                if (!string.Equals(context.Model.GetDefaultSchema() ?? "public", defaultSchema, StringComparison.Ordinal) ||
                    (!ReferenceEquals(root.Definition, graph) &&
                     !string.Equals(JsonSerializer.Serialize(root.Definition), graphShape, StringComparison.Ordinal)))
                    throw new ContinuousGraphQueryRegistrationException("The authorized context has a different graph mapping.");

                var result = await execute(root, execution.Arguments, token).ConfigureAwait(false);
                if (result.BudgetExhausted)
                    throw new ContinuousGraphIncrementalException(
                        $"Graph traversal exceeded its {result.BudgetReason} budget before it could publish a complete result.");
                return resultSelector(result);
            },
            keySelector, rowComparer, StringComparer.Ordinal);
        var impact = new ContinuousGraphImpactPlan(
            graph.Name, graph.Schema, fingerprint, string.Empty, null, null, aliases, [], canonical);
        var evaluator = new TraversalRepairEvaluator<TResult>(dependencies);
        return new ContinuousGraphQueryPlan<TResult, string>(
            graph.Name, graph.Schema, aliases, live, ContinuousGraphMaintenanceCapabilities.AuthoritativeRepair,
            impact, (_, _) => evaluator);
    }

    private static bool SameIdentity(BlueTuskGraphIdentity left, BlueTuskGraphIdentity right) =>
        left.Element == right.Element && left.Key.SequenceEqual(right.Key);

    private sealed class TraversalRowComparer : IEqualityComparer<BlueTuskGraphTraversalRow>
    {
        public static TraversalRowComparer Instance { get; } = new();

        public bool Equals(BlueTuskGraphTraversalRow? left, BlueTuskGraphTraversalRow? right) =>
            ReferenceEquals(left, right) || (left is not null && right is not null &&
                left.Hops == right.Hops && SameIdentity(left.Vertex, right.Vertex) &&
                (left.Edge is null ? right.Edge is null : right.Edge is not null && SameIdentity(left.Edge, right.Edge)));

        public int GetHashCode(BlueTuskGraphTraversalRow row) =>
            HashCode.Combine(row.Hops, row.Vertex.EncodeIdentity(), row.Edge?.EncodeIdentity());
    }

    private sealed class TraversalPathComparer : IEqualityComparer<BlueTuskGraphPath>
    {
        public static TraversalPathComparer Instance { get; } = new();

        public bool Equals(BlueTuskGraphPath? left, BlueTuskGraphPath? right) =>
            ReferenceEquals(left, right) || (left is not null && right is not null &&
                SameSequence(left.Vertices, right.Vertices) && SameSequence(left.Edges, right.Edges));

        public int GetHashCode(BlueTuskGraphPath path)
        {
            var hash = new HashCode();
            foreach (var identity in path.Vertices)
                hash.Add(identity.EncodeIdentity(), StringComparer.Ordinal);
            foreach (var identity in path.Edges)
                hash.Add(identity.EncodeIdentity(), StringComparer.Ordinal);
            return hash.ToHashCode();
        }

        private static bool SameSequence(
            IReadOnlyList<BlueTuskGraphIdentity> left, IReadOnlyList<BlueTuskGraphIdentity> right)
        {
            if (left.Count != right.Count)
                return false;
            for (var index = 0; index < left.Count; index++)
                if (!SameIdentity(left[index], right[index]))
                    return false;
            return true;
        }
    }

    private sealed class TraversalRepairEvaluator<TResult>(IEnumerable<LiveTableDependency> dependencies) :
        IContinuousGraphIncrementalEvaluator<TResult, string> where TResult : class
    {
        private readonly HashSet<(string Schema, string Table)> _dependencies =
            dependencies.Select(static table => (table.Schema, table.Table)).ToHashSet();

        public async ValueTask<ContinuousGraphIncrementalResult<TResult, string>> EvaluateAsync(
            ChangeTransaction transaction, LiveQueryExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            await foreach (var change in transaction.Changes.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var relevant = change switch
                {
                    InsertChange insert => Contains(insert.NewRow.Table),
                    UpdateChange update => Contains(update.OldRow.Table) || Contains(update.NewRow.Table),
                    DeleteChange delete => Contains(delete.OldRow.Table),
                    TruncateChange truncate => truncate.Tables.Any(Contains),
                    _ => false,
                };
                if (relevant)
                    return ContinuousGraphIncrementalResult<TResult, string>.RequiresRepair("traversal-dependency-changed");
            }

            return ContinuousGraphIncrementalResult<TResult, string>.Unrelated("no-traversal-dependency");
        }

        private bool Contains(ChangeTable table) => _dependencies.Contains((table.Schema, table.Name));
    }
}
