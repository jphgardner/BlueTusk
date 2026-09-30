using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PostgreSqlJsonQueryTranslationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Primitive_JSON_collections_use_JSON_array_expansion()
    {
        using var context = CreateContext();

        var sql = context.Documents
            .Where(document => document.Payload.Scores.Any(score => score > 10))
            .ToQueryString();

        Assert.Contains("jsonb_array_elements_text", sql, StringComparison.Ordinal);
        Assert.Contains("WITH ORDINALITY", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("unnest(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Structural_JSON_collections_use_typed_recordset_expansion()
    {
        using var context = CreateContext();

        var sql = context.Documents
            .Where(document => document.Payload.Items.Any(item => item.Number == 10))
            .ToQueryString();

        Assert.Contains("ROWS FROM (jsonb_to_recordset(", sql, StringComparison.Ordinal);
        Assert.Contains("\"Number\" integer", sql, StringComparison.Ordinal);
        Assert.Contains(") WITH ORDINALITY AS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Indexed_structural_JSON_collection_projections_preserve_array_order()
    {
        using var context = CreateContext();

        var sql = context.Documents
            .Select(document => new
            {
                First = document.Payload.Items[0].Values,
                Second = document.Payload.Items[1].Values,
            })
            .ToQueryString();

        Assert.Contains("WITH ORDINALITY", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("\"ordinality\"", sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET 1", sql, StringComparison.Ordinal);
    }

    private static JsonQueryContext CreateContext()
        => new(new DbContextOptionsBuilder<JsonQueryContext>()
            .UseBlueTusk(ConnectionString)
            .ConfigureWarnings(warnings => warnings.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning))
            .Options);

    private sealed class JsonQueryContext(DbContextOptions<JsonQueryContext> options)
        : DbContext(options)
    {
        public DbSet<JsonDocument> Documents => Set<JsonDocument>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<JsonDocument>().OwnsOne(
                document => document.Payload,
                json =>
                {
                    json.ToJson();
                    json.OwnsMany(payload => payload.Items);
                });
    }

    private sealed class JsonDocument
    {
        public int Id { get; set; }

        public JsonPayload Payload { get; set; } = new();
    }

    private sealed class JsonPayload
    {
        public List<int> Scores { get; set; } = [];

        public List<JsonItem> Items { get; set; } = [];
    }

    private sealed class JsonItem
    {
        public int Number { get; set; }

        public int[] Values { get; set; } = [];
    }
}
