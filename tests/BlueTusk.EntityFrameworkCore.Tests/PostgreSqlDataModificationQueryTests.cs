using BlueTusk.Client;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PostgreSqlDataModificationQueryTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Returning_modifications_translate_typed_predicates_setters_and_projections()
    {
        using var context = CreateContext();
        var category = "alpha";
        var increment = 4;
        var insertedId = 4;

        var doNothingSql = context.Documents
            .InsertOnConflictDoNothingReturning(
                () => new ReturningDocument
                {
                    Id = insertedId,
                    Category = category,
                    Score = increment,
                },
                document => document.Id)
            .Select(document => new { document.Id, document.Category, document.Score })
            .ToQueryString();
        var conflictUpdateSql = context.Documents
            .InsertOnConflictUpdateReturning(
                () => new ReturningDocument
                {
                    Id = insertedId,
                    Category = category,
                    Score = increment,
                },
                document => document.Id,
                document => new { document.Category, document.Score })
            .Select(document => new { document.Id, document.Category, document.Score })
            .ToQueryString();
        var compositeConflictSql = context.Documents
            .AsNoTracking()
            .InsertOnConflictDoNothingReturning(
                () => new ReturningDocument
                {
                    Id = insertedId,
                    Category = category,
                    Score = increment,
                },
                document => new { document.Category, document.Score })
            .ToQueryString();

        var deleteSql = context.Documents
            .Where(document => document.Category == category)
            .DeleteReturning()
            .Select(document => new { document.Id, document.Category })
            .ToQueryString();
        var updateSql = context.Documents
            .Where(document => document.Category == category)
            .UpdateReturning(setters => setters
                .SetProperty(document => document.Score, document => document.Score + increment)
                .SetProperty(document => document.Category, document => "promoted"))
            .Select(document => new { document.Id, document.Category, document.Score })
            .ToQueryString();
        var singleSetterSql = context.Documents
            .Where(document => document.Id == 1)
            .UpdateReturning(
                document => document.Score,
                document => document.Score + increment)
            .Select(document => document.Score)
            .ToQueryString();

        Assert.Contains("INSERT INTO \"ef_returning_documents\" AS \"e\"", doNothingSql, StringComparison.Ordinal);
        Assert.Contains("(\"Id\", \"Category\", \"Score\")", doNothingSql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (\"Id\") DO NOTHING", doNothingSql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (\"Category\", \"Score\") DO NOTHING", compositeConflictSql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (\"Id\") DO UPDATE SET", conflictUpdateSql, StringComparison.Ordinal);
        Assert.Contains("\"Category\" = EXCLUDED.\"Category\"", conflictUpdateSql, StringComparison.Ordinal);
        Assert.Contains("\"Score\" = EXCLUDED.\"Score\"", conflictUpdateSql, StringComparison.Ordinal);
        Assert.Contains("RETURNING \"e\".\"Id\", \"e\".\"Category\", \"e\".\"Score\"", conflictUpdateSql, StringComparison.Ordinal);
        Assert.DoesNotContain("VALUES (4,", conflictUpdateSql, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM \"ef_returning_documents\" AS \"e\"", deleteSql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"e\".\"Category\" = @category", deleteSql, StringComparison.Ordinal);
        Assert.Contains("RETURNING \"e\".\"Id\", \"e\".\"Category\"", deleteSql, StringComparison.Ordinal);
        Assert.Contains("UPDATE \"ef_returning_documents\" AS \"e\"", updateSql, StringComparison.Ordinal);
        Assert.Contains("SET \"Score\" = \"e\".\"Score\" +", updateSql, StringComparison.Ordinal);
        Assert.Contains(", \"Category\" =", updateSql, StringComparison.Ordinal);
        Assert.Contains("RETURNING \"e\".\"Id\", \"e\".\"Category\", \"e\".\"Score\"", updateSql, StringComparison.Ordinal);
        Assert.Contains("SET \"Score\" = \"e\".\"Score\" +", singleSetterSql, StringComparison.Ordinal);

        var ordered = Assert.Throws<InvalidOperationException>(() => context.Documents
            .OrderBy(document => document.Id)
            .DeleteReturning()
            .ToQueryString());
        Assert.Contains("without ordering", ordered.Message, StringComparison.Ordinal);
        var noSetters = Assert.Throws<InvalidOperationException>(() => context.Documents
            .UpdateReturning(_ => { })
            .ToQueryString());
        Assert.Contains("at least one SetProperty", noSetters.Message, StringComparison.Ordinal);

        var multiSetterCompiled = EF.CompileQuery(
            (ReturningContext database) => database.Documents
                .AsNoTracking()
                .UpdateReturning(
                    setters => setters.SetProperty(
                        document => document.Score,
                        document => document.Score + 1)));
        var compiledSetters = Assert.Throws<InvalidOperationException>(() => multiSetterCompiled(context).ToArray());
        Assert.Contains("property/value-selector overload", compiledSetters.Message, StringComparison.Ordinal);

        var composedInsert = Assert.Throws<InvalidOperationException>(() => context.Documents
            .Where(document => document.Score > 0)
            .InsertOnConflictDoNothingReturning(
                () => new ReturningDocument { Id = 5, Category = "invalid", Score = 1 },
                document => document.Id)
            .ToQueryString());
        Assert.Contains("target a DbSet directly", composedInsert.Message, StringComparison.Ordinal);
        var computedConflict = Assert.Throws<ArgumentException>(() => context.Documents
            .InsertOnConflictDoNothingReturning(
                () => new ReturningDocument { Id = 5, Category = "invalid", Score = 1 },
                document => document.Id + 1)
            .ToQueryString());
        Assert.Equal("conflictTarget", computedConflict.ParamName);
        var emptyValues = Assert.Throws<ArgumentException>(() => context.Documents
            .InsertOnConflictDoNothingReturning(
                () => new ReturningDocument(),
                document => document.Id)
            .ToQueryString());
        Assert.Contains("non-empty entity object initializer", emptyValues.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returning_modifications_execute_and_materialize_without_tracking()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_returning_documents";
            CREATE TABLE "ef_returning_documents" (
                "Id" integer PRIMARY KEY,
                "Category" text NOT NULL,
                "Score" integer NOT NULL,
                UNIQUE ("Category", "Score"));
            INSERT INTO "ef_returning_documents" ("Id", "Category", "Score")
            VALUES
                (1, 'alpha', 10),
                (2, 'alpha', 20),
                (3, 'beta', 30)
            """);

        try
        {
            await using var context = CreateContext(dataSource);
            var inserted = await context.Documents
                .InsertOnConflictDoNothingReturning(
                    () => new ReturningDocument { Id = 4, Category = "gamma", Score = 40 },
                    document => document.Id)
                .Select(document => new ReturningDocumentResult(
                    document.Id,
                    document.Category,
                    document.Score))
                .ToArrayAsync();
            Assert.Equal([new ReturningDocumentResult(4, "gamma", 40)], inserted);

            var ignored = await context.Documents
                .InsertOnConflictDoNothingReturning(
                    () => new ReturningDocument { Id = 4, Category = "ignored", Score = 400 },
                    document => document.Id)
                .ToArrayAsync();
            Assert.Empty(ignored);

            var upserted = await context.Documents
                .InsertOnConflictUpdateReturning(
                    () => new ReturningDocument { Id = 4, Category = "upserted", Score = 41 },
                    document => document.Id,
                    document => new { document.Category, document.Score })
                .Select(document => new ReturningDocumentResult(
                    document.Id,
                    document.Category,
                    document.Score))
                .ToArrayAsync();
            Assert.Equal([new ReturningDocumentResult(4, "upserted", 41)], upserted);
            Assert.Empty(context.ChangeTracker.Entries());

            var compiledUpsert = EF.CompileQuery(
                (ReturningContext database, int id, string category, int score) => database.Documents
                    .AsNoTracking()
                    .InsertOnConflictUpdateReturning(
                        () => new ReturningDocument { Id = id, Category = category, Score = score },
                        document => document.Id,
                        document => new { document.Category, document.Score })
                    .Select(document => new ReturningDocumentResult(
                        document.Id,
                        document.Category,
                        document.Score)));
            Assert.Equal(
                [new ReturningDocumentResult(4, "compiled-upsert", 42)],
                compiledUpsert(context, 4, "compiled-upsert", 42).ToArray());
            Assert.Empty(await context.Documents
                .InsertOnConflictDoNothingReturning(
                    () => new ReturningDocument { Id = 99, Category = "compiled-upsert", Score = 42 },
                    document => new { document.Category, document.Score })
                .ToArrayAsync());

            var increment = 4;
            var updated = await context.Documents
                .Where(document => document.Id == 1)
                .UpdateReturning(setters => setters
                    .SetProperty(document => document.Score, document => document.Score + increment)
                    .SetProperty(document => document.Category, document => "promoted"))
                .Select(document => new ReturningDocumentResult(
                    document.Id,
                    document.Category,
                    document.Score))
                .ToArrayAsync();

            Assert.Equal([new ReturningDocumentResult(1, "promoted", 14)], updated);
            Assert.Empty(context.ChangeTracker.Entries());

            var compiledUpdate = EF.CompileQuery(
                (ReturningContext database, int id, int value) => database.Documents
                    .AsNoTracking()
                    .Where(document => document.Id == id)
                    .UpdateReturning(
                        document => document.Score,
                        document => document.Score + value)
                    .Select(document => new ReturningDocumentResult(
                        document.Id,
                        document.Category,
                        document.Score)));
            Assert.Equal(
                [new ReturningDocumentResult(2, "alpha", 25)],
                compiledUpdate(context, 2, 5).ToArray());

            var compiledDelete = EF.CompileQuery(
                (ReturningContext database, string category) => database.Documents
                    .AsNoTracking()
                    .Where(document => document.Category == category)
                    .DeleteReturning());
            var deleted = compiledDelete(context, "beta").ToArray();
            Assert.Single(deleted);
            Assert.Equal(new ReturningDocumentResult(3, "beta", 30), new ReturningDocumentResult(
                deleted[0].Id,
                deleted[0].Category,
                deleted[0].Score));
            Assert.Empty(context.ChangeTracker.Entries());

            Assert.Equal(
                [
                    new ReturningDocumentResult(1, "promoted", 14),
                    new ReturningDocumentResult(2, "alpha", 25),
                    new ReturningDocumentResult(4, "compiled-upsert", 42),
                ],
                await context.Documents
                    .AsNoTracking()
                    .OrderBy(document => document.Id)
                    .Select(document => new ReturningDocumentResult(
                        document.Id,
                        document.Category,
                        document.Score))
                    .ToArrayAsync());
        }
        finally
        {
            await ExecuteNonQueryAsync(dataSource, "DROP TABLE IF EXISTS \"ef_returning_documents\"");
        }
    }

    private static ReturningContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ReturningContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new ReturningContext(options);
    }

    private static ReturningContext CreateContext(BlueTuskDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<ReturningContext>()
            .UseBlueTusk(dataSource)
            .Options;
        return new ReturningContext(options);
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

    private sealed class ReturningContext(DbContextOptions<ReturningContext> options)
        : DbContext(options)
    {
        public DbSet<ReturningDocument> Documents => Set<ReturningDocument>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<ReturningDocument>().ToTable("ef_returning_documents");
    }

    private sealed class ReturningDocument
    {
        public int Id { get; set; }

        public string Category { get; set; } = "";

        public int Score { get; set; }
    }

    private sealed record ReturningDocumentResult(int Id, string Category, int Score);
}
