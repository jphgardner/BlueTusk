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
    public async Task Set_returning_functions_execute_and_materialize_typed_elements()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_set_returning_values";
            CREATE TABLE "ef_set_returning_values" (
                "Id" integer PRIMARY KEY,
                "Numbers" integer[] NOT NULL,
                "Labels" text[] NOT NULL,
                "JsonArray" jsonb NOT NULL,
                "JsonObject" jsonb NOT NULL);
            INSERT INTO "ef_set_returning_values" (
                "Id",
                "Numbers",
                "Labels",
                "JsonArray",
                "JsonObject")
            VALUES
                (
                    1,
                    ARRAY[1, 3],
                    ARRAY['one', NULL, 'extra']::text[],
                    '["one",null,"one"]'::jsonb,
                    '{"alpha":1,"beta":2}'::jsonb),
                (
                    2,
                    ARRAY[2, 4],
                    ARRAY['two', 'four'],
                    '[1,2]'::jsonb,
                    '{"gamma":3,"nullable":null}'::jsonb),
                (
                    3,
                    ARRAY[]::integer[],
                    ARRAY[]::text[],
                    '[]'::jsonb,
                    '{}'::jsonb)
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
            var zippedArrays = await context.Values
                .Where(value => value.Id == 1)
                .SelectMany(
                    value => EF.Functions.Unnest(value.Numbers, value.Labels),
                    (_, pair) => pair)
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
            var compiledZippedArrayCount = EF.CompileQuery(
                (SetReturningContext database, int[] numbers, string?[] labels) => database.Values
                    .Where(value => value.Id == 1)
                    .SelectMany(
                        _ => EF.Functions.Unnest(numbers, labels),
                        (_, pair) => pair)
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
            Assert.Equal(
                [
                    new KeyValuePair<int?, string?>(1, "one"),
                    new KeyValuePair<int?, string?>(3, null),
                    new KeyValuePair<int?, string?>(null, "extra"),
                ],
                zippedArrays);
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
                3,
                compiledZippedArrayCount(context, [1, 2], ["one", null, "three"]));
        }
        finally
        {
            await ExecuteNonQueryAsync(dataSource, "DROP TABLE IF EXISTS \"ef_set_returning_values\"");
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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var value = modelBuilder.Entity<SetReturningValue>();
            value.ToTable("ef_set_returning_values");
            value.Property(item => item.JsonArray).HasColumnType("jsonb");
            value.Property(item => item.JsonObject).HasColumnType("jsonb");
        }
    }

    private sealed class SetReturningValue
    {
        public int Id { get; set; }

        public int[] Numbers { get; set; } = [];

        public string?[] Labels { get; set; } = [];

        public string JsonArray { get; set; } = "[]";

        public string JsonObject { get; set; } = "{}";
    }
}
