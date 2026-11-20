CREATE TABLE bluetusk_graph._graphs (
    graph_schema text COLLATE "C" NOT NULL,
    graph_name text COLLATE "C" NOT NULL,
    definition jsonb NOT NULL,
    shape jsonb NOT NULL,
    PRIMARY KEY (graph_schema, graph_name)
);

SELECT pg_catalog.pg_extension_config_dump('bluetusk_graph._graphs', '');

-- Definition format: vertices and edges are objects keyed by element alias.
-- An element maps a schema, table, ordered key, and optional labels/properties.
-- Edge source/destination map a vertex alias and ordered foreign-key columns.
CREATE FUNCTION bluetusk_graph._relation(element jsonb)
RETURNS regclass LANGUAGE plpgsql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    relation regclass;
BEGIN
    relation := to_regclass(format('%I.%I', element->>'schema', element->>'table'));
    IF relation IS NULL OR NOT EXISTS (
        SELECT FROM pg_class WHERE oid = relation AND relkind IN ('r', 'p')
    ) THEN
        RAISE EXCEPTION 'Graph relation %.% is missing or is not a table',
            element->>'schema', element->>'table' USING ERRCODE = '55000';
    END IF;
    RETURN relation;
END
$$;

CREATE FUNCTION bluetusk_graph._key_types(relation regclass, columns jsonb, unique_key boolean)
RETURNS oid[] LANGUAGE plpgsql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    numbers smallint[];
    types oid[];
    all_not_null boolean;
BEGIN
    IF jsonb_typeof(columns) IS DISTINCT FROM 'array'
       OR jsonb_array_length(columns) NOT BETWEEN 1 AND 8 THEN
        RAISE EXCEPTION 'Graph relation % needs one to eight ordered key columns', relation
            USING ERRCODE = '22023';
    END IF;
    SELECT array_agg(a.attnum ORDER BY c.ordinal), array_agg(a.atttypid ORDER BY c.ordinal),
           bool_and(a.attnotnull)
      INTO numbers, types, all_not_null
      FROM jsonb_array_elements_text(columns) WITH ORDINALITY AS c(column_name, ordinal)
      JOIN pg_attribute a ON a.attrelid = relation
           AND a.attname::text COLLATE "C" = c.column_name COLLATE "C"
           AND a.attnum > 0 AND NOT a.attisdropped;
    IF cardinality(numbers) IS DISTINCT FROM jsonb_array_length(columns)
       OR cardinality(numbers) <> (SELECT count(DISTINCT i) FROM unnest(numbers) AS i)
       OR EXISTS (SELECT FROM unnest(types) AS t
                  WHERE t NOT IN ('int4'::regtype, 'int8'::regtype, 'uuid'::regtype)) THEN
        RAISE EXCEPTION 'Graph relation % has missing, duplicate, or unsupported key columns %',
            relation, columns USING ERRCODE = '22023';
    END IF;
    IF unique_key AND (NOT all_not_null OR NOT EXISTS (
        SELECT FROM pg_index i
         WHERE i.indrelid = relation AND i.indisunique AND i.indisvalid
           AND i.indimmediate AND i.indpred IS NULL AND i.indexprs IS NULL
           AND i.indnkeyatts = cardinality(numbers)
           AND NOT EXISTS (
               SELECT FROM unnest(numbers) WITH ORDINALITY AS a(attnum, ordinal)
                WHERE i.indkey[(a.ordinal - 1)::integer] <> a.attnum
           )
    )) THEN
        RAISE EXCEPTION 'Graph relation % needs a non-null, immediate unique key on %',
            relation, columns USING ERRCODE = '22023';
    END IF;
    RETURN types;
END
$$;

CREATE FUNCTION bluetusk_graph._shape(definition jsonb)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    item record;
    endpoint jsonb;
    vertex jsonb;
    relation regclass;
    types oid[];
    shape jsonb := '{}'::jsonb;
    vertex_types jsonb := '{}'::jsonb;
    element_shape jsonb;
    property text;
    property_type oid;
