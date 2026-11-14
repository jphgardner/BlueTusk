\set ON_ERROR_STOP on
\if :{?edge_count}
\else
    \set edge_count 100000
\endif

CREATE EXTENSION IF NOT EXISTS bluetusk_graph;
CREATE SCHEMA graph_bench;
CREATE TABLE graph_bench.vertex (id bigint PRIMARY KEY);
CREATE TABLE graph_bench.edge (id bigint PRIMARY KEY, source bigint NOT NULL, destination bigint NOT NULL);
CREATE INDEX ON graph_bench.edge (source);
CREATE INDEX ON graph_bench.edge (destination);

INSERT INTO graph_bench.vertex
SELECT id FROM generate_series(1, greatest(64, :edge_count / 8)) AS id;
INSERT INTO graph_bench.edge
SELECT id, 1 + ((id - 1) % vertex_count),
       1 + ((id - 1 + 1 + (id - 1) / vertex_count) % vertex_count)
FROM generate_series(1, :edge_count) AS id
CROSS JOIN (SELECT greatest(64, :edge_count / 8) AS vertex_count) AS sizes;
ANALYZE graph_bench.vertex;
ANALYZE graph_bench.edge;

DO $$
BEGIN
    PERFORM bluetusk_graph.register_graph('graph_bench', 'topology', '{
        "vertices":{"v":{"schema":"graph_bench","table":"vertex","key":["id"]}},
        "edges":{"e":{"schema":"graph_bench","table":"edge","key":["id"],
            "source":{"vertex":"v","columns":["source"]},
            "destination":{"vertex":"v","columns":["destination"]}}}
    }');
END
$$;

CREATE FUNCTION graph_bench.reference(max_depth integer)
RETURNS jsonb LANGUAGE sql STABLE AS $$
    WITH RECURSIVE walk(id, hops) AS (
        SELECT 1::bigint, 0
        UNION
        SELECT e.destination, walk.hops + 1
          FROM walk JOIN graph_bench.edge e ON e.source = walk.id
                    JOIN graph_bench.vertex v ON v.id = e.destination
         WHERE walk.hops < max_depth
    ), distances AS (
        SELECT id, min(hops) AS hops FROM walk WHERE id <> 1 GROUP BY id
    )
    SELECT coalesce(jsonb_object_agg(id, hops), '{}'::jsonb) FROM distances
$$;

CREATE FUNCTION graph_bench.native(max_depth integer)
RETURNS jsonb LANGUAGE sql STABLE AS $$
    SELECT coalesce(jsonb_object_agg(row->'vertex'->1->>0, row->'hops'), '{}'::jsonb)
      FROM jsonb_array_elements(bluetusk_graph.reachable('graph_bench','topology','v','[1]',
          max_depth => max_depth, max_results => 10000, max_edges => 1000000)->'rows') AS row
$$;

DO $$
BEGIN
    IF graph_bench.reference(1) <> graph_bench.native(1)
       OR graph_bench.reference(32) <> graph_bench.native(32)
       OR graph_bench.reference(1) <> (
           SELECT jsonb_object_agg(e.destination, 1)
           FROM graph_bench.edge e JOIN graph_bench.vertex v ON v.id = e.destination
           WHERE e.source = 1 AND e.destination <> 1
       ) THEN
        RAISE EXCEPTION 'Benchmark strategies returned different results';
    END IF;
END
$$;

CREATE TEMP TABLE graph_measurement (strategy text, depth integer, milliseconds double precision);
DO $$
DECLARE
    iteration integer;
    depth integer;
    started timestamptz;
