-- bluetusk-query: BlueTusk.Sql.Tests.Generated.TypedCollections
-- bluetusk-param: numbers int4[] required
-- bluetusk-param: texts text[] required
-- bluetusk-param: nullableNumbers int4?[] required
-- bluetusk-param: precise numeric-precise required
-- bluetusk-param: wholeNullable int8[] nullable
-- bluetusk-param: matrix int4[,] required
-- bluetusk-result: Numbers int4[] required
-- bluetusk-result: Texts text[] required
-- bluetusk-result: NullableNumbers int4?[] required
-- bluetusk-result: Precise numeric-precise required
-- bluetusk-result: WholeNullable int8[] nullable
-- bluetusk-result: Matrix int4[,] required
-- bluetusk-max-rows: 1
SELECT $1::int4[] AS "Numbers", $2::text[] AS "Texts", $3::int4[] AS "NullableNumbers",
    $4::numeric AS "Precise", $5::int8[] AS "WholeNullable", $6::int4[] AS "Matrix";
