using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Graphs.Internal;

internal sealed class BlueTuskPortableGraphTraversal(
    DbContext context, BlueTuskPropertyGraphDefinition graph, BlueTuskGraphTraversalOptions options)
{
    private sealed record Vertex(string Key, string? Edge, Vertex? Parent, int Hops);

    private readonly ISqlGenerationHelper _sql = context.GetService<ISqlGenerationHelper>();
    private int _edges;
    private int _peakFrontier;

    public async Task<BlueTuskGraphTraversalResult> ExecuteAsync(
        string operation, BlueTuskGraphIdentity start, BlueTuskGraphIdentity? target,
        BlueTuskGraphIdentity? afterEdge, CancellationToken cancellationToken)
    {
        var startKey = await FindVertexAsync(start, cancellationToken).ConfigureAwait(false);
        if (startKey is null)
            return Complete();

        string? targetKey = null;
        if (target is not null)
        {
            targetKey = await FindVertexAsync(target, cancellationToken).ConfigureAwait(false);
            if (targetKey is null)
                return Complete();
            if (targetKey == startKey)
                return Complete(path: Path(new Vertex(startKey, null, null, 0)));
        }

        var stepSql = CreateStepSql();
        if (stepSql is null)
            return Complete();

        return operation == "neighbors"
            ? await NeighborsAsync(stepSql, startKey, afterEdge, cancellationToken).ConfigureAwait(false)
            : await SearchAsync(stepSql, startKey, targetKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BlueTuskGraphTraversalResult> NeighborsAsync(
        string sql, string start, BlueTuskGraphIdentity? afterEdge, CancellationToken cancellationToken)
    {
        var rows = new List<BlueTuskGraphTraversalRow>();
        var maximum = Math.Min(options.MaximumEdges, options.MaximumResults);
        await using var command = BlueTuskGraphTraversalExecutor.CreateCommand(context, sql,
            ("frontier", $"[{start}]"), ("limit", maximum + 1),
            ("after", afterEdge is null ? "null" : afterEdge.EncodeIdentity()));
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
            .ConfigureAwait(false);

        long bytes = 4_096;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == maximum)
            {
                return _edges >= options.MaximumEdges
                    ? Exhausted("edges")
                    : Complete(rows, next: rows[^1].Edge);
            }
            _edges++;
            var edge = reader.GetString(1);
            var vertex = reader.GetString(2);
            bytes += 256L + 4L * (edge.Length + vertex.Length);
            if (bytes > options.MaximumBytes)
                return Exhausted("memory");

            rows.Add(new BlueTuskGraphTraversalRow(Identity(vertex), Identity(edge), 1));
        }
        return Complete(rows);
    }

    private async Task<BlueTuskGraphTraversalResult> SearchAsync(
        string sql, string start, string? target, CancellationToken cancellationToken)
    {
        var first = new Vertex(start, null, null, 0);
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var frontier = new List<Vertex> { first };
        var rows = new List<BlueTuskGraphTraversalRow>();
        long bytes = 4_096;

        for (var depth = 0; depth < options.MaximumDepth && frontier.Count > 0; depth++)
        {
            _peakFrontier = Math.Max(_peakFrontier, frontier.Count);
            var next = new List<Vertex>();
            var input = $"[{string.Join(',', frontier.Select(static vertex => vertex.Key))}]";
            if (bytes + 4L * input.Length > options.MaximumBytes)
                return Exhausted("memory");

            await using var command = BlueTuskGraphTraversalExecutor.CreateCommand(context, sql,
                ("frontier", input), ("limit", options.MaximumEdges - _edges + 1), ("after", "null"));
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_edges == options.MaximumEdges)
                    return Exhausted("edges");

                _edges++;
                var ordinal = checked((int) reader.GetInt64(0)) - 1;
                var edge = reader.GetString(1);
                var key = reader.GetString(2);
                if (!visited.Add(key))
                    continue;

                bytes += 256L + 4L * (key.Length + edge.Length);
                if (bytes > options.MaximumBytes)
                    return Exhausted("memory");

                var vertex = new Vertex(key, edge, frontier[ordinal], depth + 1);
                if (string.Equals(key, target, StringComparison.Ordinal))
                    return Complete(path: Path(vertex));

                if (target is null)
                {
                    if (rows.Count == options.MaximumResults)
                        return Exhausted("results");
                    rows.Add(new BlueTuskGraphTraversalRow(Identity(key), null, vertex.Hops));
                }
                if (vertex.Hops < options.MaximumDepth)
                {
                    if (next.Count == options.MaximumFrontier)
                        return Exhausted("frontier");
                    next.Add(vertex);
                }
            }
            frontier = next;
        }

        return Complete(rows);
    }

    private async Task<string?> FindVertexAsync(BlueTuskGraphIdentity identity, CancellationToken cancellationToken)
    {
        var vertex = VertexElement(identity.Element);
        if (vertex.KeyColumns.Count != identity.Key.Count)
            throw new ArgumentException("The key does not match the vertex key arity.", nameof(identity));

        var table = Table(vertex);
        var sql = $"SELECT {OutputIdentity(vertex, "v")} FROM {table} v " +
                  $"WHERE {KeyMatch(vertex, "v", vertex.KeyColumns, "CAST(@key AS jsonb)")}";
        await using var command = BlueTuskGraphTraversalExecutor.CreateCommand(context, sql,
            ("key", identity.EncodeKey()));
        return (string?) await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? CreateStepSql()
    {
        var branches = new List<string>();
        foreach (var edge in graph.ElementTables.Where(static element => element.Kind == BlueTuskGraphElementKind.Edge))
        {
            var source = edge.Source!;
            var destination = edge.Destination!;
            if (options.Direction != BlueTuskGraphEdgeDirection.Incoming)
                branches.Add(Branch(edge, source, destination, skipSelfLoop: false));
            if (options.Direction != BlueTuskGraphEdgeDirection.Outgoing)
                branches.Add(Branch(edge, destination, source,
                    options.Direction == BlueTuskGraphEdgeDirection.Undirected &&
                    source.VertexTableAlias == destination.VertexTableAlias));
        }

        return branches.Count == 0
            ? null
            : $$"""
                WITH frontier AS (
                    SELECT value->>0 AS element, value->1 AS key, ordinal
                      FROM pg_catalog.jsonb_array_elements(CAST(@frontier AS jsonb))
                           WITH ORDINALITY AS f(value, ordinal)
                )
                SELECT ordinal, edge_key, vertex_key FROM ({{string.Join(" UNION ALL ", branches)}}) candidates
                 WHERE CAST(@after AS jsonb) = 'null'::jsonb
                       OR edge_key COLLATE pg_catalog."C" > CAST(@after AS jsonb)::text
                 ORDER BY ordinal, edge_key COLLATE pg_catalog."C" LIMIT @limit
                """;
    }

    private string Branch(
        BlueTuskGraphElementTableDefinition edge, BlueTuskGraphEndpointDefinition source,
        BlueTuskGraphEndpointDefinition destination, bool skipSelfLoop)
    {
        var sourceVertex = VertexElement(source.VertexTableAlias);
        var vertex = VertexElement(destination.VertexTableAlias);
        if (!sourceVertex.KeyColumns.SequenceEqual(source.VertexKeyColumns, StringComparer.Ordinal) ||
            !vertex.KeyColumns.SequenceEqual(destination.VertexKeyColumns, StringComparer.Ordinal))
            throw new BlueTuskGraphTranslationException("Traversal endpoints must reference the graph vertex key.");

        var left = $"f.element COLLATE pg_catalog.\"C\" = {Literal(source.VertexTableAlias)}";
        var edgeMatch = KeyMatch(edge, "e", source.EdgeKeyColumns, $"(CASE WHEN {left} THEN f.key END)");
        var vertexMatch = string.Join(" AND ", destination.EdgeKeyColumns.Zip(vertex.KeyColumns,
            (edgeColumn, vertexColumn) => $"e.{Quote(edgeColumn)} = v.{Quote(vertexColumn)}"));
        var loop = skipSelfLoop
            ? $" AND ROW({Columns("e", source.EdgeKeyColumns)}) IS DISTINCT FROM " +
              $"ROW({Columns("e", destination.EdgeKeyColumns)})"
            : string.Empty;

        return $"SELECT f.ordinal, {OutputIdentity(edge, "e")} AS edge_key, " +
               $"{OutputIdentity(vertex, "v")} AS vertex_key FROM frontier f " +
               $"JOIN {Table(edge)} e ON {edgeMatch} JOIN {Table(vertex)} v ON {vertexMatch} WHERE {left}{loop}";
    }

    private string KeyMatch(
        BlueTuskGraphElementTableDefinition table, string alias, IReadOnlyList<string> columns, string key)
    {
        var recordFields = string.Join(", ", columns.Select((column, index) =>
            $"{Literal(column)}, {key}->{index}"));
        var record = $"pg_catalog.jsonb_populate_record(NULL::{Table(table)}, " +
                     $"pg_catalog.jsonb_build_object({recordFields}))";

        return string.Join(" AND ", columns.Select(column => $"{alias}.{Quote(column)} = ({record}).{Quote(column)}"));
    }

    private BlueTuskGraphElementTableDefinition VertexElement(string element) =>
        graph.ElementTables.SingleOrDefault(item =>
            item.Kind == BlueTuskGraphElementKind.Vertex && string.Equals(item.Alias, element, StringComparison.Ordinal))
        ?? throw new BlueTuskGraphTranslationException($"Unknown graph vertex '{element}'.");

    private string Quote(string identifier) => _sql.DelimitIdentifier(identifier);

    private string Table(BlueTuskGraphElementTableDefinition element) =>
        _sql.DelimitIdentifier(element.Table, element.Schema ?? context.Model.GetDefaultSchema() ?? "public");

    private string Columns(string alias, IReadOnlyList<string> columns) =>
        string.Join(", ", columns.Select(column => $"{alias}.{Quote(column)}"));

    private static string Literal(string value) =>
        $"E'{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal)}'";

    private string OutputIdentity(BlueTuskGraphElementTableDefinition element, string alias) =>
        $"pg_catalog.jsonb_build_array({Literal(element.Alias)}::text, " +
        $"pg_catalog.jsonb_build_array({Columns(alias, element.KeyColumns)}))::text";

    private static BlueTuskGraphIdentity Identity(string value)
    {
        using var document = JsonDocument.Parse(value);
        return BlueTuskGraphIdentity.Read(document.RootElement);
    }

    private static BlueTuskGraphPath Path(Vertex last)
    {
        var vertices = new BlueTuskGraphIdentity[last.Hops + 1];
        var edges = new BlueTuskGraphIdentity[last.Hops];
        for (Vertex? vertex = last; vertex is not null; vertex = vertex.Parent)
        {
            vertices[vertex.Hops] = Identity(vertex.Key);
            if (vertex.Edge is not null)
                edges[vertex.Hops - 1] = Identity(vertex.Edge);
        }
        return new BlueTuskGraphPath(Array.AsReadOnly(vertices), Array.AsReadOnly(edges));
    }

    private BlueTuskGraphTraversalResult Complete(
        List<BlueTuskGraphTraversalRow>? rows = null, BlueTuskGraphPath? path = null, BlueTuskGraphIdentity? next = null) =>
        new(rows is null ? Array.Empty<BlueTuskGraphTraversalRow>() : rows.AsReadOnly(), path, next, null,
            _edges, _peakFrontier, usedServerEngine: false);

    private BlueTuskGraphTraversalResult Exhausted(string reason) =>
        new(Array.Empty<BlueTuskGraphTraversalRow>(), null, null, reason, _edges, _peakFrontier, usedServerEngine: false);
}
