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
}

public interface IControlPlaneContinuousGraphQueryService
{
    ValueTask<ControlPlaneContinuousGraphOverview> GetContinuousGraphOverviewAsync(
        CancellationToken cancellationToken = default);
}
