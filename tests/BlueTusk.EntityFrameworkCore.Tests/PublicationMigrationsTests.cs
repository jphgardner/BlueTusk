using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Publications;
using BlueTusk.EntityFrameworkCore.Publications.Internal;
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

#pragma warning disable EF1001

public sealed class PublicationMigrationsTests
{
    private const string Offline = "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Publication_SQL_diffs_ordering_version_guards_and_generated_CSharp_are_preserved()
    {
        using var initial = Create<PublicationContext>(Offline);
        using var changed = Create<ChangedPublicationContext>(Offline);
        using var renamed = Create<RenamedPublicationContext>(Offline);
        using var removed = Create<RemovedPublicationContext>(Offline);
        var model = initial.GetService<IDesignTimeModel>().Model;
        var differ = initial.GetService<IMigrationsModelDiffer>();
        var creates = differ.GetDifferences(null, model.GetRelationalModel()).ToArray();
        var create = Assert.Single(creates.OfType<CreatePublicationOperation>());
        Assert.True(Array.FindLastIndex(creates, operation => operation is CreateTableOperation) <
                    Array.FindIndex(creates, operation => operation is CreatePublicationOperation));
        var sql = Assert.Single(initial.GetService<IMigrationsSqlGenerator>().Generate([create], model)).CommandText;
        Assert.Contains(
            "CREATE PUBLICATION \"documents_publication\" FOR TABLE ONLY " +
            "\"publication_tests\".\"documents\" (\"id\", \"note\") WHERE (id > 0)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("publish = 'insert, update'", sql, StringComparison.Ordinal);
        Assert.Contains("publish_via_partition_root = true", sql, StringComparison.Ordinal);

        var alters = differ.GetDifferences(
            model.GetRelationalModel(),
            changed.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Single(alters.OfType<AlterPublicationOperation>());
        Assert.Empty(alters.OfType<DropPublicationOperation>());
        var renameOperations = differ.GetDifferences(
            changed.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            renamed.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Single(renameOperations.OfType<RenamePublicationOperation>());

        var removals = differ.GetDifferences(
            model.GetRelationalModel(),
            removed.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();
        Assert.True(Array.FindIndex(removals, operation => operation is DropPublicationOperation) <
                    Array.FindIndex(removals, operation => operation is DropTableOperation));
        var drop = Assert.Single(removals.OfType<DropPublicationOperation>());
        Assert.True(drop.IsDestructiveChange);
        Assert.Contains(
            " RESTRICT",
            Assert.Single(initial.GetService<IMigrationsSqlGenerator>().Generate([drop], model)).CommandText,
            StringComparison.Ordinal);

        var generatedDefinition = create.Definition with
        {
            GeneratedColumns = BlueTuskPublicationGeneratedColumns.Stored,
        };
        var generatedSql = Assert.Single(initial.GetService<IMigrationsSqlGenerator>().Generate(
            [new CreatePublicationOperation { Definition = generatedDefinition }], model)).CommandText;
        Assert.Contains("server_version_num')::integer < 180000", generatedSql, StringComparison.Ordinal);
        Assert.Contains("publish_generated_columns = stored", generatedSql, StringComparison.Ordinal);

        var allSequencesDefinition = new BlueTuskPublicationDefinition(
            "all_data",
            [new BlueTuskPublicationTableDefinition(
                "audit",
                "publication_tests",
                IncludeDescendants: false,
                Columns: null,
                RowFilterSql: null,
                IsExcluded: true)],
            Schemas: [],
            AllTables: true,
            AllSequences: true,
            BlueTuskPublicationOperations.All,
            PublishViaPartitionRoot: false,
            BlueTuskPublicationGeneratedColumns.None);
        var version19Sql = Assert.Single(initial.GetService<IMigrationsSqlGenerator>().Generate(
            [new CreatePublicationOperation { Definition = allSequencesDefinition }], model)).CommandText;
        Assert.Contains("server_version_num')::integer < 190000", version19Sql, StringComparison.Ordinal);
        Assert.Contains("ALL TABLES EXCEPT (TABLE ONLY", version19Sql, StringComparison.Ordinal);
        Assert.Contains("ALL SEQUENCES", version19Sql, StringComparison.Ordinal);

        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreatePublication(create.Definition);
        migration.AlterPublication(create.Definition, generatedDefinition);
        migration.RenamePublication("documents_publication", "documents_publication_v2");
        migration.DropPublication("documents_publication_v2");
        using var provider = DesignServices();
        var builder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, builder);
        var code = builder.ToString();
        Assert.Contains("CreatePublication", code, StringComparison.Ordinal);
        Assert.Contains("AlterPublication", code, StringComparison.Ordinal);
        Assert.Contains("RenamePublication", code, StringComparison.Ordinal);
        Assert.Contains("DropPublication", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_publication_combinations_are_rejected()
    {
        var model = new ModelBuilder();
        Assert.Throws<ArgumentException>(() => model.HasPublication(
            "invalid",
            publication => publication
                .ForAllTables()
                .ForTable("documents", "publication_tests")));
        Assert.Throws<ArgumentException>(() => model.HasPublication(
            "invalid_columns",
            publication => publication
                .ForTable("documents", "publication_tests", table => table.HasColumns("id"))
                .ForTablesInSchema("publication_tests")));
        Assert.Throws<ArgumentException>(() => model.HasPublication(
            "invalid_except",
            publication => publication.ExceptTable("documents", "publication_tests")));
    }

    [Fact]
    public async Task Publications_round_trip_alter_rename_and_drop_across_PostgreSQL()
    {
        var cs = ConnectionString();
        await Cleanup(cs);
        try
        {
            using var initial = Create<PublicationContext>(cs);
            var initialModel = initial.GetService<IDesignTimeModel>().Model;
            await Apply(initial, null, initialModel, cs);

            var database = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], ["publication_tests"]));
            var definitions = BlueTuskPublicationMetadata.Deserialize(Assert.IsType<string>(
                database[BlueTuskPublicationMetadata.AnnotationName]));
            var discovered = Assert.Single(
                definitions.Publications,
                publication => publication.Name == "documents_publication");
            Assert.Equal(
                BlueTuskPublicationOperations.Insert | BlueTuskPublicationOperations.Update,
                discovered.Operations);
            Assert.True(discovered.PublishViaPartitionRoot);
            var table = Assert.Single(discovered.Tables);
            Assert.Equal(["id", "note"], table.Columns);
            Assert.Equal("(id > 0)", table.RowFilterSql);

            var schemaPublication = new BlueTuskPublicationDefinition(
                "schema_publication",
                Tables: [],
                Schemas: ["publication_tests"],
                AllTables: false,
                AllSequences: false,
                BlueTuskPublicationOperations.Insert,
                PublishViaPartitionRoot: false,
                BlueTuskPublicationGeneratedColumns.None);
            await ExecuteOperations(
                initial,
                [new CreatePublicationOperation { Definition = schemaPublication }],
                cs);
            var schemaDatabase = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], ["publication_tests"]));
            var schemaDefinitions = BlueTuskPublicationMetadata.Deserialize(Assert.IsType<string>(
                schemaDatabase[BlueTuskPublicationMetadata.AnnotationName]));
            Assert.Equal(
                ["publication_tests"],
                Assert.Single(schemaDefinitions.Publications,
                    publication => publication.Name == schemaPublication.Name).Schemas);
            await Execute(cs, "DROP PUBLICATION schema_publication");

            using (var provider = DesignServices())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    cs,
                    new DatabaseModelFactoryOptions([], ["publication_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "PublicationContext",
                        ConnectionString = cs,
                        ContextNamespace = "PublicationModels",
                        ModelNamespace = "PublicationModels",
                        RootNamespace = "PublicationModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasPublications(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
            }

            using var changed = Create<ChangedPublicationContext>(cs);
            var changedModel = changed.GetService<IDesignTimeModel>().Model;
            await Apply(changed, initialModel, changedModel, cs);
            Assert.Equal("(id > 10)", await Scalar(
                cs,
                "SELECT rowfilter FROM pg_catalog.pg_publication_tables " +
                "WHERE pubname = 'documents_publication'"));

            using var renamed = Create<RenamedPublicationContext>(cs);
            var renamedModel = renamed.GetService<IDesignTimeModel>().Model;
            await Apply(renamed, changedModel, renamedModel, cs);
            Assert.Equal(true, await Scalar(
                cs,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_publication " +
                "WHERE pubname = 'documents_publication_v2')"));

            var version = Convert.ToInt32(await Scalar(cs, "SHOW server_version_num"),
                System.Globalization.CultureInfo.InvariantCulture);
            if (version >= 180000)
            {
                var generated = new BlueTuskPublicationDefinition(
                    "generated_columns_publication",
                    [new BlueTuskPublicationTableDefinition(
                        "documents", "publication_tests", false, null, null)],
                    [], false, false, BlueTuskPublicationOperations.Insert, false,
                    BlueTuskPublicationGeneratedColumns.Stored);
                await ExecuteOperations(initial, [new CreatePublicationOperation { Definition = generated }], cs);
                var generatedDatabase = new BlueTuskDatabaseModelFactory().Create(
                    cs,
                    new DatabaseModelFactoryOptions([], ["publication_tests"]));
                var generatedDefinitions = BlueTuskPublicationMetadata.Deserialize(Assert.IsType<string>(
                    generatedDatabase[BlueTuskPublicationMetadata.AnnotationName]));
                Assert.Equal(
                    BlueTuskPublicationGeneratedColumns.Stored,
                    Assert.Single(generatedDefinitions.Publications,
                        publication => publication.Name == generated.Name).GeneratedColumns);
                await Execute(cs, "DROP PUBLICATION generated_columns_publication");
            }

            if (version >= 190000)
            {
                var allData = new BlueTuskPublicationDefinition(
                    "all_data_publication",
                    [new BlueTuskPublicationTableDefinition(
                        "audit", "publication_tests", false, null, null, IsExcluded: true)],
                    [], true, true, BlueTuskPublicationOperations.All, false,
                    BlueTuskPublicationGeneratedColumns.None);
                await ExecuteOperations(initial, [new CreatePublicationOperation { Definition = allData }], cs);
                var allDataDatabase = new BlueTuskDatabaseModelFactory().Create(
                    cs,
                    new DatabaseModelFactoryOptions([], ["publication_tests"]));
                var allDataDefinitions = BlueTuskPublicationMetadata.Deserialize(Assert.IsType<string>(
                    allDataDatabase[BlueTuskPublicationMetadata.AnnotationName]));
                var allDataRoundTrip = Assert.Single(allDataDefinitions.Publications,
                    publication => publication.Name == allData.Name);
                Assert.True(allDataRoundTrip.AllTables);
                Assert.True(allDataRoundTrip.AllSequences);
                Assert.True(Assert.Single(allDataRoundTrip.Tables).IsExcluded);
                var changedAllData = allData with
                {
                    Tables = [new BlueTuskPublicationTableDefinition(
                        "documents", "publication_tests", false, null, null, IsExcluded: true)],
                };
                await ExecuteOperations(initial,
                    [new AlterPublicationOperation
                    {
                        OldDefinition = allData,
                        Definition = changedAllData,
                    }],
                    cs);
                var changedAllDataDatabase = new BlueTuskDatabaseModelFactory().Create(
                    cs,
                    new DatabaseModelFactoryOptions([], ["publication_tests"]));
                var changedAllDataDefinitions = BlueTuskPublicationMetadata.Deserialize(Assert.IsType<string>(
                    changedAllDataDatabase[BlueTuskPublicationMetadata.AnnotationName]));
                Assert.Equal(
                    "documents",
                    Assert.Single(Assert.Single(changedAllDataDefinitions.Publications,
                        publication => publication.Name == allData.Name).Tables).Name);
                await Execute(cs, "DROP PUBLICATION all_data_publication");
            }

            using var noPublication = Create<NoPublicationContext>(cs);
            await Apply(noPublication, renamedModel, noPublication.GetService<IDesignTimeModel>().Model, cs);
            Assert.Equal(false, await Scalar(
                cs,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_publication " +
                "WHERE pubname = 'documents_publication_v2')"));
        }
        finally
        {
            await Cleanup(cs);
        }
    }

