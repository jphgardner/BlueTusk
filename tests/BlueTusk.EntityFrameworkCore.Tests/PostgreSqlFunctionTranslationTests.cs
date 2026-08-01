using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PostgreSqlFunctionTranslationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Array_range_and_multirange_functions_translate()
    {
        using var context = CreateContext();

        var sql = context.Values
            .Select(value => new
            {
                Length = EF.Functions.ArrayLength(value.Numbers, 1),
                LowerBound = EF.Functions.ArrayLowerBound(value.Numbers, 1),
                UpperBound = EF.Functions.ArrayUpperBound(value.Numbers, 1),
                Cardinality = EF.Functions.ArrayCardinality(value.Numbers),
                RangeLower = EF.Functions.RangeLower(value.Range),
                RangeUpper = EF.Functions.RangeUpper(value.Range),
                RangeEmpty = EF.Functions.RangeIsEmpty(value.Range),
                RangeLowerInclusive = EF.Functions.RangeIsLowerInclusive(value.Range),
                RangeUpperInclusive = EF.Functions.RangeIsUpperInclusive(value.Range),
                RangeLowerInfinite = EF.Functions.RangeIsLowerInfinite(value.Range),
                RangeUpperInfinite = EF.Functions.RangeIsUpperInfinite(value.Range),
                MultirangeLower = EF.Functions.MultirangeLower(value.Multirange),
                MultirangeUpper = EF.Functions.MultirangeUpper(value.Multirange),
                MultirangeEmpty = EF.Functions.MultirangeIsEmpty(value.Multirange),
                MultirangeLowerInclusive = EF.Functions.MultirangeIsLowerInclusive(value.Multirange),
                MultirangeUpperInclusive = EF.Functions.MultirangeIsUpperInclusive(value.Multirange),
                MultirangeLowerInfinite = EF.Functions.MultirangeIsLowerInfinite(value.Multirange),
                MultirangeUpperInfinite = EF.Functions.MultirangeIsUpperInfinite(value.Multirange),
            })
            .ToQueryString();

        Assert.Contains("array_length(", sql, StringComparison.Ordinal);
        Assert.Contains("array_lower(", sql, StringComparison.Ordinal);
        Assert.Contains("array_upper(", sql, StringComparison.Ordinal);
        Assert.Contains("cardinality(", sql, StringComparison.Ordinal);
        Assert.Contains("lower(", sql, StringComparison.Ordinal);
        Assert.Contains("upper(", sql, StringComparison.Ordinal);
        Assert.Contains("isempty(", sql, StringComparison.Ordinal);
        Assert.Contains("lower_inc(", sql, StringComparison.Ordinal);
        Assert.Contains("upper_inc(", sql, StringComparison.Ordinal);
        Assert.Contains("lower_inf(", sql, StringComparison.Ordinal);
        Assert.Contains("upper_inf(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_regex_and_network_functions_translate_with_store_specific_results()
    {
        using var context = CreateContext();
        var path = new BlueTuskJsonPath("$.version");

        var sql = context.Values
            .Select(value => new
            {
                JsonType = EF.Functions.JsonTypeOf(value.Json),
                JsonLength = EF.Functions.JsonArrayLength(value.JsonArray),
                JsonValue = EF.Functions.JsonPathQueryFirst(value.Json, path),
                Replaced = EF.Functions.RegexReplace(value.Text, "provider", "driver"),
                Matches = EF.Functions.RegexCount(value.Text, "[A-Z]"),
                Host = EF.Functions.NetworkHost(value.Network),
                Family = EF.Functions.NetworkAddressFamily(value.Network),
                MaskLength = EF.Functions.NetworkMaskLength(value.Network),
                Network = EF.Functions.NetworkPart(value.Network),
                Broadcast = EF.Functions.NetworkBroadcast(value.Network),
            })
            .ToQueryString();

        Assert.Contains("jsonb_typeof(", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_array_length(", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_path_query_first(", sql, StringComparison.Ordinal);
        Assert.Contains("regexp_replace(", sql, StringComparison.Ordinal);
        Assert.Contains("regexp_count(", sql, StringComparison.Ordinal);
        Assert.Contains("host(", sql, StringComparison.Ordinal);
        Assert.Contains("family(", sql, StringComparison.Ordinal);
        Assert.Contains("masklen(", sql, StringComparison.Ordinal);
        Assert.Contains("network(", sql, StringComparison.Ordinal);
        Assert.Contains("broadcast(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_text_construction_and_ranking_functions_translate_composably()
    {
        using var context = CreateContext();
        var query = "PostgreSQL provider";

        var sql = context.Values
            .Where(value => EF.Functions.FullTextMatches(
                EF.Functions.ToTextSearchVector(value.Text),
                EF.Functions.PlainToTextSearchQuery(query)))
            .Select(value => new
            {
                VectorLength = EF.Functions.TextSearchVectorLength(
                    EF.Functions.ToTextSearchVector(value.Text)),
                RawNodes = EF.Functions.TextSearchQueryNodeCount(
                    EF.Functions.ToTextSearchQuery("PostgreSQL & provider")),
                PlainNodes = EF.Functions.TextSearchQueryNodeCount(
                    EF.Functions.PlainToTextSearchQuery(query)),
                PhraseNodes = EF.Functions.TextSearchQueryNodeCount(
                    EF.Functions.PhraseToTextSearchQuery(query)),
                WebNodes = EF.Functions.TextSearchQueryNodeCount(
                    EF.Functions.WebSearchToTextSearchQuery(query)),
                Rank = EF.Functions.TextSearchRank(
                    EF.Functions.ToTextSearchVector(value.Text),
                    EF.Functions.PlainToTextSearchQuery(query)),
            })
            .ToQueryString();

        Assert.Contains("to_tsvector(", sql, StringComparison.Ordinal);
        Assert.Contains("to_tsquery(", sql, StringComparison.Ordinal);
        Assert.Contains("plainto_tsquery(", sql, StringComparison.Ordinal);
        Assert.Contains("phraseto_tsquery(", sql, StringComparison.Ordinal);
        Assert.Contains("websearch_to_tsquery(", sql, StringComparison.Ordinal);
        Assert.Contains("length(", sql, StringComparison.Ordinal);
        Assert.Contains("numnode(", sql, StringComparison.Ordinal);
        Assert.Contains("ts_rank(", sql, StringComparison.Ordinal);
        Assert.Contains(" @@ ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostgreSQL_scalar_functions_execute_with_typed_results_and_parameters()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_function_values";
            CREATE TABLE "ef_function_values" (
                "Id" integer PRIMARY KEY,
                "Text" text NOT NULL,
                "Numbers" integer[] NOT NULL,
                "Range" int4range NOT NULL,
                "Multirange" int4multirange NOT NULL,
                "Json" jsonb NOT NULL,
                "JsonArray" jsonb NOT NULL,
                "Network" inet NOT NULL);
            INSERT INTO "ef_function_values"
                ("Id", "Text", "Numbers", "Range", "Multirange", "Json", "JsonArray", "Network")
            VALUES (
                1,
                'BlueTusk PostgreSQL provider',
                ARRAY[1, 2, 3],
                '[1,10)'::int4range,
                '{[1,4),[7,10)}'::int4multirange,
                '{"kind":"provider","version":3}'::jsonb,
                '[1,2,3]'::jsonb,
                '10.0.0.42/24'::inet)
            """);

        try
        {
            await using var context = CreateContext(dataSource);
            var collection = await context.Values
                .Select(value => new
                {
                    Length = EF.Functions.ArrayLength(value.Numbers, 1),
                    LowerBound = EF.Functions.ArrayLowerBound(value.Numbers, 1),
                    UpperBound = EF.Functions.ArrayUpperBound(value.Numbers, 1),
                    Cardinality = EF.Functions.ArrayCardinality(value.Numbers),
                    RangeLower = EF.Functions.RangeLower(value.Range),
                    RangeUpper = EF.Functions.RangeUpper(value.Range),
                    RangeEmpty = EF.Functions.RangeIsEmpty(value.Range),
                    RangeLowerInclusive = EF.Functions.RangeIsLowerInclusive(value.Range),
                    RangeUpperInclusive = EF.Functions.RangeIsUpperInclusive(value.Range),
                    RangeLowerInfinite = EF.Functions.RangeIsLowerInfinite(value.Range),
                    RangeUpperInfinite = EF.Functions.RangeIsUpperInfinite(value.Range),
                    MultirangeLower = EF.Functions.MultirangeLower(value.Multirange),
                    MultirangeUpper = EF.Functions.MultirangeUpper(value.Multirange),
                    MultirangeEmpty = EF.Functions.MultirangeIsEmpty(value.Multirange),
                    MultirangeLowerInclusive = EF.Functions.MultirangeIsLowerInclusive(value.Multirange),
                    MultirangeUpperInclusive = EF.Functions.MultirangeIsUpperInclusive(value.Multirange),
                    MultirangeLowerInfinite = EF.Functions.MultirangeIsLowerInfinite(value.Multirange),
                    MultirangeUpperInfinite = EF.Functions.MultirangeIsUpperInfinite(value.Multirange),
                })
                .SingleAsync();

            Assert.Equal(3, collection.Length);
            Assert.Equal(1, collection.LowerBound);
            Assert.Equal(3, collection.UpperBound);
            Assert.Equal(3, collection.Cardinality);
            Assert.Equal(1, collection.RangeLower);
            Assert.Equal(10, collection.RangeUpper);
            Assert.False(collection.RangeEmpty);
            Assert.True(collection.RangeLowerInclusive);
            Assert.False(collection.RangeUpperInclusive);
            Assert.False(collection.RangeLowerInfinite);
            Assert.False(collection.RangeUpperInfinite);
            Assert.Equal(1, collection.MultirangeLower);
            Assert.Equal(10, collection.MultirangeUpper);
            Assert.False(collection.MultirangeEmpty);
            Assert.True(collection.MultirangeLowerInclusive);
            Assert.False(collection.MultirangeUpperInclusive);
            Assert.False(collection.MultirangeLowerInfinite);
            Assert.False(collection.MultirangeUpperInfinite);

            var jsonPath = new BlueTuskJsonPath("$.version");
            var scalar = await context.Values
                .Select(value => new
                {
                    JsonType = EF.Functions.JsonTypeOf(value.Json),
                    JsonLength = EF.Functions.JsonArrayLength(value.JsonArray),
                    JsonValue = EF.Functions.JsonPathQueryFirst(value.Json, jsonPath),
                    Replaced = EF.Functions.RegexReplace(value.Text, "PostgreSQL", "database"),
                    Capitals = EF.Functions.RegexCount(value.Text, "[A-Z]"),
                    Host = EF.Functions.NetworkHost(value.Network),
                    Family = EF.Functions.NetworkAddressFamily(value.Network),
                    MaskLength = EF.Functions.NetworkMaskLength(value.Network),
                    Network = EF.Functions.NetworkPart(value.Network),
                    Broadcast = EF.Functions.NetworkBroadcast(value.Network),
                })
                .SingleAsync();

            Assert.Equal("object", scalar.JsonType);
            Assert.Equal(3, scalar.JsonLength);
            Assert.Equal("3", scalar.JsonValue);
            Assert.Equal("BlueTusk database provider", scalar.Replaced);
            Assert.Equal(6, scalar.Capitals);
            Assert.Equal("10.0.0.42", scalar.Host);
            Assert.Equal(4, scalar.Family);
            Assert.Equal(24, scalar.MaskLength);
            Assert.Equal("10.0.0.0/24", scalar.Network.ToString());
            Assert.True(scalar.Network.IsCidr);
            Assert.Equal("10.0.0.255/24", scalar.Broadcast.ToString());

            var search = "PostgreSQL provider";
            var textSearch = await context.Values
                .Select(value => new
                {
                    Matches = EF.Functions.FullTextMatches(
                        EF.Functions.ToTextSearchVector(value.Text),
                        EF.Functions.PlainToTextSearchQuery(search)),
                    VectorLength = EF.Functions.TextSearchVectorLength(
                        EF.Functions.ToTextSearchVector(value.Text)),
                    RawNodes = EF.Functions.TextSearchQueryNodeCount(
                        EF.Functions.ToTextSearchQuery("PostgreSQL & provider")),
                    PlainNodes = EF.Functions.TextSearchQueryNodeCount(
                        EF.Functions.PlainToTextSearchQuery(search)),
                    PhraseNodes = EF.Functions.TextSearchQueryNodeCount(
                        EF.Functions.PhraseToTextSearchQuery(search)),
                    WebNodes = EF.Functions.TextSearchQueryNodeCount(
                        EF.Functions.WebSearchToTextSearchQuery(search)),
                    Rank = EF.Functions.TextSearchRank(
                        EF.Functions.ToTextSearchVector(value.Text),
                        EF.Functions.PlainToTextSearchQuery(search)),
                })
                .SingleAsync();

            Assert.True(textSearch.Matches);
            Assert.True(textSearch.VectorLength > 0);
            Assert.True(textSearch.RawNodes > 0);
            Assert.True(textSearch.PlainNodes > 0);
            Assert.True(textSearch.PhraseNodes > 0);
            Assert.True(textSearch.WebNodes > 0);
            Assert.True(textSearch.Rank > 0);
        }
        finally
        {
            await ExecuteNonQueryAsync(dataSource, "DROP TABLE IF EXISTS \"ef_function_values\"");
        }
    }

    private static FunctionContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FunctionContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new FunctionContext(options);
    }

    private static FunctionContext CreateContext(BlueTuskDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<FunctionContext>()
            .UseBlueTusk(dataSource)
            .Options;
        return new FunctionContext(options);
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

    private sealed class FunctionContext(DbContextOptions<FunctionContext> options) : DbContext(options)
    {
        public DbSet<FunctionValue> Values => Set<FunctionValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var value = modelBuilder.Entity<FunctionValue>();
            value.ToTable("ef_function_values");
            value.Property(item => item.Json).HasColumnType("jsonb");
            value.Property(item => item.JsonArray).HasColumnType("jsonb");
        }
    }

    private sealed class FunctionValue
    {
        public int Id { get; set; }

        public string Text { get; set; } = string.Empty;

        public int[] Numbers { get; set; } = [];

        public BlueTuskRange<int> Range { get; set; }

        public BlueTuskMultirange<int> Multirange { get; set; } = new([]);

        public string Json { get; set; } = "{}";

        public string JsonArray { get; set; } = "[]";

        public BlueTuskNetworkAddress Network { get; set; }
    }
}
