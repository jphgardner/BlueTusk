CREATE FUNCTION bluetusk_graph.compare_reachability(
    graph_schema text, graph_name text, start_alias text, start_key jsonb, excluded_edges jsonb,
    max_depth integer DEFAULT 8, direction text DEFAULT 'outgoing',
    max_frontier integer DEFAULT 4096, max_edges integer DEFAULT 100000,
    max_results integer DEFAULT 1000, max_bytes integer DEFAULT 8388608)
RETURNS jsonb LANGUAGE sql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
    SELECT jsonb_build_object(
        'baseline', bluetusk_graph._walk(graph_schema, graph_name, start_alias, start_key,
            direction, 'reachable', NULL, NULL, max_depth, max_frontier, max_edges,
            max_results, max_bytes, NULL),
        'scenario', bluetusk_graph._walk(graph_schema, graph_name, start_alias, start_key,
            direction, 'reachable', NULL, NULL, max_depth, max_frontier, max_edges,
            max_results, max_bytes, NULL, excluded_edges))
$$;

CREATE FUNCTION bluetusk_graph.weighted_shortest_path(
    graph_schema text, graph_name text, start_alias text, start_key jsonb,
    target_alias text, target_key jsonb, weight_property text, max_depth integer DEFAULT 8,
    direction text DEFAULT 'outgoing', max_frontier integer DEFAULT 4096,
    max_edges integer DEFAULT 100000, max_bytes integer DEFAULT 8388608)
RETURNS jsonb LANGUAGE sql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
    SELECT bluetusk_graph._walk(graph_schema, graph_name, start_alias, start_key,
        direction, 'weighted_shortest_path', target_alias, target_key, max_depth,
        max_frontier, max_edges, 1, max_bytes, NULL, NULL, weight_property)
$$;