    private static void ConfigureEntities(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<Document>();
        document.ToTable("documents", "publication_tests");
        document.HasKey(item => item.Id);
        document.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        document.Property(item => item.Note).HasColumnName("note");
        var audit = modelBuilder.Entity<Audit>();
        audit.ToTable("audit", "publication_tests");
        audit.HasKey(item => item.Id);
        audit.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
    }

    private static void ConfigurePublication(
        ModelBuilder modelBuilder,
        string name,
        string filter,
        BlueTuskPublicationOperations operations,
        bool viaRoot)
    {
        ConfigureEntities(modelBuilder);
        modelBuilder.HasPublication(name, publication => publication
            .ForTable("documents", "publication_tests", table => table
                .HasColumns("id", "note")
                .HasRowFilter(filter))
            .Publishes(operations)
            .PublishViaPartitionRoot(viaRoot));
    }

    private static T Create<T>(string cs) where T : DbContext =>
        (T)Activator.CreateInstance(typeof(T), new DbContextOptionsBuilder<T>().UseBlueTusk(cs).Options)!;

    private static ServiceProvider DesignServices()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddEntityFrameworkBlueTusk();
        new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
        return services.BuildServiceProvider();
    }

    private static async Task Apply(DbContext context, IModel? source, IModel target, string cs)
    {
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            source?.GetRelationalModel(), target.GetRelationalModel());
        await ExecuteOperations(context, operations, cs);
    }

    private static async Task ExecuteOperations(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        string cs)
    {
        foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(operations))
        {
            await Execute(cs, command.CommandText);
        }
    }

    private static async Task Execute(string cs, string sql)
    {
        await using var connection = new BlueTuskConnection(cs);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<object?> Scalar(string cs, string sql)
    {
        await using var connection = new BlueTuskConnection(cs);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async Task Cleanup(string cs) => await Execute(
        cs,
        "DROP PUBLICATION IF EXISTS documents_publication; " +
        "DROP PUBLICATION IF EXISTS documents_publication_v2; " +
        "DROP PUBLICATION IF EXISTS generated_columns_publication; " +
        "DROP PUBLICATION IF EXISTS all_data_publication; " +
        "DROP PUBLICATION IF EXISTS schema_publication; " +
        "DROP SCHEMA IF EXISTS publication_tests CASCADE");

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(value)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }

    private sealed class PublicationContext(DbContextOptions<PublicationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigurePublication(
            modelBuilder,
            "documents_publication",
            "id > 0",
            BlueTuskPublicationOperations.Insert | BlueTuskPublicationOperations.Update,
            viaRoot: true);
    }

    private sealed class ChangedPublicationContext(DbContextOptions<ChangedPublicationContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigurePublication(
            modelBuilder,
            "documents_publication",
            "id > 10",
            BlueTuskPublicationOperations.Insert,
            viaRoot: false);
    }

    private sealed class RenamedPublicationContext(DbContextOptions<RenamedPublicationContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigurePublication(
            modelBuilder,
            "documents_publication_v2",
            "id > 10",
            BlueTuskPublicationOperations.Insert,
            viaRoot: false);
    }

    private sealed class NoPublicationContext(DbContextOptions<NoPublicationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
    }

    private sealed class RemovedPublicationContext(DbContextOptions<RemovedPublicationContext> options)
        : DbContext(options);

    private sealed class Document { public int Id { get; set; } public string? Note { get; set; } }
    private sealed class Audit { public int Id { get; set; } }
}

#pragma warning restore EF1001