BEGIN
    IF jsonb_typeof(definition->'vertices') IS DISTINCT FROM 'object'
       OR definition->'vertices' = '{}'::jsonb
       OR jsonb_typeof(definition->'edges') IS DISTINCT FROM 'object'
       OR (SELECT count(*) FROM jsonb_object_keys(definition->'vertices')) > 64
       OR (SELECT count(*) FROM jsonb_object_keys(definition->'edges')) > 64
       OR EXISTS (
           SELECT FROM jsonb_object_keys(definition->'vertices') AS name
            WHERE definition->'edges' ? name
       ) THEN
        RAISE EXCEPTION 'A graph needs uniquely named vertices and edges (up to 64 of each)'
            USING ERRCODE = '22023';
    END IF;
    FOR item IN
        SELECT key AS alias, value AS element, false AS is_edge FROM jsonb_each(definition->'vertices')
        UNION ALL
        SELECT key, value, true FROM jsonb_each(definition->'edges')
        ORDER BY is_edge
    LOOP
        IF octet_length(item.alias) NOT BETWEEN 1 AND 128
           OR coalesce(jsonb_typeof(item.element->'labels'), 'array') <> 'array'
           OR coalesce(jsonb_typeof(item.element->'properties'), 'array') <> 'array' THEN
            RAISE EXCEPTION 'Invalid graph element %', item.alias USING ERRCODE = '22023';
        END IF;
        relation := bluetusk_graph._relation(item.element);
        types := bluetusk_graph._key_types(relation, item.element->'key', true);
        element_shape := jsonb_build_object('key', types, 'properties', '{}'::jsonb);
        FOR property IN SELECT jsonb_array_elements_text(item.element->'properties')
        LOOP
            SELECT a.atttypid INTO property_type
              FROM pg_attribute a WHERE a.attrelid = relation AND a.attnum > 0
               AND NOT a.attisdropped AND a.attname::text COLLATE "C" = property COLLATE "C";
            IF NOT FOUND THEN
                RAISE EXCEPTION 'Graph element % exposes missing column %', item.alias, property
                    USING ERRCODE = '22023';
            END IF;
            element_shape := jsonb_set(element_shape, ARRAY['properties', property],
                to_jsonb(property_type::regtype::text));
        END LOOP;
        IF item.is_edge THEN
            FOREACH endpoint IN ARRAY ARRAY[item.element->'source', item.element->'destination']
            LOOP
                vertex := definition->'vertices'->(endpoint->>'vertex');
                IF vertex IS NULL THEN
                    RAISE EXCEPTION 'Edge % references an unknown vertex', item.alias
                        USING ERRCODE = '22023';
                END IF;
                types := bluetusk_graph._key_types(relation, endpoint->'columns', false);
                IF to_jsonb(types) <> vertex_types->(endpoint->>'vertex') THEN
                    RAISE EXCEPTION 'Edge % endpoint % disagrees with its vertex key', item.alias, endpoint
                        USING ERRCODE = '22023';
                END IF;
                element_shape := element_shape || jsonb_build_object('endpoint:' || endpoint::text, types);
            END LOOP;
        ELSE
            vertex_types := vertex_types || jsonb_build_object(item.alias, types);
        END IF;
        shape := shape || jsonb_build_object(item.alias, element_shape);
    END LOOP;
    RETURN shape;
END
$$;

CREATE FUNCTION bluetusk_graph.register_graph(graph_schema text, graph_name text, definition jsonb)
RETURNS void LANGUAGE plpgsql SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF graph_schema IS NULL OR octet_length(graph_schema) NOT BETWEEN 1 AND 128
       OR graph_name IS NULL OR octet_length(graph_name) NOT BETWEEN 1 AND 128 THEN
        RAISE EXCEPTION 'Graph schema and name are required (up to 128 bytes)'
            USING ERRCODE = '22023';
    END IF;
    INSERT INTO bluetusk_graph._graphs AS graphs VALUES (
        graph_schema, graph_name, definition, bluetusk_graph._shape(definition)
    )
    ON CONFLICT ON CONSTRAINT _graphs_pkey DO UPDATE
        SET definition = excluded.definition, shape = excluded.shape;
END
$$;

REVOKE ALL ON FUNCTION bluetusk_graph.register_graph(text, text, jsonb) FROM PUBLIC;

CREATE FUNCTION bluetusk_graph._identities(prefix text, columns jsonb)
RETURNS text LANGUAGE sql IMMUTABLE STRICT
SET search_path = pg_catalog, pg_temp AS $$
    SELECT string_agg(format('%s.%I', prefix, column_name), ', ' ORDER BY ordinal)
      FROM jsonb_array_elements_text(columns) WITH ORDINALITY AS k(column_name, ordinal)
$$;

CREATE FUNCTION bluetusk_graph._key_match(
    prefix text, columns jsonb, types oid[], key_sql text)
RETURNS text LANGUAGE sql STABLE STRICT
SET search_path = pg_catalog, pg_temp AS $$
    SELECT string_agg(format('%s.%I = (%s->>%s)::%s',
               prefix, column_name, key_sql, ordinal - 1, types[ordinal]::regtype),
               ' AND ' ORDER BY ordinal)
      FROM jsonb_array_elements_text(columns) WITH ORDINALITY AS k(column_name, ordinal)
