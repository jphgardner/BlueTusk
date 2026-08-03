using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PostgreSqlSetReturningFunctionTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Array_collection_queries_translate_to_parameterized_lateral_unnest()
    {
        using var context = CreateContext();
        var minimum = 3;

        var innerSql = context.Values
            .SelectMany(
                value => value.Numbers.Where(number => number >= minimum),
                (value, number) => new { value.Id, Number = number })
            .OrderBy(result => result.Id)
            .ThenBy(result => result.Number)
            .ToQueryString();
        var outerSql = context.Values
            .SelectMany(
                value => value.Numbers
                    .Select(number => (int?)number)
                    .Where(number => number >= minimum)
                    .DefaultIfEmpty(),
                (value, number) => new { value.Id, Number = number })
            .ToQueryString();

        Assert.Contains("JOIN LATERAL", innerSql, StringComparison.Ordinal);
        Assert.Contains("unnest(", innerSql, StringComparison.Ordinal);
        Assert.Contains("WITH ORDINALITY", innerSql, StringComparison.Ordinal);
        Assert.Contains("(\"value\", \"ordinality\")", innerSql, StringComparison.Ordinal);
        Assert.Contains("@minimum", innerSql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN LATERAL", outerSql, StringComparison.Ordinal);
        Assert.DoesNotContain("CROSS APPLY", innerSql, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTER APPLY", outerSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Array_predicates_translate_through_the_set_returning_query_root()
    {
        using var context = CreateContext();
        var minimum = 3;

        var sql = context.Values
            .Where(value => value.Numbers.Any(number => number >= minimum))
            .Select(value => value.Id)
            .ToQueryString();

        Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("unnest(", sql, StringComparison.Ordinal);
        Assert.Contains("@minimum", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_series_roots_and_lateral_collections_translate_with_parameters()
    {
        using var context = CreateContext();
        var start = 2;
        var stop = 8;
        var step = 2;
        var minimum = 4;
        var timestampStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var timestampStop = timestampStart.AddHours(2);
        var timestampStep = TimeSpan.FromHours(1);

        var rootSql = context.Database
            .GenerateSeries(start, stop, step)
            .Where(value => value >= minimum)
            .OrderBy(value => value)
            .ToQueryString();
        var lateralSql = context.Values
            .SelectMany(
                value => EF.Functions.GenerateSeries(start, value.Id),
                (value, generated) => new { value.Id, Generated = generated })
            .Where(result => result.Generated >= minimum)
            .ToQueryString();
        var numericSql = context.Values
            .SelectMany(
                value => EF.Functions.GenerateSeries(1m, (decimal)value.Id, 0.5m),
                (value, generated) => new { value.Id, Generated = generated })
            .ToQueryString();
        var timestampSql = context.Values
            .SelectMany(
                _ => EF.Functions.GenerateSeries(timestampStart, timestampStop, timestampStep),
                (_, generated) => generated)
            .ToQueryString();

        Assert.Contains("generate_series(", rootSql, StringComparison.Ordinal);
        Assert.Contains("::integer", rootSql, StringComparison.Ordinal);
        Assert.Contains("@p0", rootSql, StringComparison.Ordinal);
        Assert.Contains("@minimum", rootSql, StringComparison.Ordinal);
        Assert.Contains("JOIN LATERAL", lateralSql, StringComparison.Ordinal);
        Assert.Contains("generate_series(", lateralSql, StringComparison.Ordinal);
        Assert.Contains("AS \"g\"(\"value\")", lateralSql, StringComparison.Ordinal);
        Assert.Contains("@start", lateralSql, StringComparison.Ordinal);
        Assert.Contains("generate_series(", numericSql, StringComparison.Ordinal);
        Assert.Contains("CAST(\"e\".\"Id\" AS numeric)", numericSql, StringComparison.Ordinal);
        Assert.Contains("generate_series(", timestampSql, StringComparison.Ordinal);
        Assert.Contains("@timestampStart", timestampSql, StringComparison.Ordinal);
        Assert.Contains("@timestampStep", timestampSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_series_query_roots_reject_a_zero_step()
    {
        using var context = CreateContext();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.Database.GenerateSeries(1, 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.Database.GenerateSeries(1L, 5L, 0L));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.Database.GenerateSeries(1m, 5m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.Database.GenerateSeries(
                new DateTime(2026, 1, 1),
                new DateTime(2026, 1, 2),
                TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.Database.GenerateSeries(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
                TimeSpan.Zero));
    }

    [Fact]
    public void Json_set_returning_roots_translate_with_exact_mappings_and_ordinality()
    {
        using var context = CreateContext();
        var json = "[\"captured\",null,\"value\"]";
        var path = new BlueTuskJsonPath("$.*");

        var elementsSql = context.Values
            .SelectMany(
                value => EF.Functions.JsonArrayElements(value.JsonArray),
                (value, element) => new { value.Id, Element = element })
            .ToQueryString();
        var textSql = context.Values
            .SelectMany(
                value => EF.Functions.JsonArrayElementsText(value.JsonArray),
                (value, element) => new { value.Id, Element = element })
            .ToQueryString();
        var keysSql = context.Values
            .SelectMany(
                value => EF.Functions.JsonObjectKeys(value.JsonObject),
                (value, key) => new { value.Id, Key = key })
            .ToQueryString();
        var eachSql = context.Values
            .SelectMany(
                value => EF.Functions.JsonEach(value.JsonObject),
                (value, pair) => new { value.Id, pair.Key, pair.Value })
            .ToQueryString();
        var eachTextSql = context.Values
            .SelectMany(
                value => EF.Functions.JsonEachText(value.JsonObject),
                (value, pair) => new { value.Id, pair.Key, pair.Value })
            .ToQueryString();
        var pathSql = context.Values
            .SelectMany(
                value => EF.Functions.JsonPathQuery(value.JsonObject, path),
                (value, match) => new { value.Id, Match = match })
            .ToQueryString();
        var parameterSql = context.Values
            .SelectMany(
                _ => EF.Functions.JsonArrayElementsText(json),
                (_, element) => element)
            .ToQueryString();

        Assert.Contains("JOIN LATERAL", elementsSql, StringComparison.Ordinal);
        Assert.Contains("jsonb_array_elements(", elementsSql, StringComparison.Ordinal);
        Assert.Contains("WITH ORDINALITY", elementsSql, StringComparison.Ordinal);
        Assert.Contains("(\"value\", \"ordinality\")", elementsSql, StringComparison.Ordinal);
        Assert.Contains("jsonb_array_elements_text(", textSql, StringComparison.Ordinal);
        Assert.Contains("jsonb_object_keys(", keysSql, StringComparison.Ordinal);
        Assert.Contains("jsonb_each(", eachSql, StringComparison.Ordinal);
        Assert.Contains("(\"key\", \"value\", \"ordinality\")", eachSql, StringComparison.Ordinal);
        Assert.Contains("jsonb_each_text(", eachTextSql, StringComparison.Ordinal);
        Assert.Contains("jsonb_path_query(", pathSql, StringComparison.Ordinal);
        Assert.Contains("@path", pathSql, StringComparison.Ordinal);
        Assert.Contains("jsonb_array_elements_text(", parameterSql, StringComparison.Ordinal);
        Assert.Contains("@json", parameterSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_set_returning_roots_translate_with_typed_arguments_and_ordinality()
    {
        using var context = CreateContext();
        var input = "BlueTusk PostgreSQL";
        var pattern = "([A-Z][A-Za-z]+)";
        var flags = "g";
        var variables = "{\"minimum\":1}";

        var subscriptsSql = context.Values
            .SelectMany(
                value => EF.Functions.GenerateSubscripts(value.Numbers, 1, true),
                (value, subscript) => new { value.Id, Subscript = subscript })
            .ToQueryString();
        var matchesSql = context.Values
            .SelectMany(
                _ => EF.Functions.RegexMatches(input, pattern, flags),
                (_, match) => match)
            .ToQueryString();
        var splitSql = context.Values
            .SelectMany(
                _ => EF.Functions.RegexSplitToTable(input, "\\s+"),
                (_, part) => part)
            .ToQueryString();
        var stringTableSql = context.Values
            .SelectMany(
                _ => EF.Functions.StringToTable("one,NULL,three", ",", "NULL"),
                (_, part) => part)
            .ToQueryString();
        var jsonPathSql = context.Values
            .SelectMany(
                value => EF.Functions.JsonPathQuery(
                    value.JsonObject,
                    new BlueTuskJsonPath("$.* ? (@ > $minimum)"),
                    variables,
                    true),
                (value, match) => new { value.Id, Match = match })
            .ToQueryString();

        Assert.Contains("JOIN LATERAL", subscriptsSql, StringComparison.Ordinal);
        Assert.Contains("generate_subscripts(", subscriptsSql, StringComparison.Ordinal);
        Assert.Contains("WITH ORDINALITY", subscriptsSql, StringComparison.Ordinal);
        Assert.Contains("TRUE", subscriptsSql, StringComparison.Ordinal);
        Assert.Contains("regexp_matches(", matchesSql, StringComparison.Ordinal);
        Assert.Contains("@input", matchesSql, StringComparison.Ordinal);
        Assert.Contains("@pattern", matchesSql, StringComparison.Ordinal);
        Assert.Contains("@flags", matchesSql, StringComparison.Ordinal);
        Assert.Contains("regexp_split_to_table(", splitSql, StringComparison.Ordinal);
        Assert.Contains("string_to_table(", stringTableSql, StringComparison.Ordinal);
        Assert.Contains("jsonb_path_query(", jsonPathSql, StringComparison.Ordinal);
        Assert.Contains("@variables", jsonPathSql, StringComparison.Ordinal);
        Assert.Contains("TRUE", jsonPathSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_recordsets_derive_quoted_columns_and_types_from_keyless_model_metadata()
    {
        using var context = CreateContext();
        var json = "[{\"id\":7,\"label\":\"captured\",\"active\":true}]";

        var lateralSql = context.Values
            .SelectMany(
                value => EF.Functions.JsonToRecordset<JsonRecordRow>(value.JsonRecords),
                (value, record) => new
                {
                    value.Id,
                    RecordId = record.Id,
                    record.Label,
                    record.Active,
                })
            .ToQueryString();
        var parameterSql = context.Values
            .SelectMany(
                _ => EF.Functions.JsonToRecordset<JsonRecordRow>(json),
                (_, record) => record)
            .ToQueryString();

        Assert.Contains("JOIN LATERAL", lateralSql, StringComparison.Ordinal);
        Assert.Contains("jsonb_to_recordset(", lateralSql, StringComparison.Ordinal);
        Assert.Contains("\"id\" integer", lateralSql, StringComparison.Ordinal);
        Assert.Contains("\"label\" text", lateralSql, StringComparison.Ordinal);
        Assert.Contains("\"active\" boolean", lateralSql, StringComparison.Ordinal);
        Assert.Contains("@json", parameterSql, StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(() => context.Values
            .SelectMany(
                value => EF.Functions.JsonToRecordset<SetReturningValue>(value.JsonRecords),
                (_, record) => record)
            .ToQueryString());
        Assert.Contains("must be configured as keyless", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_argument_unnest_translates_typed_nullable_pairs_and_parameters()
    {
        using var context = CreateContext();
        var numbers = new[] { 1, 2 };
        string?[] labels = ["one"];

        var lateralSql = context.Values
            .SelectMany(
                value => EF.Functions.Unnest(value.Numbers, value.Labels),
                (value, pair) => new { value.Id, Number = pair.Key, Label = pair.Value })
            .ToQueryString();
        var parameterSql = context.Values
            .SelectMany(
                _ => EF.Functions.Unnest(numbers, labels),
                (_, pair) => new { Number = pair.Key, Label = pair.Value })
            .ToQueryString();

        Assert.Contains("JOIN LATERAL", lateralSql, StringComparison.Ordinal);
        Assert.Contains("unnest(", lateralSql, StringComparison.Ordinal);
        Assert.Contains("WITH ORDINALITY", lateralSql, StringComparison.Ordinal);
        Assert.Contains(
            "(\"first\", \"second\", \"ordinality\")",
            lateralSql,
            StringComparison.Ordinal);
        Assert.Contains("unnest(", parameterSql, StringComparison.Ordinal);
        Assert.Contains("@numbers", parameterSql, StringComparison.Ordinal);
        Assert.Contains("@labels", parameterSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_argument_unnest_supports_generic_types_and_two_to_four_inputs()
    {
        using var context = CreateContext();
        long?[] numbers = [1, null];
        Guid?[] identifiers = [Guid.Parse("11111111-1111-1111-1111-111111111111")];
        bool?[] flags = [true, false, null];
        double?[] amounts = [1.5, 2.5];

        var pairSql = context.Values
            .SelectMany(
                _ => EF.Functions.Unnest(numbers, identifiers),
                (_, row) => new { row.First, row.Second })
            .ToQueryString();
        var tripleSql = context.Values
            .SelectMany(
                _ => EF.Functions.Unnest(numbers, identifiers, flags),
                (_, row) => new { row.First, row.Second, row.Third })
            .ToQueryString();
        var quadrupleSql = context.Values
            .SelectMany(
                _ => EF.Functions.Unnest(numbers, identifiers, flags, amounts),
                (_, row) => new { row.First, row.Second, row.Third, row.Fourth })
            .ToQueryString();

        Assert.Contains("unnest(", pairSql, StringComparison.Ordinal);
        Assert.Contains("@numbers", pairSql, StringComparison.Ordinal);
        Assert.Contains("@identifiers", pairSql, StringComparison.Ordinal);
        Assert.Contains("(\"first\", \"second\", \"ordinality\")", pairSql, StringComparison.Ordinal);
        Assert.Contains("@flags", tripleSql, StringComparison.Ordinal);
        Assert.Contains(
            "(\"first\", \"second\", \"third\", \"ordinality\")",
            tripleSql,
            StringComparison.Ordinal);
        Assert.Contains("@amounts", quadrupleSql, StringComparison.Ordinal);
        Assert.Contains(
            "(\"first\", \"second\", \"third\", \"fourth\", \"ordinality\")",
            quadrupleSql,
            StringComparison.Ordinal);

        var unsafePadding = Assert.Throws<InvalidOperationException>(() => context.Values
            .SelectMany(
                _ => EF.Functions.Unnest(new long[] { 1 }, identifiers),
                (_, row) => row)
            .ToQueryString());
        Assert.Contains("must use nullable element arrays", unsafePadding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_registered_table_functions_translate_schema_arguments_and_lateral_composition()
    {
        using var context = CreateContext();
        var minimum = 2;

        var rootSql = context.UserDefinedRows(minimum)
            .Where(row => row.Id >= minimum)
            .OrderBy(row => row.Id)
            .ToQueryString();
        var lateralSql = context.Values
            .SelectMany(
                value => context.UserDefinedRows(value.Id),
                (value, row) => new { value.Id, RowId = row.Id, row.Label })
            .ToQueryString();

        Assert.Contains(
            "\"application\".\"ef_user_defined_rows\"(@minimum)",
            rootSql,
            StringComparison.Ordinal);
        Assert.Contains("JOIN LATERAL", lateralSql, StringComparison.Ordinal);
        Assert.Contains(
            "\"application\".\"ef_user_defined_rows\"(\"e\".\"Id\")",
            lateralSql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CROSS APPLY", lateralSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_returning_functions_execute_and_materialize_typed_elements()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_set_returning_values";
            DROP FUNCTION IF EXISTS "application"."ef_user_defined_rows"(integer);
            CREATE SCHEMA IF NOT EXISTS "application";
            CREATE TABLE "ef_set_returning_values" (
                "Id" integer PRIMARY KEY,
                "Numbers" integer[] NOT NULL,
                "Labels" text[] NOT NULL,
                "JsonArray" jsonb NOT NULL,
                "JsonObject" jsonb NOT NULL,
                "JsonRecords" jsonb NOT NULL);
            INSERT INTO "ef_set_returning_values" (
                "Id",
                "Numbers",
                "Labels",
                "JsonArray",
                "JsonObject",
                "JsonRecords")
            VALUES
                (
                    1,
                    ARRAY[1, 3],
                    ARRAY['one', NULL, 'extra']::text[],
                    '["one",null,"one"]'::jsonb,
                    '{"alpha":1,"beta":2}'::jsonb,
                    '[{"id":1,"label":"one","active":true},{"id":2,"label":null}]'::jsonb),
                (
                    2,
                    ARRAY[2, 4],
                    ARRAY['two', 'four'],
                    '[1,2]'::jsonb,
                    '{"gamma":3,"nullable":null}'::jsonb,
                    '[{"id":3,"label":"three","active":false}]'::jsonb),
                (
                    3,
                    ARRAY[]::integer[],
                    ARRAY[]::text[],
                    '[]'::jsonb,
                    '{}'::jsonb,
                    '[]'::jsonb);
            CREATE FUNCTION "application"."ef_user_defined_rows"(minimum integer)
            RETURNS TABLE ("Id" integer, "Label" text)
            LANGUAGE SQL
            STABLE
            AS $function$
                SELECT value, 'row-' || value::text
                FROM generate_series(minimum, minimum + 2) AS value
            $function$
            """);

        try
        {
            await using var context = CreateContext(dataSource);
            var minimum = 3;
            var expanded = await context.Values
                .SelectMany(
                    value => value.Numbers.Where(number => number >= minimum),
                    (value, number) => new { value.Id, Number = number })
                .OrderBy(result => result.Id)
                .ThenBy(result => result.Number)
                .ToListAsync();
            var matchingIds = await context.Values
                .Where(value => value.Numbers.Any(number => number >= minimum))
                .OrderBy(value => value.Id)
                .Select(value => value.Id)
                .ToListAsync();
            var outer = await context.Values
                .SelectMany(
                    value => value.Numbers
                        .Select(number => (int?)number)
                        .Where(number => number >= minimum)
                        .DefaultIfEmpty(),
                    (value, number) => new { value.Id, Number = number })
                .OrderBy(result => result.Id)
                .ToListAsync();
            var labels = await context.Values
                .SelectMany(
                    value => value.Labels,
                    (value, label) => new { value.Id, Label = label })
                .ToListAsync();
            var jsonElements = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    value => EF.Functions.JsonArrayElements(value.JsonArray),
                    (_, element) => element)
                .ToListAsync();
            var jsonTextElements = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    value => EF.Functions.JsonArrayElementsText(value.JsonArray),
                    (_, element) => element)
                .ToListAsync();
            var jsonKeys = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    value => EF.Functions.JsonObjectKeys(value.JsonObject),
                    (_, key) => key)
                .OrderBy(key => key)
                .ToListAsync();
            var jsonPairs = await context.Values
                .Where(value => value.Id == 2)
                .SelectMany(
                    value => EF.Functions.JsonEach(value.JsonObject),
                    (_, pair) => pair)
                .OrderBy(pair => pair.Key)
                .ToListAsync();
            var jsonTextPairs = await context.Values
                .Where(value => value.Id == 2)
                .SelectMany(
                    value => EF.Functions.JsonEachText(value.JsonObject),
                    (_, pair) => pair)
                .OrderBy(pair => pair.Key)
                .ToListAsync();
            var jsonPathMatches = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    value => EF.Functions.JsonPathQuery(
                        value.JsonObject,
                        new BlueTuskJsonPath("$.*")),
                    (_, match) => match)
                .OrderBy(match => match)
                .ToListAsync();
            var jsonPathVariableMatches = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    value => EF.Functions.JsonPathQuery(
                        value.JsonObject,
                        new BlueTuskJsonPath("$.* ? (@ > $minimum)"),
                        "{\"minimum\":1}",
                        true),
                    (_, match) => match)
                .ToListAsync();
            var subscripts = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    value => EF.Functions.GenerateSubscripts(value.Numbers, 1),
                    (_, subscript) => subscript)
                .ToListAsync();
            var reverseSubscripts = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    value => EF.Functions.GenerateSubscripts(value.Numbers, 1, true),
                    (_, subscript) => subscript)
                .ToListAsync();
            var regexMatches = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    _ => EF.Functions.RegexMatches(
                        "BlueTusk PostgreSQL",
                        "([A-Z][A-Za-z]+)",
                        "g"),
                    (_, match) => match)
                .ToListAsync();
            var regexParts = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    _ => EF.Functions.RegexSplitToTable("alpha,beta;gamma", "[,;]"),
                    (_, part) => part)
                .ToListAsync();
            var stringParts = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    _ => EF.Functions.StringToTable("alpha,NULL,gamma", ",", "NULL"),
                    (_, part) => part)
                .ToListAsync();
            var jsonRecords = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    value => EF.Functions.JsonToRecordset<JsonRecordRow>(value.JsonRecords),
                    (_, record) => record)
                .OrderBy(record => record.Id)
                .ToListAsync();
            var zippedArrays = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    value => EF.Functions.Unnest(value.Numbers, value.Labels),
                    (_, pair) => pair)
                .ToListAsync();
            long?[] multiNumbers = [10, null];
            Guid firstIdentifier = Guid.Parse("11111111-1111-1111-1111-111111111111");
            Guid?[] multiIdentifiers = [firstIdentifier];
            bool?[] multiFlags = [true, false, null];
            double?[] multiAmounts = [1.5, 2.5];
            var multiTypedArrays = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    _ => EF.Functions.Unnest(
                        multiNumbers,
                        multiIdentifiers,
                        multiFlags,
                        multiAmounts),
                    (_, row) => row)
                .ToListAsync();
            var userDefinedRows = await context.UserDefinedRows(2)
                .Where(row => row.Id > 2)
                .OrderBy(row => row.Id)
                .ToListAsync();
            var correlatedUserDefinedRows = await context.Values
                .Where(value => value.Id <= 2)
                .SelectMany(
                    value => context.UserDefinedRows(value.Id),
                    (value, row) => new { SourceId = value.Id, RowId = row.Id })
                .OrderBy(row => row.SourceId)
                .ThenBy(row => row.RowId)
                .ToListAsync();
            var integerSeries = await context.Database
                .GenerateSeries(2, 6, 2)
                .OrderBy(value => value)
                .ToListAsync();
            var longSeries = await context.Database
                .GenerateSeries(5L, 1L, -2L)
                .OrderByDescending(value => value)
                .ToListAsync();
            var standaloneNumericSeries = await context.Database
                .GenerateSeries(1m, 3m)
                .OrderBy(value => value)
                .ToListAsync();
            var numericSeries = await context.Values
                .Where(value => value.Id == 3)
                .SelectMany(
                    value => EF.Functions.GenerateSeries(1m, (decimal)value.Id, 0.5m),
                    (_, generated) => generated)
                .OrderBy(value => value)
                .ToListAsync();
            var timestampStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var standaloneTimestampSeries = await context.Database
                .GenerateSeries(
                    timestampStart,
                    timestampStart.AddHours(2),
                    TimeSpan.FromHours(1))
                .OrderBy(value => value)
                .ToListAsync();
            var timestampSeries = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    _ => EF.Functions.GenerateSeries(
                        timestampStart,
                        timestampStart.AddHours(2),
                        TimeSpan.FromHours(1)),
                    (_, generated) => generated)
                .OrderBy(value => value)
                .ToListAsync();
            var timestampWithTimeZoneStart = new DateTimeOffset(
                2026,
                1,
                1,
                0,
                0,
                0,
                TimeSpan.Zero);
            var timestampWithTimeZoneSeries = await context.Database
                .GenerateSeries(
                    timestampWithTimeZoneStart,
                    timestampWithTimeZoneStart.AddHours(2),
                    TimeSpan.FromHours(1))
                .OrderBy(value => value)
                .ToListAsync();
            var correlatedSeries = await context.Values
                .SelectMany(
                    value => EF.Functions.GenerateSeries(1, value.Id),
                    (value, generated) => new { value.Id, Generated = generated })
                .OrderBy(result => result.Id)
                .ThenBy(result => result.Generated)
                .ToListAsync();
            var compiledSeriesCount = EF.CompileQuery(
                (SetReturningContext database, int seriesStart) => database.Values
                    .SelectMany(
                        value => EF.Functions.GenerateSeries(seriesStart, value.Id),
                        (value, generated) => generated)
                    .Count());
            var compiledNumericSeriesCount = EF.CompileQuery(
                (SetReturningContext database, decimal seriesStop) => database.Values
                    .Where(value => value.Id == 1)
                    .SelectMany(
                        _ => EF.Functions.GenerateSeries(1m, seriesStop, 0.5m),
                        (_, generated) => generated)
                    .Count());
            var compiledTimestampSeriesCount = EF.CompileQuery(
                (SetReturningContext database, DateTime seriesStop) => database.Values
                    .Where(value => value.Id == 1)
                    .SelectMany(
                        _ => EF.Functions.GenerateSeries(
                            timestampStart,
                            seriesStop,
                            TimeSpan.FromHours(1)),
                        (_, generated) => generated)
                    .Count());
            var compiledTimestampWithTimeZoneSeriesCount = EF.CompileQuery(
                (SetReturningContext database, DateTimeOffset seriesStop) => database.Values
                    .Where(value => value.Id == 1)
                    .SelectMany(
                        _ => EF.Functions.GenerateSeries(
                            timestampWithTimeZoneStart,
                            seriesStop,
                            TimeSpan.FromHours(1)),
                        (_, generated) => generated)
                    .Count());
            var compiledJsonElementCount = EF.CompileQuery(
                (SetReturningContext database, string jsonValue) => database.Values
                    .Where(value => value.Id == 1)
                    .SelectMany(
                        _ => EF.Functions.JsonArrayElementsText(jsonValue),
                        (_, element) => element)
                    .Count());
            var compiledJsonPairCount = EF.CompileQuery(
                (SetReturningContext database, string jsonValue) => database.Values
                    .Where(value => value.Id == 1)
                    .SelectMany(
                        _ => EF.Functions.JsonEachText(jsonValue),
                        (_, pair) => pair)
                    .Count());
            var compiledJsonRecordCount = EF.CompileQuery(
                (SetReturningContext database, string jsonValue) => database.Values
                    .Where(value => value.Id == 1)
                    .SelectMany(
                        _ => EF.Functions.JsonToRecordset<JsonRecordRow>(jsonValue),
                        (_, record) => record)
                    .Count());
            var compiledRegexSplitCount = EF.CompileQuery(
                (SetReturningContext database, string input) => database.Values
                    .Where(value => value.Id == 1)
                    .SelectMany(
                        _ => EF.Functions.RegexSplitToTable(input, "[,;]"),
                        (_, part) => part)
                    .Count());
            var compiledZippedArrayCount = EF.CompileQuery(
                (SetReturningContext database, int[] numbers, string?[] labels) => database.Values
                    .Where(value => value.Id == 1)
                    .SelectMany(
                        _ => EF.Functions.Unnest(numbers, labels),
                        (_, pair) => pair)
                    .Count());
            var compiledMultiArrayCount = EF.CompileQuery(
                (SetReturningContext database, long?[] first, Guid?[] second, bool?[] third) => database.Values
                    .Where(value => value.Id == 1)
                    .SelectMany(
                        _ => EF.Functions.Unnest(first, second, third),
                        (_, row) => row)
                    .Count());
            var compiledUserDefinedRowCount = EF.CompileQuery(
                (SetReturningContext database, int minimumId) => database
                    .UserDefinedRows(minimumId)
                    .Count());

            Assert.Collection(
                expanded,
                result =>
                {
                    Assert.Equal(1, result.Id);
                    Assert.Equal(3, result.Number);
                },
                result =>
                {
                    Assert.Equal(2, result.Id);
                    Assert.Equal(4, result.Number);
                });
            Assert.Equal([1, 2], matchingIds);
            Assert.Collection(
                outer,
                result =>
                {
                    Assert.Equal(1, result.Id);
                    Assert.Equal(3, result.Number);
                },
                result =>
                {
                    Assert.Equal(2, result.Id);
                    Assert.Equal(4, result.Number);
                },
                result =>
                {
                    Assert.Equal(3, result.Id);
                    Assert.Null(result.Number);
                });
            Assert.Contains(labels, result => result.Id == 1 && result.Label == "one");
            Assert.Contains(labels, result => result.Id == 1 && result.Label is null);
            Assert.Contains(labels, result => result.Id == 2 && result.Label == "two");
            Assert.Contains(labels, result => result.Id == 2 && result.Label == "four");
            Assert.Equal(["\"one\"", "null", "\"one\""], jsonElements);
            Assert.Equal(["one", null, "one"], jsonTextElements);
            Assert.Equal(["alpha", "beta"], jsonKeys);
            Assert.Equal(
                [
                    new KeyValuePair<string, string>("gamma", "3"),
                    new KeyValuePair<string, string>("nullable", "null"),
                ],
                jsonPairs);
            Assert.Equal(
                [
                    new KeyValuePair<string, string?>("gamma", "3"),
                    new KeyValuePair<string, string?>("nullable", null),
                ],
                jsonTextPairs);
            Assert.Equal(["1", "2"], jsonPathMatches);
            Assert.Equal(["2"], jsonPathVariableMatches);
            Assert.Equal([1, 2], subscripts);
            Assert.Equal([2, 1], reverseSubscripts);
            Assert.Collection(
                regexMatches,
                match => Assert.Equal(["BlueTusk"], match),
                match => Assert.Equal(["PostgreSQL"], match));
            Assert.Equal(["alpha", "beta", "gamma"], regexParts);
            Assert.Equal(["alpha", null, "gamma"], stringParts);
            Assert.Collection(
                jsonRecords,
                record => Assert.Equal((1, "one", true), (record.Id, record.Label, record.Active)),
                record => Assert.Equal((2, null, null), (record.Id, record.Label, record.Active)));
            Assert.Equal(
                [
                    new KeyValuePair<int?, string?>(1, "one"),
                    new KeyValuePair<int?, string?>(3, null),
                    new KeyValuePair<int?, string?>(null, "extra"),
                ],
                zippedArrays);
            Assert.Collection(
                multiTypedArrays,
                row => Assert.Equal((10L, firstIdentifier, true, 1.5),
                    (row.First, row.Second, row.Third, row.Fourth)),
                row => Assert.Equal((null, null, false, 2.5),
                    (row.First, row.Second, row.Third, row.Fourth)),
                row => Assert.Equal((null, null, null, null),
                    (row.First, row.Second, row.Third, row.Fourth)));
            Assert.Collection(
                userDefinedRows,
                row => Assert.Equal((3, "row-3"), (row.Id, row.Label)),
                row => Assert.Equal((4, "row-4"), (row.Id, row.Label)));
            Assert.Equal(
                [(1, 1), (1, 2), (1, 3), (2, 2), (2, 3), (2, 4)],
                correlatedUserDefinedRows
                    .Select(row => (row.SourceId, row.RowId))
                    .ToArray());
            Assert.Equal([2, 4, 6], integerSeries);
            Assert.Equal([5L, 3L, 1L], longSeries);
            Assert.Equal([1m, 2m, 3m], standaloneNumericSeries);
            Assert.Equal([1m, 1.5m, 2m, 2.5m, 3m], numericSeries);
            Assert.Equal(
                [timestampStart, timestampStart.AddHours(1), timestampStart.AddHours(2)],
                standaloneTimestampSeries);
            Assert.Equal(
                [timestampStart, timestampStart.AddHours(1), timestampStart.AddHours(2)],
                timestampSeries);
            Assert.Equal(
                [
                    timestampWithTimeZoneStart,
                    timestampWithTimeZoneStart.AddHours(1),
                    timestampWithTimeZoneStart.AddHours(2),
                ],
                timestampWithTimeZoneSeries);
            Assert.Collection(
                correlatedSeries,
                result => Assert.Equal((1, 1), (result.Id, result.Generated)),
                result => Assert.Equal((2, 1), (result.Id, result.Generated)),
                result => Assert.Equal((2, 2), (result.Id, result.Generated)),
                result => Assert.Equal((3, 1), (result.Id, result.Generated)),
                result => Assert.Equal((3, 2), (result.Id, result.Generated)),
                result => Assert.Equal((3, 3), (result.Id, result.Generated)));
            Assert.Equal(6, compiledSeriesCount(context, 1));
            Assert.Equal(3, compiledNumericSeriesCount(context, 2m));
            Assert.Equal(3, compiledTimestampSeriesCount(context, timestampStart.AddHours(2)));
            Assert.Equal(
                3,
                compiledTimestampWithTimeZoneSeriesCount(
                    context,
                    timestampWithTimeZoneStart.AddHours(2)));
            Assert.Equal(
                3,
                compiledJsonElementCount(context, "[\"captured\",null,\"value\"]"));
            Assert.Equal(
                2,
                compiledJsonPairCount(context, "{\"key\":\"value\",\"nullable\":null}"));
            Assert.Equal(
                2,
                compiledJsonRecordCount(
                    context,
                    "[{\"id\":10,\"label\":\"ten\"},{\"id\":11,\"active\":true}]"));
            Assert.Equal(3, compiledRegexSplitCount(context, "one,two;three"));
            Assert.Equal(
                3,
                compiledZippedArrayCount(context, [1, 2], ["one", null, "three"]));
            Assert.Equal(
                3,
                compiledMultiArrayCount(
                    context,
                    [10, null],
                    [firstIdentifier],
                    [true, false, null]));
            Assert.Equal(3, compiledUserDefinedRowCount(context, 5));
        }
        finally
        {
            await ExecuteNonQueryAsync(
                dataSource,
                """
                DROP TABLE IF EXISTS "ef_set_returning_values";
                DROP FUNCTION IF EXISTS "application"."ef_user_defined_rows"(integer)
                """);
        }
    }

    private static SetReturningContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SetReturningContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new SetReturningContext(options);
    }

    private static SetReturningContext CreateContext(BlueTuskDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<SetReturningContext>()
            .UseBlueTusk(dataSource)
            .Options;
        return new SetReturningContext(options);
    }

    private static async Task ExecuteNonQueryAsync(BlueTuskDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }

    private sealed class SetReturningContext(DbContextOptions<SetReturningContext> options) : DbContext(options)
    {
        public DbSet<SetReturningValue> Values => Set<SetReturningValue>();

        public IQueryable<UserDefinedFunctionRow> UserDefinedRows(int minimum)
            => FromExpression(() => UserDefinedRows(minimum));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var value = modelBuilder.Entity<SetReturningValue>();
            value.ToTable("ef_set_returning_values");
            value.Property(item => item.JsonArray).HasColumnType("jsonb");
            value.Property(item => item.JsonObject).HasColumnType("jsonb");
            value.Property(item => item.JsonRecords).HasColumnType("jsonb");

            var jsonRecord = modelBuilder.Entity<JsonRecordRow>();
            jsonRecord.HasNoKey();
            jsonRecord.Property(record => record.Id).HasColumnName("id").HasColumnType("integer");
            jsonRecord.Property(record => record.Label).HasColumnName("label").HasColumnType("text");
            jsonRecord.Property(record => record.Active).HasColumnName("active").HasColumnType("boolean");

            modelBuilder.Entity<UserDefinedFunctionRow>().HasNoKey();
            modelBuilder
                .HasDbFunction(
                    typeof(SetReturningContext).GetMethod(
                        nameof(UserDefinedRows),
                        [typeof(int)])!)
                .HasName("ef_user_defined_rows")
                .HasSchema("application");
        }
    }

    private sealed class UserDefinedFunctionRow
    {
        public int Id { get; set; }

        public string Label { get; set; } = "";
    }

    private sealed class JsonRecordRow
    {
        public int Id { get; set; }

        public string? Label { get; set; }

        public bool? Active { get; set; }
    }

    private sealed class SetReturningValue
    {
        public int Id { get; set; }

        public int[] Numbers { get; set; } = [];

        public string?[] Labels { get; set; } = [];

        public string JsonArray { get; set; } = "[]";

        public string JsonObject { get; set; } = "{}";

        public string JsonRecords { get; set; } = "[]";
    }
}
