using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PostgreSqlOperatorTranslationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Pattern_matching_extensions_translate_to_PostgreSQL_operators()
    {
        using var context = CreateContext();
        var like = "blue%";
        var regex = "^blue";

        var sql = context.Documents
            .Where(document =>
                EF.Functions.ILike(document.Name, like)
                && EF.Functions.RegexIsMatch(document.Name, regex)
                && EF.Functions.RegexIsMatchInsensitive(document.Name, regex))
            .ToQueryString();

        Assert.Contains(" ILIKE ", sql, StringComparison.Ordinal);
        Assert.Contains(" ~ ", sql, StringComparison.Ordinal);
        Assert.Contains(" ~* ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Array_range_and_multirange_extensions_translate_to_PostgreSQL_operators()
    {
        using var context = CreateContext();
        var numbers = new[] { 2, 3 };
        var range = new BlueTuskRange<int>(2, 8);
        var multirange = new BlueTuskMultirange<int>([range]);

        var arraySql = context.Documents
            .Where(document =>
                EF.Functions.ArrayContains(document.Numbers, numbers)
                && EF.Functions.ArrayContainedBy(document.Numbers, numbers)
                && EF.Functions.ArrayOverlaps(document.Numbers, numbers))
            .ToQueryString();
        var rangeSql = context.Documents
            .Where(document =>
                EF.Functions.RangeContains(document.Range, range)
                && EF.Functions.RangeContains(document.Range, 5)
                && EF.Functions.RangeContainedBy(document.Range, range)
                && EF.Functions.RangeOverlaps(document.Range, range)
                && EF.Functions.RangeIsStrictlyLeftOf(document.Range, range)
                && EF.Functions.RangeIsStrictlyRightOf(document.Range, range)
                && EF.Functions.RangeIsAdjacentTo(document.Range, range))
            .ToQueryString();
        var multirangeSql = context.Documents
            .Where(document =>
                EF.Functions.MultirangeContains(document.Multirange, multirange)
                && EF.Functions.MultirangeContains(document.Multirange, range)
                && EF.Functions.MultirangeContains(document.Multirange, 5)
                && EF.Functions.MultirangeContainedBy(document.Multirange, multirange)
                && EF.Functions.MultirangeOverlaps(document.Multirange, multirange))
            .ToQueryString();

        Assert.Contains(" @> ", arraySql, StringComparison.Ordinal);
        Assert.Contains(" <@ ", arraySql, StringComparison.Ordinal);
        Assert.Contains(" && ", arraySql, StringComparison.Ordinal);
        Assert.Contains(" << ", rangeSql, StringComparison.Ordinal);
        Assert.Contains(" >> ", rangeSql, StringComparison.Ordinal);
        Assert.Contains(" -|- ", rangeSql, StringComparison.Ordinal);
        Assert.Contains(" @> ", multirangeSql, StringComparison.Ordinal);
        Assert.Contains(" <@ ", multirangeSql, StringComparison.Ordinal);
        Assert.Contains(" && ", multirangeSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_extensions_preserve_jsonb_and_path_operator_semantics()
    {
        using var context = CreateContext();
        var fragment = """{"kind":"provider"}""";
        var keys = new[] { "kind", "version" };
        var path = new BlueTuskJsonPath("$.kind");
        var predicate = new BlueTuskJsonPath("$.version > 1");

        var sql = context.Documents
            .Where(document =>
                EF.Functions.JsonContains(document.Json, fragment)
                && EF.Functions.JsonContainedBy(document.Json, fragment)
                && EF.Functions.JsonExists(document.Json, "kind")
                && EF.Functions.JsonExistsAny(document.Json, keys)
                && EF.Functions.JsonExistsAll(document.Json, keys)
                && EF.Functions.JsonPathExists(document.Json, path)
                && EF.Functions.JsonPathMatches(document.Json, predicate))
            .ToQueryString();

        Assert.Contains(" @> ", sql, StringComparison.Ordinal);
        Assert.Contains(" <@ ", sql, StringComparison.Ordinal);
        Assert.Contains(" ? ", sql, StringComparison.Ordinal);
        Assert.Contains(" ?| ", sql, StringComparison.Ordinal);
        Assert.Contains(" ?& ", sql, StringComparison.Ordinal);
        Assert.Contains(" @? ", sql, StringComparison.Ordinal);
        Assert.Contains(" @@ ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_and_full_text_extensions_translate_to_typed_PostgreSQL_operators()
    {
        using var context = CreateContext();
        var network = BlueTuskNetworkAddress.Parse("10.0.0.0/24");
        var query = BlueTuskTextSearchQuery.Parse("'provider'");

        var sql = context.Documents
            .Where(document =>
                EF.Functions.NetworkContains(document.Network, network)
                && EF.Functions.NetworkContainedBy(document.Network, network)
                && EF.Functions.NetworkOverlaps(document.Network, network)
                && EF.Functions.FullTextMatches(document.SearchVector, query))
            .ToQueryString();

        Assert.Contains(" >>= ", sql, StringComparison.Ordinal);
        Assert.Contains(" <<= ", sql, StringComparison.Ordinal);
        Assert.Contains(" && ", sql, StringComparison.Ordinal);
        Assert.Contains(" @@ ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostgreSQL_native_operators_execute_with_typed_parameters()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_operator_documents";
            CREATE TABLE "ef_operator_documents" (
                "Id" integer PRIMARY KEY,
                "Name" text NOT NULL,
                "Numbers" integer[] NOT NULL,
                "Range" int4range NOT NULL,
                "Multirange" int4multirange NOT NULL,
                "Json" jsonb NOT NULL,
                "Network" inet NOT NULL,
                "SearchVector" tsvector NOT NULL);
            INSERT INTO "ef_operator_documents"
                ("Id", "Name", "Numbers", "Range", "Multirange", "Json", "Network", "SearchVector")
            VALUES (
                1,
                'BlueTusk Provider',
                ARRAY[1, 2, 3],
                '[1,10)'::int4range,
                '{[1,4),[7,10)}'::int4multirange,
                '{"kind":"provider","version":3}'::jsonb,
                '10.0.0.42/24'::inet,
                to_tsvector('simple', 'BlueTusk PostgreSQL provider'))
            """);

        try
        {
            await using var context = CreateContext(dataSource);
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.ILike(document.Name, "bluetusk%")
                    && EF.Functions.RegexIsMatch(document.Name, "^Blue")
                    && EF.Functions.RegexIsMatchInsensitive(document.Name, "^blue")));
            var containedNumbers = new[] { 1, 3 };
            var containingNumbers = new[] { 1, 2, 3, 4 };
            var overlappingNumbers = new[] { 3, 9 };
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.ArrayContains(document.Numbers, containedNumbers)
                    && EF.Functions.ArrayContainedBy(document.Numbers, containingNumbers)
                    && EF.Functions.ArrayOverlaps(document.Numbers, overlappingNumbers)));

            var containedRange = new BlueTuskRange<int>(2, 3);
            var containingRange = new BlueTuskRange<int>(0, 20);
            var overlappingRange = new BlueTuskRange<int>(9, 12);
            var rightRange = new BlueTuskRange<int>(20, 30);
            var leftRange = new BlueTuskRange<int>(-10, 0);
            var adjacentRange = new BlueTuskRange<int>(10, 12);
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.RangeContains(document.Range, containedRange)
                    && EF.Functions.RangeContains(document.Range, 5)
                    && EF.Functions.RangeContainedBy(document.Range, containingRange)
                    && EF.Functions.RangeOverlaps(document.Range, overlappingRange)
                    && EF.Functions.RangeIsStrictlyLeftOf(document.Range, rightRange)
                    && EF.Functions.RangeIsStrictlyRightOf(document.Range, leftRange)
                    && EF.Functions.RangeIsAdjacentTo(document.Range, adjacentRange)));

            var containedMultirange = new BlueTuskMultirange<int>([containedRange]);
            var containingMultirange = new BlueTuskMultirange<int>([containingRange]);
            var overlappingMultirange = new BlueTuskMultirange<int>([overlappingRange]);
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.MultirangeContains(document.Multirange, containedMultirange)
                    && EF.Functions.MultirangeContains(document.Multirange, containedRange)
                    && EF.Functions.MultirangeContains(document.Multirange, 8)
                    && EF.Functions.MultirangeContainedBy(document.Multirange, containingMultirange)
                    && EF.Functions.MultirangeOverlaps(document.Multirange, overlappingMultirange)));

            var jsonFragment = """{"kind":"provider"}""";
            var jsonContainer = """{"kind":"provider","version":3,"stable":false}""";
            var anyJsonKeys = new[] { "missing", "version" };
            var allJsonKeys = new[] { "kind", "version" };
            var jsonPath = new BlueTuskJsonPath("$.version ? (@ > 2)");
            var jsonPredicate = new BlueTuskJsonPath("$.version > 2");
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.JsonContains(document.Json, jsonFragment)
                    && EF.Functions.JsonContainedBy(document.Json, jsonContainer)
                    && EF.Functions.JsonExists(document.Json, "kind")
                    && EF.Functions.JsonExistsAny(document.Json, anyJsonKeys)
                    && EF.Functions.JsonExistsAll(document.Json, allJsonKeys)
                    && EF.Functions.JsonPathExists(document.Json, jsonPath)
                    && EF.Functions.JsonPathMatches(document.Json, jsonPredicate)));

            var containedAddress = BlueTuskNetworkAddress.Parse("10.0.0.99");
            var containingNetwork = BlueTuskNetworkAddress.Parse("10.0.0.0/16");
            var overlappingNetwork = BlueTuskNetworkAddress.Parse("10.0.0.0/25");
            var textSearchQuery = BlueTuskTextSearchQuery.Parse("'provider'");
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.NetworkContains(document.Network, containedAddress)
                    && EF.Functions.NetworkContainedBy(document.Network, containingNetwork)
                    && EF.Functions.NetworkOverlaps(document.Network, overlappingNetwork)
                    && EF.Functions.FullTextMatches(document.SearchVector, textSearchQuery)));
        }
        finally
        {
            await ExecuteNonQueryAsync(dataSource, "DROP TABLE IF EXISTS \"ef_operator_documents\"");
        }
    }

    private static DocumentContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DocumentContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new DocumentContext(options);
    }

    private static DocumentContext CreateContext(BlueTuskDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<DocumentContext>()
            .UseBlueTusk(dataSource)
            .Options;
        return new DocumentContext(options);
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

    private sealed class DocumentContext(DbContextOptions<DocumentContext> options) : DbContext(options)
    {
        public DbSet<Document> Documents => Set<Document>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var document = modelBuilder.Entity<Document>();
            document.ToTable("ef_operator_documents");
            document.Property(value => value.Json).HasColumnType("jsonb");
        }
    }

    private sealed class Document
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public int[] Numbers { get; set; } = [];

        public BlueTuskRange<int> Range { get; set; }

        public BlueTuskMultirange<int> Multirange { get; set; } = new([]);

        public string Json { get; set; } = "{}";

        public BlueTuskNetworkAddress Network { get; set; }

        public BlueTuskTextSearchVector SearchVector { get; set; } = new([]);
    }
}
