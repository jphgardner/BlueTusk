CREATE EXTENSION bluetusk_graph VERSION '1.0';
CREATE TABLE topology_node (id bigint PRIMARY KEY, tenant text NOT NULL);
CREATE TABLE topology_edge (
    id bigint PRIMARY KEY,
    source bigint NOT NULL REFERENCES topology_node(id),
    destination bigint NOT NULL REFERENCES topology_node(id),
    tenant text NOT NULL,
    cost bigint NOT NULL DEFAULT 1 CHECK (cost >= 0)
);
CREATE INDEX ON topology_edge (source);
CREATE INDEX ON topology_edge (destination);

INSERT INTO topology_node VALUES (1,'a'), (2,'a'), (3,'a'), (4,'a'), (5,'b'), (6,'a');
INSERT INTO topology_edge VALUES
    (11,1,2,'a'), (12,1,3,'a'), (13,2,4,'a'), (14,3,4,'a'), (15,4,1,'a'),
    (16,1,2,'a'), (17,1,1,'a'), (18,4,5,'a'), (19,5,6,'a');
UPDATE topology_edge SET cost = 10 WHERE id = 13;

DO $$
BEGIN
    PERFORM bluetusk_graph.register_graph('public', 'topology', '{
      "vertices":{"services":{"schema":"public","table":"topology_node","key":["id"],"labels":["service"],"properties":["tenant"]}},
      "edges":{"depends":{"schema":"public","table":"topology_edge","key":["id"],"properties":["cost"],
          "source":{"vertex":"services","columns":["source"]},
          "destination":{"vertex":"services","columns":["destination"]}}}
    }');
END
$$;

ALTER EXTENSION bluetusk_graph UPDATE TO '1.1';

DO $$
DECLARE
    result jsonb;
    cursor jsonb;
BEGIN
    result := bluetusk_graph.reachable('public', 'topology', 'services', '[1]');
    IF result->>'status' <> 'complete' OR result->'rows' <> '[
        {"vertex":["services",[2]],"hops":1},{"vertex":["services",[3]],"hops":1},
        {"vertex":["services",[4]],"hops":2},{"vertex":["services",[5]],"hops":3},
        {"vertex":["services",[6]],"hops":4}]'::jsonb THEN
        RAISE EXCEPTION 'Invalid reachability: %', result;
    END IF;

    result := bluetusk_graph.shortest_path('public','topology','services','[1]','services','[4]');
    IF result->'path'->'edges' <> '[["depends",[11]],["depends",[13]]]'::jsonb THEN
        RAISE EXCEPTION 'Shortest path lost its stable tie break: %', result;
    END IF;
    IF bluetusk_graph.shortest_path('public','topology','services','[1]','services','[1]',0)
        ->'path'->'edges' <> '[]'::jsonb THEN
        RAISE EXCEPTION 'Expected zero-hop path from a vertex to itself';
    END IF;
    result := bluetusk_graph.weighted_shortest_path('public','topology','services','[1]',
        'services','[4]','cost',2);
    IF result->'path'->'edges' <> '[["depends",[12]],["depends",[14]]]'::jsonb OR
       result->'path'->'cost' <> '2'::jsonb THEN
        RAISE EXCEPTION 'Weighted traversal chose the wrong path: %', result;
    END IF;

    result := bluetusk_graph.neighbors('public','topology','services','[1]','undirected', 3);
    cursor := result->'next';
    IF cursor <> '["depends",[15]]'::jsonb OR
       jsonb_array_length(result->'rows') <> 3 THEN
        RAISE EXCEPTION 'Unexpected first page: %', result;
    END IF;
    result := bluetusk_graph.neighbors('public','topology','services','[1]','undirected', 3, cursor);
    IF result->'rows' <> '[
        {"edge":["depends",[16]],"vertex":["services",[2]]},
        {"edge":["depends",[17]],"vertex":["services",[1]]}]'::jsonb
       OR result->'next' <> 'null'::jsonb THEN
        RAISE EXCEPTION 'Parallel edge or self-loop lost across pages: %', result;
    END IF;

    result := bluetusk_graph.reachable('public','topology','services','[1]', max_edges => 1);
    IF result->>'status' <> 'budget_exhausted' OR result->>'reason' <> 'edges'
       OR result->'rows' <> '[]'::jsonb THEN
        RAISE EXCEPTION 'Expected an explicit edge-budget failure: %', result;
    END IF;
    result := bluetusk_graph.reachable('public','topology','services','[1]', max_frontier => 1);
    IF result->>'reason' <> 'frontier' THEN
        RAISE EXCEPTION 'Expected a frontier-budget failure: %', result;
    END IF;
    result := bluetusk_graph.reachable('public','topology','services','[1]', max_results => 1);
    IF result->>'reason' <> 'results' THEN
        RAISE EXCEPTION 'Expected a result-budget failure: %', result;
    END IF;

    result := bluetusk_graph.compare_reachability('public','topology','services','[1]',
        '[["depends",[13]],["depends",[14]]]');
    IF jsonb_array_length(result->'baseline'->'rows') <> 5 OR
       jsonb_array_length(result->'scenario'->'rows') <> 2 THEN
        RAISE EXCEPTION 'Scenario did not isolate the removed dependencies: %', result;
    END IF;
END
$$;

CREATE ROLE graph_reader;
GRANT SELECT ON topology_node, topology_edge TO graph_reader;
ALTER TABLE topology_node ENABLE ROW LEVEL SECURITY;
ALTER TABLE topology_edge ENABLE ROW LEVEL SECURITY;
CREATE POLICY topology_visible_nodes ON topology_node TO graph_reader USING (tenant = 'a');
CREATE POLICY topology_visible_edges ON topology_edge TO graph_reader USING (tenant = 'a');
SET ROLE graph_reader;

DO $$
DECLARE
    result jsonb;
BEGIN
    result := bluetusk_graph.shortest_path('public','topology','services','[1]','services','[6]');
    IF result->'path' <> 'null'::jsonb THEN
        RAISE EXCEPTION 'Traversal passed through a hidden intermediate vertex: %', result;
    END IF;
    result := bluetusk_graph.reachable('public','topology','services','[1]');
    IF jsonb_array_length(result->'rows') <> 3 THEN
        RAISE EXCEPTION 'Traversal leaked a hidden vertex: %', result;
    END IF;
    BEGIN
        PERFORM bluetusk_graph.register_graph('public','unauthorized','{}');
        RAISE EXCEPTION 'Reader could mutate graph definitions';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
END
$$;

RESET ROLE;
ALTER TABLE topology_node RENAME COLUMN tenant TO organisation;
DO $$
BEGIN
    BEGIN
        PERFORM bluetusk_graph.reachable('public','topology','services','[1]');
        RAISE EXCEPTION 'Stale graph metadata was accepted';
    EXCEPTION WHEN object_not_in_prerequisite_state THEN NULL;
    END;
END
$$;
