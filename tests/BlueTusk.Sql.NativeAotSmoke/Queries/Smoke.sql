-- bluetusk-query: BlueTusk.Sql.NativeAotSmoke.Generated.TypedSmoke
-- bluetusk-param: identifier int8 required
-- bluetusk-param: label text nullable
-- bluetusk-param: key uuid required
-- bluetusk-param: data jsonb required
-- bluetusk-param: blob bytea required
-- bluetusk-param: time timestamptz required
-- bluetusk-param: amount numeric required
-- bluetusk-result: Id int8 required
-- bluetusk-result: Label text nullable
-- bluetusk-result: Key uuid required
-- bluetusk-result: Data jsonb required
-- bluetusk-result: Blob bytea required
-- bluetusk-result: Time timestamptz required
-- bluetusk-result: Amount numeric required
-- bluetusk-max-rows: 1
SELECT $1::int8 AS "Id", $2::text AS "Label", $3::uuid AS "Key", $4::jsonb AS "Data",
    $5::bytea AS "Blob", $6::timestamptz AS "Time", $7::numeric AS "Amount"
