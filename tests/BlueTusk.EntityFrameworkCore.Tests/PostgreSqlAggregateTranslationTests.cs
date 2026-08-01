using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PostgreSqlAggregateTranslationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void PostgreSQL_aggregates_translate_ordering_distinct_and_filters()
    {
        using var context = CreateContext();
        var delimiter = "|";
        var minimum = 10;

        var sql = context.Values
            .GroupBy(value => value.GroupId)
            .Select(group => new
            {
                group.Key,
                OrderedValues = EF.Functions.ArrayAggregate(
                    group.OrderBy(value => value.SortOrder).Select(value => value.Number)),
                UniqueValues = EF.Functions.ArrayAggregate(
                    group.Select(value => value.Number).Distinct()),
                IncludedValues = EF.Functions.ArrayAggregate(
                    group
                        .Where(value => value.Include && value.Number >= minimum)
                        .Select(value => value.Number)),
                Text = EF.Functions.StringAggregate(
                    group.OrderBy(value => value.SortOrder).Select(value => value.Text),
                    delimiter),
                All = EF.Functions.BooleanAnd(group.Select(value => value.Flag)),
                Any = EF.Functions.BooleanOr(group.Select(value => value.Flag)),
                Union = EF.Functions.RangeAggregate(group.Select(value => value.Range)),
                Intersection = EF.Functions.RangeIntersectAggregate(
                    group
                        .Where(value => value.Include && value.Number >= minimum)
                        .Select(value => value.Range)),
            })
            .ToQueryString();

        Assert.Contains("array_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("array_agg(DISTINCT ", sql, StringComparison.Ordinal);
        Assert.Contains("string_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("bool_and(", sql, StringComparison.Ordinal);
        Assert.Contains("bool_or(", sql, StringComparison.Ordinal);
        Assert.Contains("range_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("range_intersect_agg(", sql, StringComparison.Ordinal);
        Assert.Contains(" ORDER BY ", sql, StringComparison.Ordinal);
        Assert.Contains(" FILTER (WHERE ", sql, StringComparison.Ordinal);
        Assert.Contains("@delimiter", sql, StringComparison.Ordinal);
        Assert.Contains("@minimum", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostgreSQL_aggregates_execute_with_typed_results()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_aggregate_values";
            CREATE TABLE "ef_aggregate_values" (
                "Id" integer PRIMARY KEY,
                "GroupId" integer NOT NULL,
                "SortOrder" integer NOT NULL,
                "Number" integer NOT NULL,
                "Text" text NOT NULL,
                "Flag" boolean NOT NULL,
                "Include" boolean NOT NULL,
                "Range" int4range NOT NULL);
            INSERT INTO "ef_aggregate_values"
                ("Id", "GroupId", "SortOrder", "Number", "Text", "Flag", "Include", "Range")
            VALUES
                (1, 7, 2, 20, 'beta',  true,  true,  '[1,5)'::int4range),
                (2, 7, 1, 10, 'alpha', true,  true,  '[4,8)'::int4range),
                (3, 7, 3, 20, 'gamma', false, false, '[10,12)'::int4range)
            """);

        try
        {
            await using var context = CreateContext(dataSource);
            var delimiter = "|";
            var minimum = 10;
            var aggregate = await context.Values
                .GroupBy(value => value.GroupId)
                .Select(group => new
                {
                    group.Key,
                    OrderedValues = EF.Functions.ArrayAggregate(
                        group.OrderBy(value => value.SortOrder).Select(value => value.Number)),
                    UniqueValues = EF.Functions.ArrayAggregate(
                        group.Select(value => value.Number).Distinct()),
                    IncludedValues = EF.Functions.ArrayAggregate(
                        group
                            .Where(value => value.Include && value.Number >= minimum)
                            .Select(value => value.Number)),
                    Text = EF.Functions.StringAggregate(
                        group.OrderBy(value => value.SortOrder).Select(value => value.Text),
                        delimiter),
                    All = EF.Functions.BooleanAnd(group.Select(value => value.Flag)),
                    Any = EF.Functions.BooleanOr(group.Select(value => value.Flag)),
                    Union = EF.Functions.RangeAggregate(group.Select(value => value.Range)),
                    Intersection = EF.Functions.RangeIntersectAggregate(
                        group
                            .Where(value => value.Include && value.Number >= minimum)
                            .Select(value => value.Range)),
                })
                .SingleAsync();

            Assert.Equal(7, aggregate.Key);
            Assert.Equal([10, 20, 20], aggregate.OrderedValues!);
            Assert.Equal([10, 20], aggregate.UniqueValues!.Order());
            Assert.Equal([10, 20], aggregate.IncludedValues!.Order());
            Assert.Equal("alpha|beta|gamma", aggregate.Text);
            Assert.False(aggregate.All);
            Assert.True(aggregate.Any);
            Assert.Equal(
                new BlueTuskMultirange<int>(
                [
                    new BlueTuskRange<int>(1, 8),
                    new BlueTuskRange<int>(10, 12),
                ]),
                aggregate.Union);
            Assert.Equal(new BlueTuskRange<int>(4, 5), aggregate.Intersection);
        }
        finally
        {
            await ExecuteNonQueryAsync(dataSource, "DROP TABLE IF EXISTS \"ef_aggregate_values\"");
        }
    }

    private static AggregateContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AggregateContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new AggregateContext(options);
    }

    private static AggregateContext CreateContext(BlueTuskDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<AggregateContext>()
            .UseBlueTusk(dataSource)
            .Options;
        return new AggregateContext(options);
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

    private sealed class AggregateContext(DbContextOptions<AggregateContext> options) : DbContext(options)
    {
        public DbSet<AggregateValue> Values => Set<AggregateValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<AggregateValue>().ToTable("ef_aggregate_values");
    }

    private sealed class AggregateValue
    {
        public int Id { get; set; }

        public int GroupId { get; set; }

        public int SortOrder { get; set; }

        public int Number { get; set; }

        public string Text { get; set; } = string.Empty;

        public bool Flag { get; set; }

        public bool Include { get; set; }

        public BlueTuskRange<int> Range { get; set; }
    }
}
