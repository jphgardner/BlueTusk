-- bluetusk-query: BlueTusk.Sql.Tests.Generated.TypedRows
-- bluetusk-param: count int4 required
-- bluetusk-param: prefix text nullable
-- bluetusk-result: Id int4 required
-- bluetusk-result: Label text nullable
-- bluetusk-max-rows: 3
SELECT n::int4 AS "Id", $2::text AS "Label" FROM generate_series(1, $1::int4) n ORDER BY n;
