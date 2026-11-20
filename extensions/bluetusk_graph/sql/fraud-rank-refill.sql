-- Prototype an authoritative rank refill across two ranges in one statement.
-- The normalized numeric rank and row ID give the covering index a total order.
CREATE TEMP TABLE fraud_rank (
    id bigint PRIMARY KEY,
    amount numeric(20,4) NOT NULL,
    rank_amount numeric(20,4) GENERATED ALWAYS AS (-amount) STORED);
CREATE INDEX ON fraud_rank (rank_amount, id) INCLUDE (amount);
INSERT INTO fraud_rank (id, amount)
SELECT id, (id % 70) + 0.0199 FROM generate_series(1, 10000) AS id;

CREATE TEMP TABLE fraud_tail AS
SELECT rank_amount, id FROM fraud_rank ORDER BY rank_amount, id OFFSET 99 LIMIT 1;
DELETE FROM fraud_rank WHERE id IN (
    SELECT id FROM fraud_rank ORDER BY rank_amount, id LIMIT 3);
UPDATE fraud_rank SET amount = amount - 59.0001 WHERE id IN (
    SELECT id FROM fraud_rank ORDER BY rank_amount, id LIMIT 5);

DO $$
DECLARE
    rank_boundary fraud_tail%ROWTYPE;
    expected bigint[];
    actual bigint[];
BEGIN
    SELECT * INTO STRICT rank_boundary FROM fraud_tail;
    SELECT array_agg(id ORDER BY rank_amount, id) INTO expected FROM (
        SELECT id, rank_amount FROM fraud_rank ORDER BY rank_amount, id LIMIT 100
    ) AS full_query;
    WITH head AS MATERIALIZED (
        SELECT id, rank_amount FROM fraud_rank
         WHERE (rank_amount, id) <= (rank_boundary.rank_amount, rank_boundary.id)
         ORDER BY rank_amount, id LIMIT 100
    ), refill AS (
        SELECT id, rank_amount FROM fraud_rank
         WHERE (rank_amount, id) > (rank_boundary.rank_amount, rank_boundary.id)
         ORDER BY rank_amount, id LIMIT (100 - (SELECT count(*) FROM head))
    )
    SELECT array_agg(id ORDER BY rank_amount, id) INTO actual
      FROM (SELECT * FROM head UNION ALL SELECT * FROM refill) AS combined;
    IF actual IS DISTINCT FROM expected THEN
        RAISE EXCEPTION 'Rank refill disagreed with authoritative requery after deletes and demotions';
    END IF;
END
$$;
