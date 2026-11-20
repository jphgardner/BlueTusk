CREATE FUNCTION bluetusk_graph._plan_v2(
    requested_schema text, requested_graph text, start_alias text, target_alias text,
    direction text, weight_property text, selected_edges text[])
RETURNS TABLE (anchor_sql text, target_sql text, step_sql text, graph_definition jsonb)
LANGUAGE plpgsql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    definition jsonb;
    shape jsonb;
    item record;
    origin jsonb;
    destination jsonb;
    vertex jsonb;
    types oid[];
    join_sql text;
    reverse_edge boolean;
    skip_loop text;
    weight_sql text;
    branches text[] := ARRAY[]::text[];
BEGIN
    SELECT g.definition, g.shape INTO definition, shape
      FROM bluetusk_graph._graphs g
     WHERE g.graph_schema = requested_schema AND g.graph_name = requested_graph;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Graph %.% is not registered', requested_schema, requested_graph
            USING ERRCODE = '22023';
    END IF;
    BEGIN
        IF bluetusk_graph._shape(definition) IS DISTINCT FROM shape THEN
            RAISE EXCEPTION 'Graph key or property types have changed' USING ERRCODE = '22023';
        END IF;
    EXCEPTION WHEN invalid_parameter_value OR object_not_in_prerequisite_state THEN
        RAISE EXCEPTION 'Graph %.% mapping is stale', requested_schema, requested_graph
            USING ERRCODE = '55000', DETAIL = SQLERRM;
    END;
    IF selected_edges IS NOT NULL THEN
        IF cardinality(selected_edges) > 64 OR coalesce(array_ndims(selected_edges), 1) <> 1
           OR EXISTS (
               SELECT FROM unnest(selected_edges) AS selector(alias)
                WHERE alias IS NULL OR NOT ((definition->'edges') ? alias)
           ) THEN
            RAISE EXCEPTION 'Edge filter must contain known graph aliases' USING ERRCODE = '22023';
        END IF;
    END IF;
    graph_definition := definition;
    anchor_sql := bluetusk_graph._anchor(definition, start_alias);
    target_sql := bluetusk_graph._anchor(definition, target_alias);
    FOR item IN
        SELECT key AS alias, value AS edge FROM jsonb_each(definition->'edges')
         WHERE selected_edges IS NULL OR key COLLATE "C" = ANY(selected_edges)
    LOOP
        weight_sql := '0::bigint';
        IF weight_property IS NOT NULL THEN
            IF NOT coalesce(item.edge->'properties' ? weight_property, false)
               OR (bluetusk_graph._key_types(bluetusk_graph._relation(item.edge),
                   jsonb_build_array(weight_property), false))[1]
                   NOT IN ('int4'::regtype, 'int8'::regtype) THEN
                RAISE EXCEPTION 'Edge % needs a mapped integer weight column %', item.alias, weight_property
                    USING ERRCODE = '22023';
            END IF;
            weight_sql := format('e.%I::bigint', weight_property);
        END IF;
        FOREACH reverse_edge IN ARRAY ARRAY[false, true]
        LOOP
            IF (reverse_edge AND direction = 'outgoing') OR
               (NOT reverse_edge AND direction = 'incoming') THEN
                CONTINUE;
            END IF;
            origin := item.edge->(CASE WHEN reverse_edge THEN 'destination' ELSE 'source' END);
            destination := item.edge->(CASE WHEN reverse_edge THEN 'source' ELSE 'destination' END);
            vertex := definition->'vertices'->(destination->>'vertex');
            types := bluetusk_graph._key_types(bluetusk_graph._relation(item.edge), origin->'columns', false);
            SELECT string_agg(format('e.%I = v.%I', edge_column, vertex_column), ' AND ')
              INTO join_sql
              FROM ROWS FROM (jsonb_array_elements_text(destination->'columns'),
                              jsonb_array_elements_text(vertex->'key')) AS keys(edge_column, vertex_column);
            skip_loop := '';
            IF direction = 'undirected' AND reverse_edge AND origin->>'vertex' = destination->>'vertex' THEN
                skip_loop := format(' AND ROW(%s) IS DISTINCT FROM ROW(%s)',
                    bluetusk_graph._identities('e', origin->'columns'),
                    bluetusk_graph._identities('e', destination->'columns'));
            END IF;
            branches := array_append(branches, format(
                'SELECT f.ordinal, jsonb_build_array(%L::text, jsonb_build_array(%s))::text AS edge_key,
                        jsonb_build_array(%L::text, jsonb_build_array(%s))::text AS vertex_key,
                        %s AS weight
                   FROM frontier f JOIN %I.%I e ON %s
                   JOIN %I.%I v ON %s
                  WHERE f.element COLLATE pg_catalog."C" = %L%s',
                item.alias, bluetusk_graph._identities('e', item.edge->'key'),
                destination->>'vertex', bluetusk_graph._identities('v', vertex->'key'),
                weight_sql,
                item.edge->>'schema', item.edge->>'table',
                bluetusk_graph._key_match('e', origin->'columns', types,
                    format('(CASE WHEN f.element COLLATE pg_catalog."C" = %L THEN f.key END)',
                        origin->>'vertex')),
                vertex->>'schema', vertex->>'table', join_sql, origin->>'vertex', skip_loop));
        END LOOP;
    END LOOP;
    IF cardinality(branches) > 0 THEN
        step_sql := format(
            'WITH frontier AS (
                SELECT value->>0 AS element, value->1 AS key, ordinal
                  FROM jsonb_array_elements($1) WITH ORDINALITY AS f(value, ordinal)
            )
            SELECT ordinal, edge_key, vertex_key, weight FROM (%s) AS candidates
             WHERE ($3 IS NULL OR edge_key COLLATE pg_catalog."C" > $3)
               AND ($4 IS NULL OR NOT EXISTS (
                   SELECT FROM jsonb_array_elements($4) AS excluded(key)
                    WHERE excluded.key = edge_key::jsonb))
             ORDER BY ordinal, edge_key COLLATE pg_catalog."C" LIMIT $2',
             array_to_string(branches, ' UNION ALL '));
    END IF;
    RETURN NEXT;
