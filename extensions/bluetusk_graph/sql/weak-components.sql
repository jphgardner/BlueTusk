-- Lab workload for batch analytics. Each component has cycles and reverse paths.
BEGIN ISOLATION LEVEL REPEATABLE READ;
CREATE EXTENSION IF NOT EXISTS bluetusk_graph;
CREATE SCHEMA graph_components;
CREATE TABLE graph_components.vertex (id bigint PRIMARY KEY);
CREATE TABLE graph_components.edge (
    id bigint PRIMARY KEY, source bigint NOT NULL, destination bigint NOT NULL);
CREATE INDEX ON graph_components.edge(source);
CREATE INDEX ON graph_components.edge(destination);

INSERT INTO graph_components.vertex SELECT id FROM generate_series(0, 3071) AS id;
INSERT INTO graph_components.edge
SELECT id, id, (id / 32) * 32 + ((id + 1) % 32)
FROM graph_components.vertex;
ANALYZE graph_components.vertex;
ANALYZE graph_components.edge;

DO $$
BEGIN
    PERFORM bluetusk_graph.register_graph('graph_components', 'batch', '{
        "vertices":{"v":{"schema":"graph_components","table":"vertex","key":["id"]}},
        "edges":{"e":{"schema":"graph_components","table":"edge","key":["id"],
            "source":{"vertex":"v","columns":["source"]},
            "destination":{"vertex":"v","columns":["destination"]}}}
    }');
END
$$;

CREATE TEMP TABLE graph_component_reference (id bigint PRIMARY KEY, component bigint) ON COMMIT DROP;
CREATE TEMP TABLE graph_component_native (LIKE graph_component_reference INCLUDING ALL) ON COMMIT DROP;
CREATE TEMP TABLE graph_component_duration (strategy text, milliseconds double precision) ON COMMIT DROP;

DO $$
DECLARE
    root bigint;
    result jsonb;
    started timestamptz;
BEGIN
    started := clock_timestamp();
    WITH RECURSIVE adjacency(a, b) AS (
        SELECT source, destination FROM graph_components.edge
        UNION ALL SELECT destination, source FROM graph_components.edge
    ), reachable(root_id, id) AS (
        SELECT id, id FROM graph_components.vertex
        UNION
        SELECT reachable.root_id, adjacency.b FROM reachable JOIN adjacency ON adjacency.a = reachable.id
    )
    INSERT INTO graph_component_reference SELECT id, min(root_id) FROM reachable GROUP BY id;
    INSERT INTO graph_component_duration VALUES ('recursive_sql',
        extract(epoch FROM clock_timestamp() - started) * 1000);

    started := clock_timestamp();
    FOR root IN SELECT id FROM graph_components.vertex ORDER BY id LOOP
        CONTINUE WHEN EXISTS (SELECT FROM graph_component_native WHERE id = root);
        result := bluetusk_graph.reachable('graph_components', 'batch', 'v',
            jsonb_build_array(root), max_depth => 64, direction => 'undirected');
        IF result->>'status' <> 'complete' THEN
            RAISE EXCEPTION 'Component traversal did not complete: %', result;
        END IF;
        INSERT INTO graph_component_native VALUES (root, root);
        INSERT INTO graph_component_native
        SELECT (row->'vertex'->1->>0)::bigint, root FROM jsonb_array_elements(result->'rows') AS row;
    END LOOP;
    INSERT INTO graph_component_duration VALUES ('native_frontier',
        extract(epoch FROM clock_timestamp() - started) * 1000);

    IF EXISTS (TABLE graph_component_reference EXCEPT TABLE graph_component_native)
       OR EXISTS (TABLE graph_component_native EXCEPT TABLE graph_component_reference)
       OR (SELECT count(DISTINCT component) FROM graph_component_native) <> 96 THEN
        RAISE EXCEPTION 'Weak connectivity did not match the recursive SQL reference';
    END IF;
END
$$;

TABLE graph_component_duration;
ROLLBACK;
