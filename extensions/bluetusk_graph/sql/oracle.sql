-- A distinct recursive SQL oracle on a graph with hubs, cycles and parallel edges.
CREATE TABLE graph_oracle_vertex (tenant integer, id uuid, PRIMARY KEY (tenant, id));
CREATE TABLE graph_oracle_edge (
    id bigint PRIMARY KEY, tenant integer NOT NULL, source uuid NOT NULL, destination uuid NOT NULL);
CREATE INDEX ON graph_oracle_edge (tenant, source);
CREATE INDEX ON graph_oracle_edge (tenant, destination);

INSERT INTO graph_oracle_vertex
SELECT tenant, md5(id::text)::uuid FROM generate_series(1, 2) AS tenant, generate_series(1, 32) AS id;
INSERT INTO graph_oracle_edge
SELECT id, 1, md5((1 + id % 28)::text)::uuid, md5((1 + (id * id + id / 5) % 28)::text)::uuid
FROM generate_series(1, 128) AS id;

DO $$
BEGIN
    PERFORM bluetusk_graph.register_graph('public', 'oracle', '{
      "vertices":{"v":{"schema":"public","table":"graph_oracle_vertex","key":["tenant","id"]}},
      "edges":{"e":{"schema":"public","table":"graph_oracle_edge","key":["id"],
          "source":{"vertex":"v","columns":["tenant","source"]},
          "destination":{"vertex":"v","columns":["tenant","destination"]}}}
    }');
END
$$;

DO $$
DECLARE
    anchor uuid;
    direction text;
    depth_limit integer;
    expected jsonb;
    got jsonb;
    actual jsonb;
BEGIN
    FOR anchor IN SELECT id FROM graph_oracle_vertex WHERE tenant = 1
    LOOP
        FOREACH direction IN ARRAY ARRAY['outgoing','incoming','undirected']
        LOOP
            FOREACH depth_limit IN ARRAY ARRAY[0, 1, 3, 8]
            LOOP
                WITH RECURSIVE adjacency(source, destination) AS (
                    SELECT e.source, e.destination FROM graph_oracle_edge e
                     WHERE tenant = 1 AND direction <> 'incoming'
                    UNION ALL
                    SELECT e.destination, e.source FROM graph_oracle_edge e
                     WHERE tenant = 1 AND direction <> 'outgoing'
                ), walk(id, depth) AS (
                    SELECT anchor, 0
                    UNION
                    SELECT a.destination, w.depth + 1 FROM walk w JOIN adjacency a ON a.source = w.id
                     WHERE w.depth < depth_limit
                ), distances AS (
                    SELECT id, min(depth) AS hops FROM walk WHERE id <> anchor GROUP BY id
                )
                SELECT coalesce(jsonb_object_agg(id::text, hops), '{}'::jsonb)
                  INTO expected FROM distances;
                got := bluetusk_graph.reachable('public', 'oracle', 'v', jsonb_build_array(1, anchor),
                    depth_limit, direction);
                SELECT coalesce(jsonb_object_agg(row->'vertex'->1->>1, row->'hops'), '{}'::jsonb)
                  INTO actual FROM jsonb_array_elements(got->'rows') AS row;
                IF got->>'status' <> 'complete' OR expected <> actual THEN
                    RAISE EXCEPTION 'Oracle mismatch at %, %, %: expected %, got %',
                        anchor, direction, depth_limit, expected, got;
                END IF;
            END LOOP;
        END LOOP;
    END LOOP;
END
$$;

-- A long path must survive closing SPI and retain correctly accounted work memory.
CREATE TABLE graph_long_vertex (id bigint PRIMARY KEY);
CREATE TABLE graph_long_edge (id bigint PRIMARY KEY, source bigint NOT NULL, destination bigint NOT NULL);
CREATE INDEX ON graph_long_edge(source);
CREATE INDEX ON graph_long_edge(destination);
INSERT INTO graph_long_vertex SELECT id FROM generate_series(1, 1025) AS id;
INSERT INTO graph_long_edge
SELECT source * 16 + offset_by, source, source + offset_by
FROM generate_series(1, 1025) AS source
CROSS JOIN generate_series(1, 16) AS offset_by
WHERE source + offset_by <= 1025;
ANALYZE graph_long_vertex;
ANALYZE graph_long_edge;

DO $$
DECLARE
    result jsonb;
    sample integer;
BEGIN
    PERFORM bluetusk_graph.register_graph('public', 'long_path', '{
        "vertices":{"v":{"schema":"public","table":"graph_long_vertex","key":["id"]}},
        "edges":{"e":{"schema":"public","table":"graph_long_edge","key":["id"],
            "source":{"vertex":"v","columns":["source"]},
            "destination":{"vertex":"v","columns":["destination"]}}}
    }');
    FOR sample IN 1..10 LOOP
        result := bluetusk_graph.shortest_path('public','long_path','v','[1]','v','[513]',64);
        IF result->>'status' <> 'complete'
           OR jsonb_array_length(result->'path'->'edges') IS DISTINCT FROM 32
           OR result->'path'->'vertices'->32 IS DISTINCT FROM '["v",[513]]'::jsonb THEN
            RAISE EXCEPTION 'Long-path state was lost: %', result;
        END IF;
    END LOOP;
    result := bluetusk_graph.reachable('public','long_path','v','[1]',64,
        max_results => 10000, max_bytes => 65536);
    IF result->>'reason' IS DISTINCT FROM 'memory' THEN
        RAISE EXCEPTION 'The memory budget did not account for walk state: %', result;
    END IF;
END
$$;

-- Generate malformed SQL arguments and require recoverable PostgreSQL errors.
DO $$
DECLARE
    sample integer;
    input_key jsonb;
    input_depth integer;
    input_direction text;
BEGIN
    FOR sample IN 1..128 LOOP
        input_key := jsonb_build_array(1, md5(sample::text)::uuid);
        input_depth := 2;
        input_direction := 'outgoing';
        CASE sample % 4
            WHEN 0 THEN input_key := jsonb_build_object('id', sample);
            WHEN 1 THEN input_key := jsonb_build_array(1, 'invalid-uuid-' || sample::text);
            WHEN 2 THEN input_depth := 64 + sample;
            WHEN 3 THEN input_direction := 'invalid-' || md5(sample::text);
        END CASE;

        BEGIN
            PERFORM bluetusk_graph.reachable('public', 'oracle', 'v', input_key,
                input_depth, input_direction);
            RAISE EXCEPTION 'Malformed traversal sample % was accepted', sample;
        EXCEPTION WHEN invalid_parameter_value OR invalid_text_representation THEN
            NULL;
        END;
    END LOOP;
END
$$;