END
$$;

CREATE FUNCTION bluetusk_graph._visible_anchors(definition jsonb, anchors jsonb)
RETURNS SETOF text LANGUAGE plpgsql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    anchor jsonb;
BEGIN
    IF jsonb_typeof(anchors) IS DISTINCT FROM 'array' OR octet_length(anchors::text) > 65536
       OR jsonb_array_length(anchors) NOT BETWEEN 1 AND 128 THEN
        RAISE EXCEPTION 'Reachability requires one to 128 bounded anchor identities' USING ERRCODE = '22023';
    END IF;
    FOR anchor IN SELECT value FROM jsonb_array_elements(anchors) WITH ORDINALITY AS a(value, ordinal)
        ORDER BY ordinal
    LOOP
        IF jsonb_typeof(anchor) IS DISTINCT FROM 'array' OR jsonb_array_length(anchor) <> 2
           OR jsonb_typeof(anchor->0) IS DISTINCT FROM 'string'
           OR jsonb_typeof(anchor->1) IS DISTINCT FROM 'array'
           OR jsonb_array_length(anchor->1) NOT BETWEEN 1 AND 8 THEN
            RAISE EXCEPTION 'An anchor must be an [element, key tuple] identity' USING ERRCODE = '22023';
        END IF;
        RETURN QUERY EXECUTE bluetusk_graph._anchor(definition, anchor->>0) USING anchor->1;
    END LOOP;
END
$$;

CREATE FUNCTION bluetusk_graph._walk_v2(
    graph_schema text, graph_name text, start_alias text, start_key jsonb,
    direction text, operation text, target_alias text, target_key jsonb,
    max_depth integer, max_frontier integer, max_edges integer,
    max_results integer, max_bytes integer, after_edge jsonb,
    excluded_edges jsonb, weight_property text, selected_edges text[], anchors jsonb)
RETURNS jsonb AS 'MODULE_PATHNAME', 'bluetusk_graph_walk'
LANGUAGE C STABLE SECURITY INVOKER PARALLEL UNSAFE
SET search_path = pg_catalog, pg_temp;

CREATE FUNCTION bluetusk_graph.reachable_from(
    graph_schema text, graph_name text, anchors jsonb, edge_aliases text[] DEFAULT NULL,
    max_depth integer DEFAULT 8, direction text DEFAULT 'outgoing',
    max_frontier integer DEFAULT 4096, max_edges integer DEFAULT 100000,
    max_results integer DEFAULT 1000, max_bytes integer DEFAULT 8388608)
RETURNS jsonb LANGUAGE sql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
    SELECT bluetusk_graph._walk_v2(graph_schema, graph_name, NULL, NULL,
        direction, 'reachable', NULL, NULL, max_depth, max_frontier,
        max_edges, max_results, max_bytes, NULL, NULL, NULL, edge_aliases, anchors)
$$;

CREATE FUNCTION bluetusk_graph.neighbors_on(
    graph_schema text, graph_name text, start_alias text, start_key jsonb, edge_aliases text[],
    direction text DEFAULT 'outgoing', page_size integer DEFAULT 100,
    after_edge jsonb DEFAULT NULL, max_edges integer DEFAULT 10000,
    max_bytes integer DEFAULT 8388608)
RETURNS jsonb LANGUAGE sql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
    SELECT bluetusk_graph._walk_v2(graph_schema, graph_name, start_alias, start_key,
        direction, 'neighbors', NULL, NULL, 1, 1, max_edges, page_size,
        max_bytes, after_edge, NULL, NULL, edge_aliases, NULL)
$$;

CREATE FUNCTION bluetusk_graph.shortest_path_on(
    graph_schema text, graph_name text, start_alias text, start_key jsonb,
    target_alias text, target_key jsonb, edge_aliases text[], max_depth integer DEFAULT 8,
    direction text DEFAULT 'outgoing', max_frontier integer DEFAULT 4096,
    max_edges integer DEFAULT 100000, max_bytes integer DEFAULT 8388608)
RETURNS jsonb LANGUAGE sql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
    SELECT bluetusk_graph._walk_v2(graph_schema, graph_name, start_alias, start_key,
        direction, 'shortest_path', target_alias, target_key, max_depth, max_frontier,
        max_edges, 1, max_bytes, NULL, NULL, NULL, edge_aliases, NULL)
$$;

CREATE FUNCTION bluetusk_graph.weighted_shortest_path_on(
    graph_schema text, graph_name text, start_alias text, start_key jsonb,
    target_alias text, target_key jsonb, weight_property text, edge_aliases text[],
    max_depth integer DEFAULT 8, direction text DEFAULT 'outgoing',
    max_frontier integer DEFAULT 4096, max_edges integer DEFAULT 100000,
    max_bytes integer DEFAULT 8388608)
RETURNS jsonb LANGUAGE sql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
    SELECT bluetusk_graph._walk_v2(graph_schema, graph_name, start_alias, start_key,
        direction, 'weighted_shortest_path', target_alias, target_key, max_depth,
        max_frontier, max_edges, 1, max_bytes, NULL, NULL, weight_property, edge_aliases, NULL)
$$;