BEGIN
    FOR iteration IN 1..85 LOOP
        FOREACH depth IN ARRAY ARRAY[1,32] LOOP
            started := clock_timestamp();
            PERFORM graph_bench.reference(depth);
            IF iteration > 5 THEN
                INSERT INTO graph_measurement VALUES ('recursive_sql', depth,
                    extract(epoch FROM clock_timestamp() - started) * 1000);
            END IF;
            started := clock_timestamp();
            PERFORM graph_bench.native(depth);
            IF iteration > 5 THEN
                INSERT INTO graph_measurement VALUES ('native_frontier', depth,
                    extract(epoch FROM clock_timestamp() - started) * 1000);
            END IF;
        END LOOP;
        started := clock_timestamp();
        PERFORM jsonb_object_agg(e.destination, 1)
          FROM graph_bench.edge e JOIN graph_bench.vertex v ON v.id = e.destination
         WHERE e.source = 1 AND e.destination <> 1;
        IF iteration > 5 THEN
            INSERT INTO graph_measurement VALUES ('indexed_join', 1,
                extract(epoch FROM clock_timestamp() - started) * 1000);
        END IF;
    END LOOP;
END
$$;

\if :{?strategy_experiments}
CREATE FUNCTION graph_bench.bounded_path(target bigint, depth_limit integer, meet_in_middle boolean)
RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
DECLARE
    forward jsonb;
    backward jsonb;
    shortest integer;
BEGIN
    IF meet_in_middle THEN
        forward := bluetusk_graph.reachable('graph_bench','topology','v','[1]',
            max_depth => depth_limit / 2, max_results => 10000);
        backward := bluetusk_graph.reachable('graph_bench','topology','v',jsonb_build_array(target),
            max_depth => depth_limit - depth_limit / 2, direction => 'incoming', max_results => 10000);
        IF forward->>'status' = 'complete' AND backward->>'status' = 'complete' THEN
            WITH a(id, hops) AS (
                SELECT 1::bigint, 0
                UNION ALL SELECT (row->'vertex'->1->>0)::bigint, (row->>'hops')::integer
                  FROM jsonb_array_elements(forward->'rows') AS row
            ), b(id, hops) AS (
                SELECT target, 0
                UNION ALL SELECT (row->'vertex'->1->>0)::bigint, (row->>'hops')::integer
                  FROM jsonb_array_elements(backward->'rows') AS row
            )
            SELECT min(a.hops + b.hops) INTO shortest FROM a JOIN b USING (id);
            IF shortest IS NULL THEN
                RETURN 'null'::jsonb;
            END IF;
        END IF;
    END IF;
    -- Recover the canonical witness with the same walk and a proven bound.
    RETURN bluetusk_graph.shortest_path('graph_bench','topology','v','[1]','v',
        jsonb_build_array(target), max_depth => coalesce(shortest, depth_limit))->'path';
END
$$;

DO $$
DECLARE
    target bigint := least(100, (SELECT max(id) FROM graph_bench.vertex));
    iteration integer;
    started timestamptz;
    native_result jsonb;
    middle_result jsonb;
BEGIN
    FOR iteration IN 1..22 LOOP
        started := clock_timestamp();
        native_result := graph_bench.bounded_path(target, 32, false);
        IF iteration > 2 THEN
            INSERT INTO graph_measurement VALUES ('unidirectional_path', 32,
                extract(epoch FROM clock_timestamp() - started) * 1000);
        END IF;
        started := clock_timestamp();
        middle_result := graph_bench.bounded_path(target, 32, true);
        IF iteration > 2 THEN
            INSERT INTO graph_measurement VALUES ('bidirectional_bound', 32,
                extract(epoch FROM clock_timestamp() - started) * 1000);
        END IF;
        IF native_result IS DISTINCT FROM middle_result THEN
            RAISE EXCEPTION 'Bidirectional bound changed the canonical shortest path';
        END IF;
    END LOOP;
END
$$;
\endif

SELECT :edge_count AS edge_count, strategy, depth, count(*) AS samples,
       percentile_cont(0.95) WITHIN GROUP (ORDER BY milliseconds) AS p95_ms,
       percentile_cont(0.99) WITHIN GROUP (ORDER BY milliseconds) AS p99_ms
FROM graph_measurement GROUP BY strategy, depth ORDER BY depth, strategy;
