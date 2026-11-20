using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using BlueTusk.Live;

namespace BlueTusk.ContinuousGraph;

/// <summary>Non-generic operational metadata for one compiled graph registration.</summary>
public sealed class ContinuousGraphQueryDescriptor
{
    private readonly ReadOnlyCollection<string> _elementTableAliases;
    private readonly ReadOnlyCollection<LiveTableDependency> _dependencies;

    public ContinuousGraphQueryDescriptor(
        string name,
        string databaseIdentity,
        string fingerprint,
        string graphName,
        string? graphSchema,
        IEnumerable<string> elementTableAliases,
        IEnumerable<LiveTableDependency> dependencies,
        int maximumResultCount,
        LiveQueryCapabilities capabilities)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        BlueTuskGuard.NotNullOrWhiteSpace(databaseIdentity);
        BlueTuskGuard.NotNullOrWhiteSpace(fingerprint);
        BlueTuskGuard.NotNullOrWhiteSpace(graphName);
        BlueTuskGuard.NotNull(elementTableAliases);
        BlueTuskGuard.NotNull(dependencies);
        BlueTuskGuard.NotNegativeOrZero(maximumResultCount);
        var aliases = elementTableAliases.Distinct(StringComparer.Ordinal).ToArray();
        var dependencyArray = dependencies.Distinct().ToArray();
        if (aliases.Length == 0 || dependencyArray.Length == 0)
        {
            throw new ArgumentException(
                "A continuous graph descriptor requires graph aliases and table dependencies.");
        }

        Name = name;
        DatabaseIdentity = databaseIdentity;
        Fingerprint = fingerprint;
        GraphName = graphName;
        GraphSchema = graphSchema;
        _elementTableAliases = Array.AsReadOnly(aliases);
        _dependencies = Array.AsReadOnly(dependencyArray);
        MaximumResultCount = maximumResultCount;
        Capabilities = capabilities;
    }

    public string Name { get; }

    public string DatabaseIdentity { get; }

    public string Fingerprint { get; }

    public string GraphName { get; }

    public string? GraphSchema { get; }

    public IReadOnlyList<string> ElementTableAliases => _elementTableAliases;

    public IReadOnlyList<LiveTableDependency> Dependencies => _dependencies;

    public int MaximumResultCount { get; }

    public LiveQueryCapabilities Capabilities { get; }
}

/// <summary>Tracks compiled graph registrations without retaining result rows or bound parameters.</summary>
public sealed class ContinuousGraphQueryRegistry
{
    private readonly ConcurrentDictionary<string, ContinuousGraphQueryDescriptor> _queries =
        new(StringComparer.Ordinal);

    public int Count => _queries.Count;

    public bool Register(ContinuousGraphQueryDescriptor descriptor)
    {
        BlueTuskGuard.NotNull(descriptor);
        return _queries.TryAdd(descriptor.Fingerprint, descriptor);
    }

    public bool Register<TResult, TKey>(ContinuousGraphQueryPlan<TResult, TKey> plan)
        where TResult : class
        where TKey : notnull
    {
        BlueTuskGuard.NotNull(plan);
        return Register(new ContinuousGraphQueryDescriptor(
            plan.Name,
            plan.LivePlan.DatabaseIdentity,
            plan.Fingerprint,
            plan.GraphName,
            plan.GraphSchema,
            plan.ElementTableAliases,
            plan.Dependencies,
            plan.LivePlan.MaximumResultCount,
            plan.LivePlan.Capabilities));
    }

    public bool Unregister(string fingerprint)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(fingerprint);
        return _queries.TryRemove(fingerprint, out _);
    }

    public IReadOnlyList<ContinuousGraphQueryDescriptor> GetQueries() =>
        _queries.Values
            .OrderBy(static query => query.DatabaseIdentity, StringComparer.Ordinal)
            .ThenBy(static query => query.GraphSchema, StringComparer.Ordinal)
            .ThenBy(static query => query.GraphName, StringComparer.Ordinal)
            .ThenBy(static query => query.Name, StringComparer.Ordinal)
            .ToArray();
}
