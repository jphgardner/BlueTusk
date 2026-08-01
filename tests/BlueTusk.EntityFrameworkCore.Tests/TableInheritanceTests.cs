using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.TableInheritance;
using BlueTusk.EntityFrameworkCore.TableInheritance.Internal;
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

public sealed class TableInheritanceTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Model_metadata_generates_ordered_table_inheritance_operations()
    {
        using var context = CreateContext<InheritanceContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .ToArray();
        var additions = operations.OfType<AddBlueTuskTableInheritanceOperation>().ToArray();

        Assert.Equal(2, additions.Length);
        Assert.Equal("base_events", additions[0].ParentTable);
        Assert.Equal("audit_records", additions[1].ParentTable);
        Assert.All(additions, operation => Assert.Equal("inheritance_tests", operation.Schema));
        Assert.True(
            Array.FindLastIndex(operations, operation => operation is CreateTableOperation) <
            Array.FindIndex(operations, operation => operation is AddBlueTuskTableInheritanceOperation));

        var createChild = Assert.Single(
            operations.OfType<CreateTableOperation>(),
            operation => operation.Name == "event_messages");
        var definition = BlueTuskTableInheritanceMetadata.Deserialize(
            Assert.IsType<string>(createChild[BlueTuskTableInheritanceMetadata.AnnotationName]));
        Assert.Equal(["base_events", "audit_records"], definition.Parents.Select(parent => parent.Name));

        var sql = string.Concat(
            context.GetService<IMigrationsSqlGenerator>()
                .Generate(operations, model)
                .Select(command => command.CommandText));
        Assert.Contains(
            "ALTER TABLE \"inheritance_tests\".\"event_messages\" INHERIT \"inheritance_tests\".\"base_events\";",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE \"inheritance_tests\".\"event_messages\" INHERIT \"inheritance_tests\".\"audit_records\";",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Model_differ_handles_removal_reordering_and_parent_renames()
    {
        using var sourceContext = CreateContext<InheritanceContext>(OfflineConnectionString);
        using var singleContext = CreateContext<SingleParentContext>(OfflineConnectionString);
        using var reorderedContext = CreateContext<ReorderedParentsContext>(OfflineConnectionString);
        using var renamedContext = CreateContext<RenamedParentContext>(OfflineConnectionString);
        using var removedContext = CreateContext<NoInheritanceContext>(OfflineConnectionString);
        var differ = sourceContext.GetService<IMigrationsModelDiffer>();
        var source = sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var single = differ.GetDifferences(
            source,
            singleContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Equal("audit_records", Assert.Single(
            single.OfType<RemoveBlueTuskTableInheritanceOperation>()).ParentTable);
        Assert.Empty(single.OfType<AddBlueTuskTableInheritanceOperation>());

        var reordered = differ.GetDifferences(
            source,
            reorderedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Equal(2, reordered.OfType<RemoveBlueTuskTableInheritanceOperation>().Count());
        Assert.Equal(
            ["audit_records", "base_events"],
            reordered.OfType<AddBlueTuskTableInheritanceOperation>()
                .Select(operation => operation.ParentTable));

        var renamed = differ.GetDifferences(
            source,
            renamedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Equal("base_events_renamed", Assert.Single(
            renamed.OfType<RenameTableOperation>()).NewName);
        Assert.Empty(renamed.OfType<AddBlueTuskTableInheritanceOperation>());
        Assert.Empty(renamed.OfType<RemoveBlueTuskTableInheritanceOperation>());

        var removed = differ.GetDifferences(
            source,
            removedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Equal(2, removed.OfType<RemoveBlueTuskTableInheritanceOperation>().Count());
        Assert.Empty(removed.OfType<AddBlueTuskTableInheritanceOperation>());
    }

    [Fact]
    public void Table_inheritance_configuration_validates_empty_duplicate_and_unknown_parents()
    {
        var modelBuilder = new ModelBuilder();
        ConfigureChild(modelBuilder);

        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<EventMessage>()
            .HasBlueTuskTableInheritance([]));
        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<EventMessage>()
            .HasBlueTuskTableInheritance(
                new BlueTuskInheritedTableDefinition("base_events", "inheritance_tests"),
                new BlueTuskInheritedTableDefinition("base_events", "inheritance_tests")));
        Assert.Throws<InvalidOperationException>(() => modelBuilder.Entity<EventMessage>()
            .InheritsFromBlueTuskTable<UnknownParent>());
    }

    [Fact]
    public void Manual_operations_generate_quoted_native_SQL()
    {
        using var context = CreateContext<InheritanceContext>(OfflineConnectionString);
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.AddBlueTuskTableInheritance(
            "event_messages",
            "base_events",
            "inheritance_tests",
            "inheritance_tests");
        migration.RemoveBlueTuskTableInheritance(
            "event_messages",
            "base_events",
            "inheritance_tests",
            "inheritance_tests");

        var commands = context.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.Operations, context.GetService<IDesignTimeModel>().Model);
        Assert.Equal(2, commands.Count);
        Assert.Equal(
            "ALTER TABLE \"inheritance_tests\".\"event_messages\" INHERIT \"inheritance_tests\".\"base_events\";" + Environment.NewLine,
            commands[0].CommandText);
        Assert.Equal(
            "ALTER TABLE \"inheritance_tests\".\"event_messages\" NO INHERIT \"inheritance_tests\".\"base_events\";" + Environment.NewLine,
            commands[1].CommandText);
    }

    [Fact]
    public void Design_time_generator_scaffolds_table_inheritance_operations()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddEntityFrameworkBlueTusk();
        new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = services.BuildServiceProvider();
        var generator = provider.GetRequiredService<ICSharpMigrationOperationGenerator>();
        var builder = new IndentedStringBuilder();

        generator.Generate(
            "migrationBuilder",
            [
                new AddBlueTuskTableInheritanceOperation
                {
                    Table = "event_messages",
                    Schema = "inheritance_tests",
                    ParentTable = "base_events",
                    ParentSchema = "inheritance_tests",
                },
                new RemoveBlueTuskTableInheritanceOperation
                {
                    Table = "event_messages",
                    Schema = "inheritance_tests",
                    ParentTable = "base_events",
                    ParentSchema = "inheritance_tests",
                },
            ],
            builder);

        var code = builder.ToString();
        Assert.Contains("migrationBuilder.AddBlueTuskTableInheritance(\"event_messages\"", code, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.RemoveBlueTuskTableInheritance(\"event_messages\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_inheritance_queries_catalogue_scaffolding_and_lifecycle_round_trip()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS inheritance_tests CASCADE");

        try
        {
            using var context = CreateContext<InheritanceContext>(connectionString);
            var model = context.GetService<IDesignTimeModel>().Model;
            var operations = context.GetService<IMigrationsModelDiffer>()
                .GetDifferences(null, model.GetRelationalModel());
            foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(operations, model))
            {
                await ExecuteNonQueryAsync(connectionString, command.CommandText);
            }

            await ExecuteNonQueryAsync(
                connectionString,
                """
                INSERT INTO inheritance_tests.event_messages (id, actor, payload)
                VALUES (1, 'blue', 'inherited')
                """);
            Assert.Equal(1, await ExecuteInt64Async(
                connectionString,
                "SELECT count(*) FROM inheritance_tests.base_events"));
            Assert.Equal(0, await ExecuteInt64Async(
                connectionString,
                "SELECT count(*) FROM ONLY inheritance_tests.base_events"));
            Assert.Equal(1, await ExecuteInt64Async(
                connectionString,
                "SELECT count(*) FROM inheritance_tests.audit_records"));

            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["inheritance_tests"]));
            Assert.Equal(3, databaseModel.Tables.Count);
            var child = Assert.Single(databaseModel.Tables, table => table.Name == "event_messages");
            var discovered = BlueTuskTableInheritanceMetadata.Deserialize(
                Assert.IsType<string>(child[BlueTuskTableInheritanceMetadata.AnnotationName]));
            Assert.Equal(
                ["base_events", "audit_records"],
                discovered.Parents.Select(parent => parent.Name));
            Assert.All(
                discovered.Parents,
                parent => Assert.Equal("inheritance_tests", parent.Schema));

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var serviceProvider = services.BuildServiceProvider())
            {
                var scaffolded = serviceProvider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], ["inheritance_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "InheritanceContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "InheritanceModels",
                        ModelNamespace = "InheritanceModels",
                        RootNamespace = "InheritanceModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains(
                    "HasBlueTuskTableInheritance(",
                    scaffolded.ContextFile.Code,
                    StringComparison.Ordinal);
                Assert.Equal(3, scaffolded.AdditionalFiles.Count);
            }

            var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            migration.RemoveBlueTuskTableInheritance(
                "event_messages",
                "audit_records",
                "inheritance_tests",
                "inheritance_tests");
            migration.AddBlueTuskTableInheritance(
                "event_messages",
                "audit_records",
                "inheritance_tests",
                "inheritance_tests");
            var lifecycle = context.GetService<IMigrationsSqlGenerator>()
                .Generate(migration.Operations, model);
            await ExecuteNonQueryAsync(connectionString, lifecycle[0].CommandText);
            Assert.Equal(0, await ExecuteInt64Async(
                connectionString,
                "SELECT count(*) FROM inheritance_tests.audit_records"));
            await ExecuteNonQueryAsync(connectionString, lifecycle[1].CommandText);
            Assert.Equal(1, await ExecuteInt64Async(
                connectionString,
                "SELECT count(*) FROM inheritance_tests.audit_records"));
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS inheritance_tests CASCADE");
        }
    }

    private static TContext CreateContext<TContext>(string connectionString)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<long> ExecuteInt64Async(string connectionString, string sql)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None), System.Globalization.CultureInfo.InvariantCulture);
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

    private static void ConfigureBase(ModelBuilder modelBuilder, string tableName = "base_events")
    {
        var entity = modelBuilder.Entity<BaseEvent>();
        entity.ToTable(tableName, "inheritance_tests");
        entity.HasNoKey();
        entity.Property(item => item.Id).HasColumnName("id");
    }

    private static void ConfigureAudit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AuditRecord>();
        entity.ToTable("audit_records", "inheritance_tests");
        entity.HasNoKey();
        entity.Property(item => item.Actor).HasColumnName("actor");
    }

    private static void ConfigureChild(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<EventMessage>();
        entity.ToTable("event_messages", "inheritance_tests");
        entity.HasNoKey();
        entity.Property(item => item.Id).HasColumnName("id");
        entity.Property(item => item.Actor).HasColumnName("actor");
        entity.Property(item => item.Payload).HasColumnName("payload");
    }

    private static void ConfigureTables(ModelBuilder modelBuilder, string baseTableName = "base_events")
    {
        ConfigureBase(modelBuilder, baseTableName);
        ConfigureAudit(modelBuilder);
        ConfigureChild(modelBuilder);
    }

    private sealed class InheritanceContext(DbContextOptions<InheritanceContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureTables(modelBuilder);
            modelBuilder.Entity<EventMessage>()
                .InheritsFromBlueTuskTable<BaseEvent>()
                .InheritsFromBlueTuskTable<AuditRecord>();
        }
    }

    private sealed class SingleParentContext(DbContextOptions<SingleParentContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureTables(modelBuilder);
            modelBuilder.Entity<EventMessage>().InheritsFromBlueTuskTable<BaseEvent>();
        }
    }

    private sealed class ReorderedParentsContext(DbContextOptions<ReorderedParentsContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureTables(modelBuilder);
            modelBuilder.Entity<EventMessage>()
                .InheritsFromBlueTuskTable<AuditRecord>()
                .InheritsFromBlueTuskTable<BaseEvent>();
        }
    }

    private sealed class RenamedParentContext(DbContextOptions<RenamedParentContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureTables(modelBuilder, "base_events_renamed");
            modelBuilder.Entity<EventMessage>()
                .InheritsFromBlueTuskTable<BaseEvent>()
                .InheritsFromBlueTuskTable<AuditRecord>();
        }
    }

    private sealed class NoInheritanceContext(DbContextOptions<NoInheritanceContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureTables(modelBuilder);
    }

    private sealed class BaseEvent
    {
        public int Id { get; set; }
    }

    private sealed class AuditRecord
    {
        public string? Actor { get; set; }
    }

    private sealed class EventMessage
    {
        public int Id { get; set; }

        public string? Actor { get; set; }

        public string? Payload { get; set; }
    }

    private sealed class UnknownParent;
}

#pragma warning restore EF1001
