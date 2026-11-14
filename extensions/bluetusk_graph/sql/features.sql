CREATE EXTENSION IF NOT EXISTS bluetusk_graph VERSION '1.1';
ALTER EXTENSION bluetusk_graph UPDATE TO '1.2';

CREATE SCHEMA graph_features;
CREATE TABLE graph_features.node (id integer PRIMARY KEY, tenant text NOT NULL DEFAULT 'a');
CREATE TABLE graph_features.dependency (
    id integer PRIMARY KEY, source integer NOT NULL, target integer NOT NULL, cost bigint NOT NULL);
CREATE TABLE graph_features.shortcut (LIKE graph_features.dependency INCLUDING ALL);
CREATE INDEX ON graph_features.dependency(source);
CREATE INDEX ON graph_features.dependency(target);
CREATE INDEX ON graph_features.shortcut(source);
CREATE INDEX ON graph_features.shortcut(target);

INSERT INTO graph_features.node(id) SELECT id FROM generate_series(1, 8) AS id;
UPDATE graph_features.node SET tenant = 'b' WHERE id = 7;
INSERT INTO graph_features.dependency VALUES (11, 1, 3, 2), (12, 2, 3, 3), (13, 3, 4, 3), (14, 1, 7, 1), (15, 7, 8, 1);
INSERT INTO graph_features.shortcut VALUES (21, 1, 4, 20), (22, 1, 5, 30);

DO $$
BEGIN
    PERFORM bluetusk_graph.register_graph('graph_features', 'topology', '{
        "vertices":{"service":{"schema":"graph_features","table":"node","key":["id"]}},
        "edges":{
            "dependency":{"schema":"graph_features","table":"dependency","key":["id"],"properties":["cost"],
                "source":{"vertex":"service","columns":["source"]},
                "destination":{"vertex":"service","columns":["target"]}},
            "shortcut":{"schema":"graph_features","table":"shortcut","key":["id"],"properties":["cost"],
                "source":{"vertex":"service","columns":["source"]},
                "destination":{"vertex":"service","columns":["target"]}}
        }
    }');
END
$$;

CREATE ROLE graph_features_reader;
GRANT USAGE ON SCHEMA graph_features TO graph_features_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA graph_features TO graph_features_reader;
ALTER TABLE graph_features.node ENABLE ROW LEVEL SECURITY;
CREATE POLICY visible_nodes ON graph_features.node TO graph_features_reader USING (tenant = 'a');
SET ROLE graph_features_reader;

DO $$
DECLARE
    result jsonb;
    cursor jsonb;
BEGIN
    result := bluetusk_graph.reachable_from('graph_features','topology',
        '[["service",[2]],["service",[1]],["service",[2]],["service",[7]]]', ARRAY['dependency']);
    IF result->>'status' IS DISTINCT FROM 'complete' OR result->'work'->>'edges' IS DISTINCT FROM '3'
       OR result->'rows' IS DISTINCT FROM '[
        {"vertex":["service",[3]],"hops":1,"anchor":["service",[2]]},
        {"vertex":["service",[4]],"hops":2,"anchor":["service",[2]]}
    ]'::jsonb THEN
        RAISE EXCEPTION 'Multi-anchor traversal lost input order, minimum distance, or authorization: %', result;
    END IF;
    result := bluetusk_graph.reachable_from('graph_features','topology',
        '[["service",[1]],["service",[2]]]', ARRAY['dependency'], max_frontier => 1);
    IF result->>'reason' IS DISTINCT FROM 'frontier' OR result->'rows' IS DISTINCT FROM '[]'::jsonb THEN
        RAISE EXCEPTION 'Combined starting frontier exceeded the budget: %', result;
    END IF;
    result := bluetusk_graph.reachable_from('graph_features','topology',
        '[["service",[1]],["service",[2]]]', ARRAY['dependency'], max_edges => 2);
    IF result->>'reason' IS DISTINCT FROM 'edges' THEN
        RAISE EXCEPTION 'Anchors did not share one edge budget: %', result;
    END IF;
    result := bluetusk_graph.reachable_from('graph_features','topology',
        '[["service",[1]]]', ARRAY[]::text[]);
    IF result->>'status' IS DISTINCT FROM 'complete' OR result->'rows' IS DISTINCT FROM '[]'::jsonb THEN
        RAISE EXCEPTION 'Empty edge filter traversed edges: %', result;
    END IF;

    result := bluetusk_graph.neighbors_on('graph_features','topology','service','[1]',ARRAY['shortcut'],
        page_size => 1);
    cursor := result->'next';
    IF cursor IS DISTINCT FROM '["shortcut",[21]]'::jsonb THEN
        RAISE EXCEPTION 'Wrong filtered pagination cursor: %', result;
    END IF;
    result := bluetusk_graph.neighbors_on('graph_features','topology','service','[1]',ARRAY['shortcut'],
        page_size => 1, after_edge => cursor);
    IF result->'rows' IS DISTINCT FROM '[{"edge":["shortcut",[22]],"vertex":["service",[5]]}]'::jsonb
       OR result->'next' IS DISTINCT FROM 'null'::jsonb THEN
        RAISE EXCEPTION 'Filtered neighbor page was incorrect: %', result;
    END IF;

    result := bluetusk_graph.shortest_path_on('graph_features','topology','service','[1]','service','[4]',
        ARRAY['dependency']);
    IF result->'path'->'edges' IS DISTINCT FROM '[["dependency",[11]],["dependency",[13]]]'::jsonb THEN
        RAISE EXCEPTION 'Filtered path incorrectly used a shortcut: %', result;
    END IF;
    result := bluetusk_graph.weighted_shortest_path_on('graph_features','topology','service','[1]','service','[4]',
        'cost', ARRAY['shortcut']);
    IF result->'path'->'cost' IS DISTINCT FROM '20'::jsonb THEN
        RAISE EXCEPTION 'Filtered weighted path did not use its selected edge: %', result;
    END IF;

    BEGIN
        PERFORM bluetusk_graph.neighbors_on('graph_features','topology','service','[1]',ARRAY['misspelled']);
        RAISE EXCEPTION 'Unknown edge alias was accepted';
    EXCEPTION WHEN invalid_parameter_value THEN NULL;
    END;
    BEGIN
        PERFORM bluetusk_graph.reachable_from('graph_features','topology','[["service",[]]]');
        RAISE EXCEPTION 'Malformed multi-anchor key was accepted';
    EXCEPTION WHEN invalid_parameter_value THEN NULL;
    END;
END
$$;
RESET ROLE;
