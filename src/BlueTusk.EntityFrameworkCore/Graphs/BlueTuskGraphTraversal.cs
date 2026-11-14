using System.Collections.ObjectModel;
using System.Text.Json;

namespace BlueTusk.EntityFrameworkCore.Graphs;

/// <summary>Identifies a vertex or edge using its graph element alias and ordered key.</summary>
public sealed class BlueTuskGraphIdentity
{
    public BlueTuskGraphIdentity(string element, params object[] key)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(element);
        BlueTuskGuard.NotNull(key);
        if (key.Length is < 1 or > 8 || key.Any(static value => value is not (int or long or Guid)))
            throw new ArgumentException("A graph key must contain one to eight integer or UUID values.", nameof(key));

        Element = element;
        Key = Array.AsReadOnly(key.ToArray());
    }

    public string Element { get; }

    public IReadOnlyList<object> Key { get; }

    internal string EncodeKey() => JsonSerializer.Serialize(Key);

    internal string EncodeIdentity() => JsonSerializer.Serialize(new object[] { Element, Key });

    internal static BlueTuskGraphIdentity Read(JsonElement value) =>
        new(value[0].GetString()!, value[1].EnumerateArray().Select(static key =>
            key.ValueKind is JsonValueKind.String ? (object) key.GetGuid() : key.GetInt64()).ToArray());
}

/// <summary>Sets the work limits and direction of a registered graph traversal.</summary>
public sealed class BlueTuskGraphTraversalOptions
{
    public BlueTuskGraphEdgeDirection Direction { get; init; }
    public int MaximumDepth { get; init; } = 8;
    public int MaximumFrontier { get; init; } = 4_096;
    public int MaximumEdges { get; init; } = 100_000;
    public int MaximumResults { get; init; } = 1_000;
    public int MaximumBytes { get; init; } = 8_388_608;

    /// <summary>Allows use of an installed, compatible BlueTusk graph engine.</summary>
    public bool UseServerEngine { get; init; }

    internal void Validate()
    {
        if (Direction is < BlueTuskGraphEdgeDirection.Outgoing or > BlueTuskGraphEdgeDirection.Undirected ||
            MaximumDepth is < 0 or > 64 || MaximumFrontier is < 1 or > 100_000 ||
            MaximumEdges is < 1 or > 1_000_000 || MaximumResults is < 1 or > 10_000 ||
            MaximumBytes is < 65_536 or > 67_108_864)
            throw new ArgumentOutOfRangeException(nameof(BlueTuskGraphTraversalOptions),
                "A graph traversal option exceeds the supported limits.");
    }
}

/// <summary>A visible vertex, with its edge for neighbor queries or distance for reachability queries.</summary>
public sealed class BlueTuskGraphTraversalRow
{
    internal BlueTuskGraphTraversalRow(BlueTuskGraphIdentity vertex, BlueTuskGraphIdentity? edge, int hops)
    {
        Vertex = vertex;
        Edge = edge;
        Hops = hops;
    }

    public BlueTuskGraphIdentity Vertex { get; }
    public BlueTuskGraphIdentity? Edge { get; }
    public int Hops { get; }
}

/// <summary>An ordered path, including the starting vertex.</summary>
public sealed class BlueTuskGraphPath
{
    internal BlueTuskGraphPath(
        IReadOnlyList<BlueTuskGraphIdentity> vertices, IReadOnlyList<BlueTuskGraphIdentity> edges)
    {
        Vertices = vertices;
        Edges = edges;
    }

    public IReadOnlyList<BlueTuskGraphIdentity> Vertices { get; }
    public IReadOnlyList<BlueTuskGraphIdentity> Edges { get; }
}

