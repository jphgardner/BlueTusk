using BlueTusk.Client;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PostgreSqlQueryConstructTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Distinct_on_translates_with_ordering_and_rejects_invalid_composition()
    {
        using var context = CreateContext();

        var sql = context.Documents
            .OrderBy(document => document.Category)
            .ThenByDescending(document => document.Score)
            .DistinctOn(document => document.Category)
            .ToQueryString();

        Assert.Contains("SELECT DISTINCT ON (", sql, StringComparison.Ordinal);
        Assert.Contains(".\"Category\")", sql, StringComparison.Ordinal);
        Assert.Contains(
            "ORDER BY \"e\".\"Category\", \"e\".\"Score\" DESC",
            sql,
            StringComparison.Ordinal);
        var orderMismatch = Assert.Throws<InvalidOperationException>(() => context.Documents
            .OrderBy(document => document.Score)
            .DistinctOn(document => document.Category)
            .ToQueryString());
        Assert.Contains("leftmost ORDER BY", orderMismatch.Message, StringComparison.Ordinal);
        var ordinaryDistinct = Assert.Throws<InvalidOperationException>(() => context.Documents
            .Distinct()
            .DistinctOn(document => document.Category)
            .ToQueryString());
        Assert.Contains("cannot be combined", ordinaryDistinct.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_sampling_translates_typed_parameters_and_validates_scope()
    {
        using var context = CreateContext();
        var percentage = 25d;
        var seed = 42d;

        var systemSql = context.Documents
            .TableSampleSystem(percentage, seed)
            .Where(document => document.Score > 0)
            .ToQueryString();
        var bernoulliSql = context.Documents
            .TableSampleBernoulli(percentage)
            .ToQueryString();

        Assert.Contains("TABLESAMPLE SYSTEM", systemSql, StringComparison.Ordinal);
        Assert.Contains("REPEATABLE", systemSql, StringComparison.Ordinal);
        Assert.Contains("TABLESAMPLE BERNOULLI", bernoulliSql, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Documents.TableSampleSystem(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => context.Documents.TableSampleBernoulli(101));
        var joined = context.Documents.Join(
            context.Documents,
            left => left.Id,
            right => right.Id,
            (left, _) => left);
        var invalidScope = Assert.Throws<InvalidOperationException>(() => joined
            .TableSampleSystem(10)
            .ToQueryString());
        Assert.Contains("single mapped table", invalidScope.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_locking_translates_every_strength_and_wait_behavior()
    {
        using var context = CreateContext();

        var updateSql = context.Documents
            .Where(document => document.Id > 0)
            .OrderBy(document => document.Id)
            .Take(2)
            .ForUpdate(BlueTuskRowLockingBehavior.SkipLocked)
            .ToQueryString();
        var noKeyUpdateSql = context.Documents
            .ForNoKeyUpdate(BlueTuskRowLockingBehavior.NoWait)
            .ToQueryString();
        var shareSql = context.Documents.ForShare().ToQueryString();
        var keyShareSql = context.Documents.ForKeyShare().ToQueryString();

        Assert.Contains("LIMIT", updateSql, StringComparison.Ordinal);
        Assert.EndsWith("FOR UPDATE SKIP LOCKED", updateSql, StringComparison.Ordinal);
        Assert.EndsWith("FOR NO KEY UPDATE NOWAIT", noKeyUpdateSql, StringComparison.Ordinal);
        Assert.EndsWith("FOR SHARE", shareSql, StringComparison.Ordinal);
        Assert.EndsWith("FOR KEY SHARE", keyShareSql, StringComparison.Ordinal);
        var duplicate = Assert.Throws<InvalidOperationException>(() => context.Documents
            .ForUpdate()
            .ForShare()
            .ToQueryString());
        Assert.Contains("can be applied only once", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_functions_translate_partition_order_direction_and_typed_values()
    {
        using var context = CreateContext();

        var sql = context.Documents
            .Select(document => new
            {
                RowNumber = EF.Functions.WindowRowNumber(
                    document.Category,
                    document.Score),
                DescendingRowNumber = EF.Functions.WindowRowNumber(
                    document.Category,
                    EF.Functions.WindowDescending(document.Score)),
                Rank = EF.Functions.WindowRank(document.Category, document.Score),
                DenseRank = EF.Functions.WindowDenseRank(document.Category, document.Score),
                PercentRank = EF.Functions.WindowPercentRank(document.Category, document.Score),
                CumulativeDistribution = EF.Functions.WindowCumulativeDistribution(
                    document.Category,
                    document.Score),
                Tile = EF.Functions.WindowNtile(2, document.Category, document.Score),
                Previous = EF.Functions.WindowLag(
                    document.Score,
                    1,
                    -1,
                    document.Category,
                    document.Score),
                Next = EF.Functions.WindowLead(
                    document.Score,
                    1,
                    -1,
                    document.Category,
                    document.Score),
                First = EF.Functions.WindowFirstValue(
                    document.Score,
                    document.Category,
                    document.Score),
                Last = EF.Functions.WindowLastValue(
                    document.Score,
                    document.Category,
                    document.Score),
                Second = EF.Functions.WindowNthValue(
                    (int?)document.Score,
                    2,
                    document.Category,
                    document.Score),
            })
            .ToQueryString();

        Assert.Contains("row_number() OVER (PARTITION BY", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"e\".\"Score\")", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"e\".\"Score\" DESC)", sql, StringComparison.Ordinal);
        Assert.Contains("rank() OVER (", sql, StringComparison.Ordinal);
        Assert.Contains("dense_rank() OVER (", sql, StringComparison.Ordinal);
        Assert.Contains("percent_rank() OVER (", sql, StringComparison.Ordinal);
        Assert.Contains("cume_dist() OVER (", sql, StringComparison.Ordinal);
        Assert.Contains("ntile(2) OVER (", sql, StringComparison.Ordinal);
        Assert.Contains("lag(\"e\".\"Score\", 1, -1) OVER (", sql, StringComparison.Ordinal);
        Assert.Contains("lead(\"e\".\"Score\", 1, -1) OVER (", sql, StringComparison.Ordinal);
        Assert.Contains("first_value(\"e\".\"Score\") OVER (", sql, StringComparison.Ordinal);
        Assert.Contains("last_value(\"e\".\"Score\") OVER (", sql, StringComparison.Ordinal);
        Assert.Contains("nth_value(\"e\".\"Score\", 2) OVER (", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_constructs_execute_with_distinct_sampling_locking_and_compilation()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_query_construct_documents";
            CREATE TABLE "ef_query_construct_documents" (
                "Id" integer PRIMARY KEY,
                "Category" text NOT NULL,
                "Score" integer NOT NULL);
            INSERT INTO "ef_query_construct_documents" ("Id", "Category", "Score")
            VALUES
                (1, 'alpha', 1),
                (2, 'alpha', 3),
                (3, 'beta', 2)
            """);

        try
        {
            await using var context = CreateContext(dataSource);
            var distinct = await context.Documents
                .OrderBy(document => document.Category)
                .ThenByDescending(document => document.Score)
                .Select(document => new { document.Id, document.Category })
                .DistinctOn(document => document.Category)
                .ToListAsync();
            var fullSystemSample = await context.Documents
                .TableSampleSystem(100, 7)
                .CountAsync();
            var emptyBernoulliSample = await context.Documents
                .TableSampleBernoulli(0)
                .CountAsync();
            var compiledDistinct = EF.CompileQuery(
                (QueryConstructContext database, int minimumScore) => database.Documents
                    .Where(document => document.Score >= minimumScore)
                    .OrderBy(document => document.Category)
                    .ThenByDescending(document => document.Score)
                    .Select(document => new { document.Id, document.Category })
                    .DistinctOn(document => document.Category));
            var compiledWindow = EF.CompileQuery(
                (QueryConstructContext database, int minimumScore) => database.Documents
                    .Where(document => document.Score >= minimumScore)
                    .OrderBy(document => document.Id)
                    .Select(document => EF.Functions.WindowRowNumber(document.Score)));
            var windows = await context.Documents
                .OrderBy(document => document.Id)
                .Select(document => new
                {
                    document.Id,
                    RowNumber = EF.Functions.WindowRowNumber(
                        document.Category,
                        document.Score),
                    DescendingRowNumber = EF.Functions.WindowRowNumber(
                        document.Category,
                        EF.Functions.WindowDescending(document.Score)),
                    Rank = EF.Functions.WindowRank(document.Category, document.Score),
                    DenseRank = EF.Functions.WindowDenseRank(document.Category, document.Score),
                    PercentRank = EF.Functions.WindowPercentRank(
                        document.Category,
                        document.Score),
                    CumulativeDistribution = EF.Functions.WindowCumulativeDistribution(
                        document.Category,
                        document.Score),
                    Tile = EF.Functions.WindowNtile(2, document.Category, document.Score),
                    Previous = EF.Functions.WindowLag(
                        document.Score,
                        1,
                        -1,
                        document.Category,
                        document.Score),
                    Next = EF.Functions.WindowLead(
                        document.Score,
                        1,
                        -1,
                        document.Category,
                        document.Score),
                    First = EF.Functions.WindowFirstValue(
                        document.Score,
                        document.Category,
                        document.Score),
                    Last = EF.Functions.WindowLastValue(
                        document.Score,
                        document.Category,
                        document.Score),
                    Second = EF.Functions.WindowNthValue(
                        (int?)document.Score,
                        2,
                        document.Category,
                        document.Score),
                })
                .ToListAsync();

            Assert.Equal([2, 3], distinct.Select(document => document.Id).ToArray());
            Assert.Equal(3, fullSystemSample);
            Assert.Equal(0, emptyBernoulliSample);
            Assert.Equal(
                [2, 3],
                compiledDistinct(context, 2).Select(document => document.Id).ToArray());
            Assert.Equal([2L, 1L], compiledWindow(context, 2).ToArray());
            Assert.Collection(
                windows,
                row => Assert.Equal(
                    (1, 1L, 2L, 1L, 1L, 0d, 0.5d, 1, -1, 3, 1, 1, (int?)null),
                    (row.Id, row.RowNumber, row.DescendingRowNumber, row.Rank, row.DenseRank,
                        row.PercentRank, row.CumulativeDistribution, row.Tile, row.Previous,
                        row.Next, row.First, row.Last, row.Second)),
                row => Assert.Equal(
                    (2, 2L, 1L, 2L, 2L, 1d, 1d, 2, 1, -1, 1, 3, (int?)3),
                    (row.Id, row.RowNumber, row.DescendingRowNumber, row.Rank, row.DenseRank,
                        row.PercentRank, row.CumulativeDistribution, row.Tile, row.Previous,
                        row.Next, row.First, row.Last, row.Second)),
                row => Assert.Equal(
                    (3, 1L, 1L, 1L, 1L, 0d, 1d, 1, -1, -1, 2, 2, (int?)null),
                    (row.Id, row.RowNumber, row.DescendingRowNumber, row.Rank, row.DenseRank,
                        row.PercentRank, row.CumulativeDistribution, row.Tile, row.Previous,
                        row.Next, row.First, row.Last, row.Second)));

            await using var lockingContext = CreateContext(dataSource);
            await using var lockingTransaction = await lockingContext.Database.BeginTransactionAsync();
            _ = await lockingContext.Documents
                .Where(document => document.Id == 1)
                .Select(document => document.Id)
                .ForUpdate()
                .ToListAsync();

            await using var skippingContext = CreateContext(dataSource);
            await using var skippingTransaction = await skippingContext.Database.BeginTransactionAsync();
            var unlockedIds = await skippingContext.Documents
                .Where(document => document.Id <= 2)
                .OrderBy(document => document.Id)
                .Select(document => document.Id)
                .ForUpdate(BlueTuskRowLockingBehavior.SkipLocked)
                .ToListAsync();
            Assert.Equal([2], unlockedIds);
            await skippingTransaction.RollbackAsync();
            await lockingTransaction.RollbackAsync();

            await using var strengthContext = CreateContext(dataSource);
            await using var strengthTransaction = await strengthContext.Database.BeginTransactionAsync();
            Assert.Equal(
                [3],
                await strengthContext.Documents
                    .Where(document => document.Id == 3)
                    .Select(document => document.Id)
                    .ForNoKeyUpdate()
                    .ToListAsync());
            Assert.Equal(
                [3],
                await strengthContext.Documents
                    .Where(document => document.Id == 3)
                    .Select(document => document.Id)
                    .ForShare()
                    .ToListAsync());
            Assert.Equal(
                [3],
                await strengthContext.Documents
                    .Where(document => document.Id == 3)
                    .Select(document => document.Id)
                    .ForKeyShare()
                    .ToListAsync());
            await strengthTransaction.RollbackAsync();
        }
        finally
        {
            await ExecuteNonQueryAsync(
                dataSource,
                "DROP TABLE IF EXISTS \"ef_query_construct_documents\"");
        }
    }

    private static QueryConstructContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<QueryConstructContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new QueryConstructContext(options);
    }

    private static QueryConstructContext CreateContext(BlueTuskDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<QueryConstructContext>()
            .UseBlueTusk(dataSource)
            .Options;
        return new QueryConstructContext(options);
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

    private sealed class QueryConstructContext(DbContextOptions<QueryConstructContext> options)
        : DbContext(options)
    {
        public DbSet<QueryConstructDocument> Documents => Set<QueryConstructDocument>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<QueryConstructDocument>()
                .ToTable("ef_query_construct_documents");
    }

    private sealed class QueryConstructDocument
    {
        public int Id { get; set; }

        public string Category { get; set; } = "";

        public int Score { get; set; }
    }
}
