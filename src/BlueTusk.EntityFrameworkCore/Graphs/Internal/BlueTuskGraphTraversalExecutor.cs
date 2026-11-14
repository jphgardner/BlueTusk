using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Graphs.Internal;

internal static class BlueTuskGraphTraversalExecutor
{
    internal static async Task RegisterAsync(
        DbContext context, BlueTuskPropertyGraphDefinition graph, CancellationToken cancellationToken)
    {
        var schema = graph.Schema ?? context.Model.GetDefaultSchema() ?? "public";
        var definition = EncodeDefinition(graph, context.Model.GetDefaultSchema() ?? "public");

        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand(context,
                "SELECT bluetusk_graph.register_graph(@schema, @graph, CAST(@definition AS jsonb))",
                ("schema", schema), ("graph", graph.Name), ("definition", definition));
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string EncodeDefinition(BlueTuskPropertyGraphDefinition graph, string defaultSchema)
    {
        return JsonSerializer.Serialize(new
        {
            vertices = graph.ElementTables.Where(static element => element.Kind == BlueTuskGraphElementKind.Vertex)
                .ToDictionary(static element => element.Alias, Element, StringComparer.Ordinal),
            edges = graph.ElementTables.Where(static element => element.Kind == BlueTuskGraphElementKind.Edge)
                .ToDictionary(static element => element.Alias, Element, StringComparer.Ordinal),
        });

        object Element(BlueTuskGraphElementTableDefinition element) => new
        {
            schema = element.Schema ?? defaultSchema,
            table = element.Table,
            key = element.KeyColumns,
            labels = element.Labels.Select(static label => label.Name),
            properties = element.Labels.SelectMany(static label => label.Properties)
                .Where(static property => property.IsColumn)
                .Select(static property => property.Expression)
                .Distinct(StringComparer.Ordinal),
            source = Endpoint(element.Source),
            destination = Endpoint(element.Destination),
        };

        object? Endpoint(BlueTuskGraphEndpointDefinition? endpoint)
        {
            if (endpoint is null)
                return null;

            var vertex = graph.ElementTables.Single(element => element.Alias == endpoint.VertexTableAlias);
            if (!vertex.KeyColumns.SequenceEqual(endpoint.VertexKeyColumns, StringComparer.Ordinal))
                throw new BlueTuskGraphTranslationException("Traversal endpoints must reference the graph vertex key.");

            return new { vertex = endpoint.VertexTableAlias, columns = endpoint.EdgeKeyColumns };
        }
    }

    internal static async Task<BlueTuskGraphTraversalResult> ExecuteAsync(
        DbContext context, BlueTuskPropertyGraphDefinition graph, string operation, BlueTuskGraphIdentity start,
        BlueTuskGraphIdentity? target, BlueTuskGraphIdentity? afterEdge, BlueTuskGraphTraversalOptions? options,
        CancellationToken cancellationToken)
    {
        BlueTuskGuard.NotNull(start);
        options ??= new BlueTuskGraphTraversalOptions();
        options.Validate();
        if (options.EdgeAliases is { } selected &&
            (selected.Count > 64 || selected.Any(alias => !graph.ElementTables.Any(element =>
                element.Kind == BlueTuskGraphElementKind.Edge &&
                string.Equals(element.Alias, alias, StringComparison.Ordinal)))))
            throw new ArgumentException("Edge filters must name registered graph edge aliases.", nameof(options));

        ValidateIdentity(graph, start, BlueTuskGraphElementKind.Vertex);
        if (target is not null)
            ValidateIdentity(graph, target, BlueTuskGraphElementKind.Vertex);
        if (afterEdge is not null)
            ValidateIdentity(graph, afterEdge, BlueTuskGraphElementKind.Edge);
        var defaultSchema = context.Model.GetDefaultSchema() ?? "public";
        var schema = graph.Schema ?? defaultSchema;

        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (options.UseServerEngine && await HasEngineAsync(context, options.EdgeAliases is not null, cancellationToken).ConfigureAwait(false))
            {
                var definition = EncodeDefinition(graph, defaultSchema);
                var native = await ExecuteEngineAsync(context, schema, graph.Name, definition, operation,
                    start, target, afterEdge, options, cancellationToken).ConfigureAwait(false);
                if (native is not null)
                    return native;
            }

            var transaction = context.Database.CurrentTransaction;
            if (transaction is not null && transaction.GetDbTransaction().IsolationLevel
                    is not (IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
                throw new InvalidOperationException(
                    "Portable traversal requires a repeatable-read or serializable transaction when a transaction is already active.");

            await using var ownedTransaction = transaction is null
                ? await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            var portable = new BlueTuskPortableGraphTraversal(context, graph, options);
            var result = await portable.ExecuteAsync(operation, start, target, afterEdge, cancellationToken)
                .ConfigureAwait(false);
            if (ownedTransaction is not null)
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static void ValidateIdentity(
        BlueTuskPropertyGraphDefinition graph, BlueTuskGraphIdentity identity, BlueTuskGraphElementKind kind)
    {
        var element = graph.ElementTables.SingleOrDefault(item =>
            item.Kind == kind && string.Equals(item.Alias, identity.Element, StringComparison.Ordinal));
        if (element is null || element.KeyColumns.Count != identity.Key.Count)
            throw new ArgumentException(
                $"Graph identity '{identity.Element}' does not match an element with that key arity.", nameof(identity));
    }

    private static async Task<bool> HasEngineAsync(
        DbContext context, bool requiresEdgeFilter, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(context, """
            SELECT EXISTS (
                SELECT FROM pg_catalog.pg_extension e
                JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace
                WHERE e.extname = 'bluetusk_graph' AND n.nspname = 'bluetusk_graph'
                  AND e.extversion IN ('1.0', '1.1', '1.2')
                  AND (NOT @filtered OR e.extversion = '1.2')
            )
            """, ("filtered", requiresEdgeFilter));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<BlueTuskGraphTraversalResult?> ExecuteEngineAsync(
        DbContext context, string schema, string graph, string definition, string operation, BlueTuskGraphIdentity start,
        BlueTuskGraphIdentity? target, BlueTuskGraphIdentity? afterEdge, BlueTuskGraphTraversalOptions options,
        CancellationToken cancellationToken)
    {
        var arguments = new List<(string, object)>
        {
            ("schema", schema), ("graph", graph), ("definition", definition), ("start_element", start.Element),
            ("start_key", start.EncodeKey()), ("direction", options.Direction.ToString().ToLowerInvariant()),
            ("edges", options.MaximumEdges), ("bytes", options.MaximumBytes),
        };

        var filtered = options.EdgeAliases is not null;
        if (options.EdgeAliases is { } selected)
            arguments.Add(("edge_aliases", JsonSerializer.Serialize(selected)));

        var filterArgument = filtered
            ? "ARRAY(SELECT pg_catalog.jsonb_array_elements_text(CAST(@edge_aliases AS jsonb))),"
            : string.Empty;
        string call;
        if (operation == "neighbors")
        {
            var function = filtered ? "neighbors_on" : "neighbors";
            call = $"{function}(@schema,@graph,@start_element,CAST(@start_key AS jsonb),{filterArgument}@direction," +
                   "@results,NULLIF(CAST(@after_edge AS jsonb), 'null'::jsonb),@edges,@bytes)";
            arguments.Add(("after_edge", afterEdge is null ? "null" : afterEdge.EncodeIdentity()));
            arguments.Add(("results", options.MaximumResults));
        }
        else
        {
            arguments.Add(("depth", options.MaximumDepth));
            arguments.Add(("frontier", options.MaximumFrontier));
            if (target is null)
            {
                call = filtered
                    ? "reachable_from(@schema,@graph,pg_catalog.jsonb_build_array(pg_catalog.jsonb_build_array(" +
                      "CAST(@start_element AS text),CAST(@start_key AS jsonb)))," + filterArgument +
                      "@depth,@direction,@frontier,@edges,@results,@bytes)"
                    : "reachable(@schema,@graph,@start_element,CAST(@start_key AS jsonb)," +
                      "@depth,@direction,@frontier,@edges,@results,@bytes)";
                arguments.Add(("results", options.MaximumResults));
            }
            else
            {
                var function = filtered ? "shortest_path_on" : "shortest_path";
                    call = $"{function}(@schema,@graph,@start_element,CAST(@start_key AS jsonb)," +
                       $"@target_element,CAST(@target_key AS jsonb),{filterArgument}@depth,@direction,@frontier,@edges,@bytes)";
                arguments.Add(("target_element", target.Element));
                arguments.Add(("target_key", target.EncodeKey()));
            }
        }

        await using var command = CreateCommand(context, $"""
            SELECT bluetusk_graph.{call}::text
              FROM bluetusk_graph._graphs
             WHERE graph_schema = @schema AND graph_name = @graph
               AND definition = CAST(@definition AS jsonb)
            """,
            arguments.ToArray());
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string json ? BlueTuskGraphTraversalResult.Read(json) : null;
    }

    internal static DbCommand CreateCommand(DbContext context, string sql, params (string Name, object Value)[] values)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        if (context.Database.GetCommandTimeout() is { } timeout)
            command.CommandTimeout = timeout;

        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