/// <summary>A bounded graph traversal and its work counters.</summary>
public sealed class BlueTuskGraphTraversalResult
{
    internal BlueTuskGraphTraversalResult(
        IReadOnlyList<BlueTuskGraphTraversalRow> rows,
        BlueTuskGraphPath? path,
        BlueTuskGraphIdentity? nextEdge,
        string? budgetReason,
        int edgesExamined,
        int peakFrontier,
        bool usedServerEngine)
    {
        Rows = rows;
        Path = path;
        NextEdge = nextEdge;
        BudgetReason = budgetReason;
        EdgesExamined = edgesExamined;
        PeakFrontier = peakFrontier;
        UsedServerEngine = usedServerEngine;
    }

    public IReadOnlyList<BlueTuskGraphTraversalRow> Rows { get; }
    public BlueTuskGraphPath? Path { get; }
    public BlueTuskGraphIdentity? NextEdge { get; }
    public string? BudgetReason { get; }
    public bool BudgetExhausted => BudgetReason is not null;
    public int EdgesExamined { get; }
    public int PeakFrontier { get; }
    public bool UsedServerEngine { get; }

    internal static BlueTuskGraphTraversalResult Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement;
        var rows = result.GetProperty("rows").EnumerateArray().Select(static row =>
            new BlueTuskGraphTraversalRow(BlueTuskGraphIdentity.Read(row.GetProperty("vertex")),
                row.TryGetProperty("edge", out var edge) ? BlueTuskGraphIdentity.Read(edge) : null,
                row.TryGetProperty("hops", out var hops) ? hops.GetInt32() : 1)).ToArray();
        var path = result.GetProperty("path");
        var next = result.GetProperty("next");
        var work = result.GetProperty("work");
        return new BlueTuskGraphTraversalResult(Array.AsReadOnly(rows),
            path.ValueKind == JsonValueKind.Null ? null : new BlueTuskGraphPath(
                ReadIdentities(path.GetProperty("vertices")), ReadIdentities(path.GetProperty("edges"))),
            next.ValueKind == JsonValueKind.Null ? null : BlueTuskGraphIdentity.Read(next),
            result.GetProperty("reason").GetString(),
            work.GetProperty("edges").GetInt32(), work.GetProperty("frontier").GetInt32(), true);
    }

    private static ReadOnlyCollection<BlueTuskGraphIdentity> ReadIdentities(JsonElement values) =>
        Array.AsReadOnly(values.EnumerateArray().Select(BlueTuskGraphIdentity.Read).ToArray());
}

public sealed partial class BlueTuskGraphQueryRoot
{
    internal BlueTuskPropertyGraphDefinition Definition => _graph;

    /// <summary>Registers the EF graph metadata with an installed BlueTusk extension.</summary>
    public Task RegisterEngineAsync(CancellationToken cancellationToken = default) =>
        Internal.BlueTuskGraphTraversalExecutor.RegisterAsync(_context, _graph, cancellationToken);

    public Task<BlueTuskGraphTraversalResult> NeighborsAsync(
        BlueTuskGraphIdentity start,
        BlueTuskGraphTraversalOptions? options = null,
        BlueTuskGraphIdentity? afterEdge = null,
        CancellationToken cancellationToken = default) =>
        Internal.BlueTuskGraphTraversalExecutor.ExecuteAsync(
            _context, _graph, "neighbors", start, null, afterEdge, options, cancellationToken);

    public Task<BlueTuskGraphTraversalResult> ReachableAsync(
        BlueTuskGraphIdentity start,
        BlueTuskGraphTraversalOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Internal.BlueTuskGraphTraversalExecutor.ExecuteAsync(
            _context, _graph, "reachable", start, null, null, options, cancellationToken);

    public Task<BlueTuskGraphTraversalResult> ShortestPathAsync(
        BlueTuskGraphIdentity start,
        BlueTuskGraphIdentity target,
        BlueTuskGraphTraversalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        BlueTuskGuard.NotNull(target);
        return Internal.BlueTuskGraphTraversalExecutor.ExecuteAsync(
            _context, _graph, "shortest_path", start, target, null, options, cancellationToken);
    }
}
