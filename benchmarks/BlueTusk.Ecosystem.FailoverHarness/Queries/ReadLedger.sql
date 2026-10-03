-- bluetusk-query: BlueTusk.Ecosystem.FailoverHarness.Queries.ReadLedger
-- bluetusk-param: tenant text required
-- bluetusk-param: gate bool required
-- bluetusk-result: Tenant text required
-- bluetusk-result: Id int4 required
-- bluetusk-result: Amount int8 required
-- bluetusk-result: Gate text required
-- bluetusk-max-rows: 64
SELECT tenant AS "Tenant", id AS "Id", amount AS "Amount", bluetusk_failover_sql.gate(id, $2::bool) AS "Gate" FROM bluetusk_failover_sql.ledger WHERE tenant = $1::text ORDER BY id LIMIT 64;