$$;

CREATE FUNCTION bluetusk_graph._anchor(definition jsonb, vertex_alias text)
RETURNS text LANGUAGE plpgsql STABLE STRICT
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    vertex jsonb := definition->'vertices'->vertex_alias;
    types oid[];
BEGIN
    IF vertex IS NULL THEN
        RAISE EXCEPTION 'Unknown graph vertex %', vertex_alias USING ERRCODE = '22023';
    END IF;
    types := bluetusk_graph._key_types(bluetusk_graph._relation(vertex), vertex->'key', true);
    RETURN format(
        'SELECT jsonb_build_array(%L::text, jsonb_build_array(%s))::text
           FROM %I.%I v WHERE jsonb_array_length($1) = %s AND %s',
        vertex_alias, bluetusk_graph._identities('v', vertex->'key'),
        vertex->>'schema', vertex->>'table', cardinality(types),
        bluetusk_graph._key_match('v', vertex->'key', types, '$1'));
END
$$;

CREATE FUNCTION bluetusk_graph._plan(
    requested_schema text, requested_graph text, start_alias text, target_alias text,
    direction text, weight_property text)
RETURNS TABLE (anchor_sql text, target_sql text, step_sql text)
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
    anchor_sql := bluetusk_graph._anchor(definition, start_alias);
    target_sql := bluetusk_graph._anchor(definition, target_alias);
    FOR item IN SELECT key AS alias, value AS edge FROM jsonb_each(definition->'edges')
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

CREATE FUNCTION bluetusk_graph._walk(
    graph_schema text, graph_name text, start_alias text, start_key jsonb,
    direction text, operation text, target_alias text, target_key jsonb,
    max_depth integer, max_frontier integer, max_edges integer,
    max_results integer, max_bytes integer, after_edge jsonb,
    excluded_edges jsonb DEFAULT NULL, weight_property text DEFAULT NULL)
RETURNS jsonb AS 'MODULE_PATHNAME', 'bluetusk_graph_walk'
LANGUAGE C STABLE SECURITY INVOKER PARALLEL UNSAFE
SET search_path = pg_catalog, pg_temp;

CREATE FUNCTION bluetusk_graph.neighbors(
    graph_schema text, graph_name text, start_alias text, start_key jsonb,
    direction text DEFAULT 'outgoing', page_size integer DEFAULT 100,
    after_edge jsonb DEFAULT NULL, max_edges integer DEFAULT 10000,
    max_bytes integer DEFAULT 8388608)
RETURNS jsonb LANGUAGE sql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
    SELECT bluetusk_graph._walk(graph_schema, graph_name, start_alias, start_key,
        direction, 'neighbors', NULL, NULL, 1, 1, max_edges, page_size, max_bytes, after_edge)
$$;

CREATE FUNCTION bluetusk_graph.reachable(
    graph_schema text, graph_name text, start_alias text, start_key jsonb,
    max_depth integer DEFAULT 8, direction text DEFAULT 'outgoing',
    max_frontier integer DEFAULT 4096, max_edges integer DEFAULT 100000,
    max_results integer DEFAULT 1000, max_bytes integer DEFAULT 8388608)
RETURNS jsonb LANGUAGE sql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
    SELECT bluetusk_graph._walk(graph_schema, graph_name, start_alias, start_key,
        direction, 'reachable', NULL, NULL, max_depth, max_frontier, max_edges,
        max_results, max_bytes, NULL)
$$;

CREATE FUNCTION bluetusk_graph.shortest_path(
    graph_schema text, graph_name text, start_alias text, start_key jsonb,
    target_alias text, target_key jsonb, max_depth integer DEFAULT 8,
    direction text DEFAULT 'outgoing', max_frontier integer DEFAULT 4096,
    max_edges integer DEFAULT 100000, max_bytes integer DEFAULT 8388608)
RETURNS jsonb LANGUAGE sql STABLE SECURITY INVOKER
SET search_path = pg_catalog, pg_temp AS $$
    SELECT bluetusk_graph._walk(graph_schema, graph_name, start_alias, start_key,
        direction, 'shortest_path', target_alias, target_key, max_depth, max_frontier,
        max_edges, 1, max_bytes, NULL)
$$;

GRANT USAGE ON SCHEMA bluetusk_graph TO PUBLIC;
GRANT SELECT ON bluetusk_graph._graphs TO PUBLIC;
