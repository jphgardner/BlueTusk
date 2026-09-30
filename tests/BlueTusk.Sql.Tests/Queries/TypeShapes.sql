-- bluetusk-query: BlueTusk.Sql.Verification.Generated.TypeShapes
-- bluetusk-param: flags bool[] required
-- bluetusk-param: shorts int2?[] required
-- bluetusk-param: longs int8?[] required
-- bluetusk-param: keys uuid[] required
-- bluetusk-param: singles float4[] required
-- bluetusk-param: doubles float8[] required
-- bluetusk-param: decimals numeric[] required
-- bluetusk-param: precise numeric-precise[] required
-- bluetusk-param: timestamps timestamp[] required
-- bluetusk-param: timezones timestamptz[] required
-- bluetusk-param: dates date[] required
-- bluetusk-param: jsons json[] required
-- bluetusk-param: jsonbs jsonb[] required
-- bluetusk-param: words varchar[] required
-- bluetusk-param: day date required
-- bluetusk-param: time time required
-- bluetusk-param: interval interval-pg required
-- bluetusk-result: Flags bool[] required
-- bluetusk-result: Shorts int2?[] required
-- bluetusk-result: Longs int8?[] required
-- bluetusk-result: Keys uuid[] required
-- bluetusk-result: Singles float4[] required
-- bluetusk-result: Doubles float8[] required
-- bluetusk-result: Decimals numeric[] required
-- bluetusk-result: Precise numeric-precise[] required
-- bluetusk-result: Timestamps timestamp[] required
-- bluetusk-result: Timezones timestamptz[] required
-- bluetusk-result: Dates date[] required
-- bluetusk-result: Jsons json[] required
-- bluetusk-result: Jsonbs jsonb[] required
-- bluetusk-result: Words varchar[] required
-- bluetusk-result: Day date required
-- bluetusk-result: Time time required
-- bluetusk-result: Interval interval-pg required
-- bluetusk-max-rows: 1
SELECT $1::bool[] AS "Flags", $2::int2[] AS "Shorts", $3::int8[] AS "Longs", $4::uuid[] AS "Keys",
    $5::float4[] AS "Singles", $6::float8[] AS "Doubles", $7::numeric[] AS "Decimals",
    $8::numeric[] AS "Precise", $9::timestamp[] AS "Timestamps", $10::timestamptz[] AS "Timezones",
    $11::date[] AS "Dates", $12::json[] AS "Jsons", $13::jsonb[] AS "Jsonbs", $14::varchar[] AS "Words",
    $15::date AS "Day", $16::time AS "Time", $17::interval AS "Interval";
