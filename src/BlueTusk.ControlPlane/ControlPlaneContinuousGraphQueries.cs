namespace BlueTusk.ControlPlane;

public sealed class ControlPlaneContinuousGraphOverview
{
    public ControlPlaneContinuousGraphOverview(
        DateTimeOffset observedAt,
        IReadOnlyList<ControlPlaneContinuousGraphQuerySnapshot> queries)
    {
        ObservedAt = observedAt;
        Queries = queries ?? throw new ArgumentNullException(nameof(queries));
    }

    public DateTimeOffset ObservedAt { get; }

    public IReadOnlyList<ControlPlaneContinuousGraphQuerySnapshot> Queries { get; }
}

public sealed class ControlPlaneContinuousGraphQuerySnapshot
{
    public ControlPlaneContinuousGraphQuerySnapshot(
        string name,
        string databaseIdentity,
        string queryFingerprint,
        string graphName,
        string? graphSchema,
        IReadOnlyList<string> elementTableAliases,
        IReadOnlyList<string> tableDependencies,
        int maximumResultCount,
        string capabilities)
        : this(
            name,
            databaseIdentity,
            queryFingerprint,
            graphName,
            graphSchema,
            elementTableAliases,
            tableDependencies,
            maximumResultCount,
            capabilities,
            [],
            false)
    {
    }

    public ControlPlaneContinuousGraphQuerySnapshot(
        string name,
        string databaseIdentity,
        string queryFingerprint,
        string graphName,
        string? graphSchema,
        IReadOnlyList<string> elementTableAliases,
        IReadOnlyList<string> tableDependencies,
        int maximumResultCount,
        string capabilities,
        IReadOnlyList<ControlPlaneContinuousGraphParameterSnapshot> parameters,
        bool canExecute)
    {
        Name = name;
        DatabaseIdentity = databaseIdentity;
        QueryFingerprint = queryFingerprint;
        GraphName = graphName;
        GraphSchema = graphSchema;
        ElementTableAliases =
            elementTableAliases ?? throw new ArgumentNullException(nameof(elementTableAliases));
        TableDependencies =
            tableDependencies ?? throw new ArgumentNullException(nameof(tableDependencies));
        MaximumResultCount = maximumResultCount;
        Capabilities = capabilities;
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        CanExecute = canExecute;
    }

    public string Name { get; }

    public string DatabaseIdentity { get; }

    public string QueryFingerprint { get; }

    public string GraphName { get; }

    public string? GraphSchema { get; }

    public IReadOnlyList<string> ElementTableAliases { get; }

    public IReadOnlyList<string> TableDependencies { get; }

    public int MaximumResultCount { get; }

    public string Capabilities { get; }

    public IReadOnlyList<ControlPlaneContinuousGraphParameterSnapshot> Parameters { get; }

    public bool CanExecute { get; }
}

public sealed record ControlPlaneContinuousGraphParameterSnapshot(
    string Name,
    string Type,
    bool AllowNull,
    bool Editable,
    string? SuggestedValue);

public sealed record ControlPlaneContinuousGraphRunRequest(
    IReadOnlyDictionary<string, string?> Parameters);

public sealed record ControlPlaneContinuousGraphProperty(
    string Name,
    string? Value);

public sealed record ControlPlaneContinuousGraphNode(
    string Id,
    string Label,
    string Category,
    IReadOnlyList<ControlPlaneContinuousGraphProperty> Properties);

public sealed record ControlPlaneContinuousGraphEdge(
    string Id,
    string SourceNodeId,
    string TargetNodeId,
    string Label,
    string Category,
    bool Directed,
    IReadOnlyList<ControlPlaneContinuousGraphProperty> Properties);

public sealed record ControlPlaneContinuousGraphFragment(
    IReadOnlyList<ControlPlaneContinuousGraphNode> Nodes,
    IReadOnlyList<ControlPlaneContinuousGraphEdge> Edges);

public sealed record ControlPlaneContinuousGraphComposition(
    string Category,
    int Count);

public sealed class ControlPlaneContinuousGraphRunResult
{
    public ControlPlaneContinuousGraphRunResult(
        Guid executionId,
        DateTimeOffset observedAt,
        TimeSpan duration,
        string queryFingerprint,
        string queryName,
        string databaseIdentity,
        string graphName,
        string? graphSchema,
        int resultRowCount,
        IReadOnlyList<ControlPlaneContinuousGraphNode> nodes,
        IReadOnlyList<ControlPlaneContinuousGraphEdge> edges,
        IReadOnlyList<ControlPlaneContinuousGraphComposition> nodeComposition,
        IReadOnlyList<ControlPlaneContinuousGraphComposition> edgeComposition)
    {
        ExecutionId = executionId;
        ObservedAt = observedAt;
        Duration = duration;
        QueryFingerprint = queryFingerprint;
        QueryName = queryName;
        DatabaseIdentity = databaseIdentity;
        GraphName = graphName;
        GraphSchema = graphSchema;
        ResultRowCount = resultRowCount;
        Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        Edges = edges ?? throw new ArgumentNullException(nameof(edges));
        NodeComposition = nodeComposition ?? throw new ArgumentNullException(nameof(nodeComposition));
        EdgeComposition = edgeComposition ?? throw new ArgumentNullException(nameof(edgeComposition));
    }

    public Guid ExecutionId { get; }

    public DateTimeOffset ObservedAt { get; }

    public TimeSpan Duration { get; }

    public string QueryFingerprint { get; }

    public string QueryName { get; }

    public string DatabaseIdentity { get; }

    public string GraphName { get; }

    public string? GraphSchema { get; }

    public int ResultRowCount { get; }

    public IReadOnlyList<ControlPlaneContinuousGraphNode> Nodes { get; }

    public IReadOnlyList<ControlPlaneContinuousGraphEdge> Edges { get; }

    public IReadOnlyList<ControlPlaneContinuousGraphComposition> NodeComposition { get; }

    public IReadOnlyList<ControlPlaneContinuousGraphComposition> EdgeComposition { get; }
}

public sealed class ControlPlaneContinuousGraphExecutionException : Exception
{
    public ControlPlaneContinuousGraphExecutionException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public interface IControlPlaneContinuousGraphQueryService
{
    ValueTask<ControlPlaneContinuousGraphOverview> GetContinuousGraphOverviewAsync(
        CancellationToken cancellationToken = default);
}

public interface IControlPlaneContinuousGraphExecutionService
{
    ValueTask<ControlPlaneContinuousGraphRunResult> ExecuteAsync(
        string queryFingerprint,
        ControlPlaneActor actor,
        ControlPlaneContinuousGraphRunRequest request,
        CancellationToken cancellationToken = default);
}
