using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.ExpressionIndexes;
using BlueTusk.EntityFrameworkCore.ExpressionIndexes.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

#pragma warning disable EF1001 // Tests intentionally exercise provider and design-time infrastructure.

public sealed class ExpressionIndexTests
{
    private const string Schema = "expression_index_tests";
    private const string Table = "documents";
    private const string Index = "documents_search";
    private const string RenamedIndex = "documents_search_v2";
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Metadata_generates_complete_expression_index_SQL_after_its_table()
    {
        using var context = CreateContext<LiveContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .ToArray();
        var tablePosition = Array.FindIndex(
            operations,
            operation => operation is CreateTableOperation { Name: Table });
        var indexPosition = Array.FindIndex(
            operations,
            operation => operation is CreateBlueTuskExpressionIndexOperation);
        Assert.True(tablePosition >= 0 && tablePosition < indexPosition);

        var create = Assert.Single(operations.OfType<CreateBlueTuskExpressionIndexOperation>());
        Assert.Equal(Index, create.Definition.Name);
        Assert.Equal(
            ["(lower(\"title\")) COLLATE \"C\" text_pattern_ops", "\"created_at\" DESC NULLS LAST"],
            create.Definition.KeySql);
        Assert.Equal(["active"], create.Definition.IncludedColumns);
        Assert.False(create.Definition.NullsDistinct);

        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate([create], model)).CommandText;
        Assert.Equal(
            "CREATE UNIQUE INDEX \"documents_search\" ON \"expression_index_tests\".\"documents\" " +
            "USING \"btree\" ((lower(\"title\")) COLLATE \"C\" text_pattern_ops, " +
            "\"created_at\" DESC NULLS LAST) INCLUDE (\"active\") NULLS NOT DISTINCT " +
            "WITH (fillfactor = 80) WHERE \"active\";" + Environment.NewLine,
            sql);
    }

    [Fact]
    public void Differ_renames_equal_indexes_and_recreates_changed_definitions()
    {
        using var sourceContext = CreateContext<LiveContext>(OfflineConnectionString);
        using var renamedContext = CreateContext<RenamedContext>(OfflineConnectionString);
        using var changedContext = CreateContext<ChangedContext>(OfflineConnectionString);
        using var removedContext = CreateContext<NoIndexContext>(OfflineConnectionString);
        var differ = sourceContext.GetService<IMigrationsModelDiffer>();
        var source = sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var rename = Assert.Single(differ.GetDifferences(
                source,
                renamedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .OfType<RenameBlueTuskExpressionIndexOperation>());
        Assert.Equal(Index, rename.Name);
        Assert.Equal(RenamedIndex, rename.NewName);

        var changed = differ.GetDifferences(
            source,
            changedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.True(Assert.Single(changed.OfType<DropBlueTuskExpressionIndexOperation>()).IsDestructiveChange);
        Assert.Equal(
            "NOT \"active\"",
            Assert.Single(changed.OfType<CreateBlueTuskExpressionIndexOperation>()).Definition.PredicateSql);

        Assert.Single(differ.GetDifferences(
                source,
                removedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .OfType<DropBlueTuskExpressionIndexOperation>());
    }

    [Fact]
    public void Invalid_metadata_is_rejected_and_manual_operations_generate_CSharp()
    {
        var modelBuilder = new ModelBuilder();
        ConfigureEntity(modelBuilder);
        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<Document>()
            .HasBlueTuskExpressionIndex("empty", _ => { }));
        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<Document>()
            .HasBlueTuskExpressionIndex(
                "bad_storage",
                index => index.HasKeySql("lower(title)").HasStorageParameter("bad;name", "1")));
        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<Document>()
            .HasBlueTuskExpressionIndex(
                "bad_nulls",
                index => index.HasKeySql("lower(title)").HasNullsDistinct(false)));

        var definition = Definition(Index) with { Tablespace = "fast_indexes", IsConcurrent = true };
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateBlueTuskExpressionIndex(Table, definition, Schema);
        migration.RenameBlueTuskExpressionIndex(Index, RenamedIndex, Schema);
        migration.DropBlueTuskExpressionIndex(RenamedIndex, Schema, concurrently: true);
        using var context = CreateContext<NoIndexContext>(OfflineConnectionString);
        var commands = context.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.Operations, context.GetService<IDesignTimeModel>().Model);
        Assert.Equal(3, commands.Count);
        Assert.True(commands[0].TransactionSuppressed);
        Assert.Contains("TABLESPACE \"fast_indexes\"", commands[0].CommandText, StringComparison.Ordinal);
        Assert.Equal(
            "ALTER INDEX \"expression_index_tests\".\"documents_search\" RENAME TO " +
            "\"documents_search_v2\";" + Environment.NewLine,
            commands[1].CommandText);
        Assert.True(commands[2].TransactionSuppressed);
        Assert.Equal(
            "DROP INDEX CONCURRENTLY \"expression_index_tests\".\"documents_search_v2\" RESTRICT;" +
            Environment.NewLine,
            commands[2].CommandText);

        using var provider = DesignServices();
        var codeBuilder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, codeBuilder);
        var code = codeBuilder.ToString();
        Assert.Contains("CreateBlueTuskExpressionIndex(", code, StringComparison.Ordinal);
        Assert.Contains("RenameBlueTuskExpressionIndex(", code, StringComparison.Ordinal);
        Assert.Contains("DropBlueTuskExpressionIndex(", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expression_index_enforces_round_trips_scaffolds_renames_and_drops_across_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await CleanupAsync(connectionString);

        try
        {
            using var initialContext = CreateContext<LiveContext>(connectionString);
            var initialModel = initialContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(initialContext, null, initialModel, connectionString);
            await ExecuteNonQueryAsync(
                connectionString,
                $"INSERT INTO {Schema}.{Table} (id, title, created_at, active) " +
                "VALUES (1, 'BlueTusk', '2026-08-01T00:00:00Z', true)");
            var duplicate = await Assert.ThrowsAsync<BlueTuskException>(() => ExecuteNonQueryAsync(
                connectionString,
                $"INSERT INTO {Schema}.{Table} (id, title, created_at, active) " +
                "VALUES (2, 'bluetusk', '2026-08-01T00:00:00Z', true)"));
            Assert.Contains(Index, duplicate.Message, StringComparison.Ordinal);
            await ExecuteNonQueryAsync(
                connectionString,
                $"INSERT INTO {Schema}.{Table} (id, title, created_at, active) " +
                "VALUES (3, 'bluetusk', '2026-08-01T00:00:00Z', false)");

            var database = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], [Schema]));
            var table = Assert.Single(database.Tables, item => item.Schema == Schema && item.Name == Table);
            Assert.DoesNotContain(table.Indexes, index => index.Name == Index);
            var discovered = Assert.Single(BlueTuskExpressionIndexMetadata.Deserialize(
                Assert.IsType<string>(table[BlueTuskExpressionIndexMetadata.AnnotationName])));
            Assert.Equal(Index, discovered.Name);
            Assert.Equal("btree", discovered.Method);
            Assert.True(discovered.IsUnique);
            Assert.False(discovered.NullsDistinct);
            Assert.Equal(["active"], discovered.IncludedColumns);
            Assert.Contains("lower(title)", discovered.KeySql[0], StringComparison.Ordinal);
            Assert.Contains("COLLATE \"C\"", discovered.KeySql[0], StringComparison.Ordinal);
            Assert.Contains("text_pattern_ops", discovered.KeySql[0], StringComparison.Ordinal);
            Assert.Contains("created_at", discovered.KeySql[1], StringComparison.Ordinal);
            Assert.Contains("timestamptz_ops", discovered.KeySql[1], StringComparison.Ordinal);
            Assert.Contains("DESC NULLS LAST", discovered.KeySql[1], StringComparison.Ordinal);
            Assert.Equal("active", discovered.PredicateSql);
            Assert.Contains(
                discovered.StorageParameters,
                parameter => parameter.Name == "fillfactor" && parameter.Value == "80");

            var replay = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            replay.CreateBlueTuskExpressionIndex(
                Table,
                discovered with { Name = "documents_search_replayed" },
                Schema);
            replay.DropBlueTuskExpressionIndex("documents_search_replayed", Schema);
            foreach (var command in initialContext.GetService<IMigrationsSqlGenerator>()
                         .Generate(replay.Operations, initialModel))
            {
                await ExecuteNonQueryAsync(connectionString, command.CommandText);
            }

            await using (var provider = DesignServices())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], [Schema]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "ExpressionIndexContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "ExpressionIndexModels",
                        ModelNamespace = "ExpressionIndexModels",
                        RootNamespace = "ExpressionIndexModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains(
                    "HasBlueTuskExpressionIndexes(",
                    scaffolded.ContextFile.Code,
                    StringComparison.Ordinal);
                Assert.Contains(Index, scaffolded.ContextFile.Code, StringComparison.Ordinal);
            }

            using var renamedContext = CreateContext<RenamedContext>(connectionString);
            var renamedModel = renamedContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(renamedContext, initialModel, renamedModel, connectionString);
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                $"SELECT to_regclass('{Schema}.{RenamedIndex}') IS NOT NULL"));

            using var removedContext = CreateContext<NoIndexContext>(connectionString);
            var removedModel = removedContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(removedContext, renamedModel, removedModel, connectionString);
            Assert.Equal(false, await ExecuteScalarAsync(
                connectionString,
                $"SELECT to_regclass('{Schema}.{RenamedIndex}') IS NOT NULL"));
        }
        finally
        {
            await CleanupAsync(connectionString);
        }
    }

    private static BlueTuskExpressionIndexDefinition Definition(
        string name,
        string predicate = "\"active\"") =>
        new(
            name,
            "btree",
            ["(lower(\"title\")) COLLATE \"C\" text_pattern_ops", "\"created_at\" DESC NULLS LAST"],
            ["active"],
            [new BlueTuskExpressionIndexStorageParameterDefinition("fillfactor", "80")],
            IsUnique: true,
            NullsDistinct: false,
            PredicateSql: predicate,
            Tablespace: null,
            IsConcurrent: false);

    private static void ConfigureEntity(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Document>();
        entity.ToTable(Table, Schema);
        entity.HasKey(document => document.Id);
        entity.Property(document => document.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(document => document.Title).HasColumnName("title");
        entity.Property(document => document.CreatedAt).HasColumnName("created_at");
        entity.Property(document => document.Active).HasColumnName("active");
    }

    private static void ConfigureIndex(ModelBuilder modelBuilder, string name, string predicate = "\"active\"")
    {
        ConfigureEntity(modelBuilder);
        var definition = Definition(name, predicate);
        modelBuilder.Entity<Document>().HasBlueTuskExpressionIndex(
            definition.Name,
            index => index.HasKeySql(definition.KeySql.ToArray())
                .UseMethod(definition.Method)
                .IncludeColumns(definition.IncludedColumns.ToArray())
                .IsUnique()
                .HasNullsDistinct(false)
                .HasStorageParameter("fillfactor", "80")
                .HasFilter(definition.PredicateSql));
    }

    private static TContext CreateContext<TContext>(string connectionString)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    private static ServiceProvider DesignServices()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddEntityFrameworkBlueTusk();
        new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
        return services.BuildServiceProvider();
    }

    private static async Task ApplyAsync(
        DbContext context,
        IModel? source,
        IModel target,
        string connectionString)
    {
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            source?.GetRelationalModel(),
            target.GetRelationalModel());
        foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(operations, target))
        {
            await ExecuteNonQueryAsync(connectionString, command.CommandText);
        }
    }

    private static async Task CleanupAsync(string connectionString) =>
        await ExecuteNonQueryAsync(connectionString, $"DROP SCHEMA IF EXISTS {Schema} CASCADE");

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

    private sealed class LiveContext(DbContextOptions<LiveContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureIndex(modelBuilder, Index);
    }

    private sealed class RenamedContext(DbContextOptions<RenamedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureIndex(modelBuilder, RenamedIndex);
    }

    private sealed class ChangedContext(DbContextOptions<ChangedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureIndex(modelBuilder, Index, "NOT \"active\"");
    }

    private sealed class NoIndexContext(DbContextOptions<NoIndexContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntity(modelBuilder);
    }

    private sealed class Document
    {
        public int Id { get; set; }

        public string? Title { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public bool Active { get; set; }
    }
}

#pragma warning restore EF1001
