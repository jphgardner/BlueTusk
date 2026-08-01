using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

#pragma warning disable EF1001 // Tests intentionally exercise provider metadata and design-time services.

public sealed class AdvancedIndexTests
{
    [Fact]
    public void Model_metadata_produces_complete_PostgreSQL_index_operation_and_SQL()
    {
        using var context = CreateContext("Host=localhost;Database=unused;Username=unused;Password=unused");
        var model = context.GetService<IDesignTimeModel>().Model;
        var differ = context.GetService<IMigrationsModelDiffer>();
        var operation = Assert.Single(
            differ.GetDifferences(null, model.GetRelationalModel()).OfType<CreateIndexOperation>());

        Assert.Equal("btree", operation[BlueTuskIndexAnnotations.Method]);
        Assert.Equal<string?[]>(
            ["text_pattern_ops", null],
            Assert.IsType<string[]>(operation[BlueTuskIndexAnnotations.OperatorClasses]));
        Assert.Equal<string?[]>(
            ["C", null],
            Assert.IsType<string[]>(operation[BlueTuskIndexAnnotations.Collations]));
        Assert.Equal(
            [(int)BlueTuskIndexNullSortOrder.NullsFirst, (int)BlueTuskIndexNullSortOrder.NullsLast],
            Assert.IsType<int[]>(operation[BlueTuskIndexAnnotations.NullSortOrders]));
        Assert.Equal(
            ["search_vector"],
            Assert.IsType<string[]>(operation[BlueTuskIndexAnnotations.IncludeProperties]));
        Assert.Equal(
            ["lower(\"title\")", string.Empty],
            Assert.IsType<string[]>(operation[BlueTuskIndexAnnotations.Expressions]));
        Assert.Equal(false, operation[BlueTuskIndexAnnotations.NullsDistinct]);
        Assert.Equal(true, operation[BlueTuskIndexAnnotations.IsConcurrent]);

        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], model));
        Assert.True(command.TransactionSuppressed);
        Assert.Equal(
            "CREATE UNIQUE INDEX CONCURRENTLY \"ix_advanced_documents_title_created\" ON \"advanced_documents\" USING \"btree\" ((lower(\"title\")) COLLATE \"C\" \"text_pattern_ops\" NULLS FIRST, \"created_at\" DESC NULLS LAST) INCLUDE (\"search_vector\") NULLS NOT DISTINCT WITH (fillfactor = 80) WHERE \"title\" IS NOT NULL;" + Environment.NewLine,
            command.CommandText);
    }

    [Fact]
    public void Index_configuration_rejects_ambiguous_or_unsafe_metadata()
    {
        var modelBuilder = new ModelBuilder();
        var entity = modelBuilder.Entity<AdvancedDocument>();
        var index = entity.HasIndex(document => new { document.Title, document.CreatedAt });

        Assert.Throws<ArgumentException>(() => index.UseBlueTuskIndexMethod("public.btree"));
        Assert.Throws<ArgumentException>(() => index.UseBlueTuskOperatorClass("text_ops", null, "extra"));
        Assert.Throws<ArgumentException>(() => index.HasBlueTuskStorageParameter("fillfactor;drop", "80"));
        Assert.Throws<ArgumentException>(() => index.HasBlueTuskStorageParameter("fillfactor", "80;drop"));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.HasBlueTuskFillFactor(9));
        Assert.Throws<ArgumentException>(() => index.IncludeProperties(document => document.Title));
        Assert.Throws<ArgumentException>(() => index.HasBlueTuskIndexExpressions(" "));
    }

    [Fact]
    public void Concurrent_drop_is_transaction_suppressed()
    {
        using var context = CreateContext("Host=localhost;Database=unused;Username=unused;Password=unused");
        var operation = new DropIndexOperation
        {
            Name = "ix_advanced_documents_title_created",
        };
        operation[BlueTuskIndexAnnotations.IsConcurrent] = true;

        var command = Assert.Single(
            context.GetService<IMigrationsSqlGenerator>().Generate(
                [operation],
                context.GetService<IDesignTimeModel>().Model));

        Assert.True(command.TransactionSuppressed);
        Assert.Equal(
            "DROP INDEX CONCURRENTLY \"ix_advanced_documents_title_created\";" + Environment.NewLine,
            command.CommandText);
    }

    [Fact]
    public void Model_differ_preserves_concurrency_on_drop()
    {
        using var sourceContext = CreateContext("Host=localhost;Database=unused;Username=unused;Password=unused");
        using var targetContext = CreateContextWithoutIndex("Host=localhost;Database=unused;Username=unused;Password=unused");
        var sourceModel = sourceContext.GetService<IDesignTimeModel>().Model;
        var targetModel = targetContext.GetService<IDesignTimeModel>().Model;
        var operation = Assert.Single(
            sourceContext.GetService<IMigrationsModelDiffer>()
                .GetDifferences(sourceModel.GetRelationalModel(), targetModel.GetRelationalModel())
                .OfType<DropIndexOperation>());

        Assert.Equal(true, operation[BlueTuskIndexAnnotations.IsConcurrent]);
        var command = Assert.Single(
            sourceContext.GetService<IMigrationsSqlGenerator>().Generate([operation], targetModel));
        Assert.True(command.TransactionSuppressed);
        Assert.Equal(
            "DROP INDEX CONCURRENTLY \"ix_advanced_documents_title_created\";" + Environment.NewLine,
            command.CommandText);
    }

    [Fact]
    public async Task Advanced_index_round_trips_on_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS advanced_documents");

        try
        {
            await ExecuteNonQueryAsync(
                connectionString,
                """
                CREATE TABLE advanced_documents (
                    id integer PRIMARY KEY,
                    title text,
                    created_at timestamp with time zone NOT NULL,
                    search_vector text)
                """);

            using var context = CreateContext(connectionString);
            var model = context.GetService<IDesignTimeModel>().Model;
            var operation = Assert.Single(
                context.GetService<IMigrationsModelDiffer>()
                    .GetDifferences(null, model.GetRelationalModel())
                    .OfType<CreateIndexOperation>());
            var createCommand = Assert.Single(
                context.GetService<IMigrationsSqlGenerator>().Generate([operation], model));
            await ExecuteNonQueryAsync(connectionString, createCommand.CommandText);

            var definition = Assert.IsType<string>(await ExecuteScalarAsync(
                connectionString,
                "SELECT pg_get_indexdef('ix_advanced_documents_title_created'::regclass)"));
            Assert.Contains("USING btree", definition, StringComparison.Ordinal);
            Assert.Contains("lower(title)", definition, StringComparison.Ordinal);
            Assert.Contains("COLLATE \"C\"", definition, StringComparison.Ordinal);
            Assert.Contains("text_pattern_ops", definition, StringComparison.Ordinal);
            Assert.Contains("NULLS FIRST", definition, StringComparison.Ordinal);
            Assert.Contains("created_at DESC", definition, StringComparison.Ordinal);
            Assert.Contains("DESC NULLS LAST", definition, StringComparison.Ordinal);
            Assert.Contains("INCLUDE (search_vector)", definition, StringComparison.Ordinal);
            Assert.Contains("NULLS NOT DISTINCT", definition, StringComparison.Ordinal);
            Assert.Contains("fillfactor='80'", definition, StringComparison.Ordinal);
            Assert.Contains("WHERE (title IS NOT NULL)", definition, StringComparison.Ordinal);

            var drop = new DropIndexOperation { Name = operation.Name };
            drop[BlueTuskIndexAnnotations.IsConcurrent] = true;
            var dropCommand = Assert.Single(
                context.GetService<IMigrationsSqlGenerator>().Generate([drop], model));
            await ExecuteNonQueryAsync(connectionString, dropCommand.CommandText);
            Assert.True(await ExecuteScalarAsync(
                connectionString,
                "SELECT to_regclass('ix_advanced_documents_title_created') IS NULL") is true);
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS advanced_documents");
        }
    }

    private static AdvancedIndexContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AdvancedIndexContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new AdvancedIndexContext(options);
    }

    private static AdvancedIndexWithoutIndexContext CreateContextWithoutIndex(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AdvancedIndexWithoutIndexContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new AdvancedIndexWithoutIndexContext(options);
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<object?> ExecuteScalarAsync(string connectionString, string sql)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None);
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

    private sealed class AdvancedIndexContext(DbContextOptions<AdvancedIndexContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => ConfigureModel(modelBuilder, includeIndex: true);
    }

    private sealed class AdvancedIndexWithoutIndexContext(DbContextOptions<AdvancedIndexWithoutIndexContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => ConfigureModel(modelBuilder, includeIndex: false);
    }

    private static void ConfigureModel(ModelBuilder modelBuilder, bool includeIndex)
    {
        var entity = modelBuilder.Entity<AdvancedDocument>();
        entity.ToTable("advanced_documents");
        entity.HasKey(document => document.Id);
        entity.Property(document => document.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(document => document.Title).HasColumnName("title");
        entity.Property(document => document.CreatedAt).HasColumnName("created_at");
        entity.Property(document => document.SearchVector).HasColumnName("search_vector");
        if (includeIndex)
        {
            entity.HasIndex(document => new { document.Title, document.CreatedAt })
                .HasDatabaseName("ix_advanced_documents_title_created")
                .IsUnique()
                .IsDescending(false, true)
                .HasFilter("\"title\" IS NOT NULL")
                .UseBlueTuskIndexMethod("btree")
                .UseBlueTuskOperatorClass("text_pattern_ops", null)
                .UseBlueTuskCollation("C", null)
                .HasBlueTuskNullSortOrder(
                    BlueTuskIndexNullSortOrder.NullsFirst,
                    BlueTuskIndexNullSortOrder.NullsLast)
                .IncludeProperties(document => document.SearchVector)
                .HasBlueTuskStorageParameter("fillfactor", "80")
                .HasBlueTuskNullsDistinct(false)
                .HasBlueTuskIndexExpressions("lower(\"title\")", null)
                .IsBlueTuskConcurrent();
        }
    }

    private sealed class AdvancedDocument
    {
        public int Id { get; set; }

        public string? Title { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public string? SearchVector { get; set; }
    }
}

#pragma warning restore EF1001
