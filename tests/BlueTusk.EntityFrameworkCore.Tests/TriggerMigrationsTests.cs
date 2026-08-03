using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Triggers;
using BlueTusk.EntityFrameworkCore.Triggers.Internal;
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

public sealed class TriggerMigrationsTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Structured_trigger_generates_complete_quoted_SQL_and_dependency_ordering()
    {
        using var context = CreateContext<TriggerContext>(OfflineConnectionString);
        using var noTriggerContext = CreateContext<NoTriggerNoFunctionContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var differ = context.GetService<IMigrationsModelDiffer>();
        var creates = differ.GetDifferences(null, model.GetRelationalModel()).ToArray();
        var triggerIndex = Array.FindIndex(creates, item => item is CreateTriggerOperation);
        Assert.True(Array.FindIndex(creates, item => item is CreateRoutineOperation) < triggerIndex);
        Assert.True(Array.FindIndex(creates, item => item is CreateTableOperation) < triggerIndex);

        var create = Assert.Single(creates.OfType<CreateTriggerOperation>());
        var sql = string.Concat(context.GetService<IMigrationsSqlGenerator>()
            .Generate([create], model)
            .Select(command => command.CommandText));
        Assert.Equal(
            "CREATE TRIGGER \"normalize_note\" BEFORE INSERT OR UPDATE OF \"note\" ON " +
            "\"trigger_tests\".\"documents\" FOR EACH ROW WHEN (NEW.note IS NOT NULL) " +
            "EXECUTE FUNCTION \"trigger_tests\".\"normalize_document_note\"('suffix', 'quoted''value');" +
            Environment.NewLine +
            "ALTER TABLE \"trigger_tests\".\"documents\" ENABLE ALWAYS TRIGGER \"normalize_note\";" +
            Environment.NewLine,
            sql);

        var drops = differ.GetDifferences(
                model.GetRelationalModel(),
                noTriggerContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .ToArray();
        Assert.True(
            Array.FindIndex(drops, item => item is DropTriggerOperation) <
            Array.FindIndex(drops, item => item is DropRoutineOperation));
        Assert.True(Assert.Single(drops.OfType<DropTriggerOperation>()).IsDestructiveChange);
    }

    [Fact]
    public void Transition_and_constraint_trigger_forms_generate_all_clauses()
    {
        using var context = CreateContext<NoTriggerNoFunctionContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var transition = new BlueTuskTriggerDefinition(
            "collect_changes",
            BlueTuskTriggerTiming.After,
            [new BlueTuskTriggerEventDefinition(BlueTuskTriggerEventKind.Update, [])],
            BlueTuskTriggerOrientation.Statement,
            "collect_document_changes",
            "trigger_tests",
            [],
            null,
            "old_rows",
            "new_rows",
            false,
            null,
            null,
            false,
            false,
            BlueTuskTriggerEnabledMode.Origin,
            ExtensionDependency: "audit_extension");
        var constraint = new BlueTuskTriggerDefinition(
            "document_check",
            BlueTuskTriggerTiming.After,
            [new BlueTuskTriggerEventDefinition(BlueTuskTriggerEventKind.Insert, [])],
            BlueTuskTriggerOrientation.Row,
            "check_document",
            "trigger_tests",
            [],
            null,
            null,
            null,
            true,
            "document_groups",
            "trigger_tests",
            true,
            true,
            BlueTuskTriggerEnabledMode.Replica);
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateTrigger("documents", transition, "trigger_tests", orReplace: true);
        migration.CreateTrigger("documents", constraint, "trigger_tests");
        var sql = string.Concat(context.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.Operations, model)
            .Select(command => command.CommandText));

        Assert.Contains(
            "CREATE OR REPLACE TRIGGER \"collect_changes\" AFTER UPDATE ON \"trigger_tests\".\"documents\" " +
            "REFERENCING OLD TABLE AS \"old_rows\" NEW TABLE AS \"new_rows\" FOR EACH STATEMENT " +
            "EXECUTE FUNCTION \"trigger_tests\".\"collect_document_changes\"()",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE CONSTRAINT TRIGGER \"document_check\" AFTER INSERT ON \"trigger_tests\".\"documents\" " +
            "FROM \"trigger_tests\".\"document_groups\" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW " +
            "EXECUTE FUNCTION \"trigger_tests\".\"check_document\"()",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TRIGGER \"collect_changes\" ON \"trigger_tests\".\"documents\" " +
            "DEPENDS ON EXTENSION \"audit_extension\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("ENABLE REPLICA TRIGGER \"document_check\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Differ_renames_and_alters_enabled_mode_without_recreating_body()
    {
        using var sourceContext = CreateContext<TriggerContext>(OfflineConnectionString);
        using var targetContext = CreateContext<RenamedDisabledTriggerContext>(OfflineConnectionString);
        var operations = sourceContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            targetContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        var rename = Assert.Single(operations.OfType<RenameTriggerOperation>());
        Assert.Equal("normalize_note", rename.Name);
        Assert.Equal("normalize_note_v2", rename.NewName);
        var mode = Assert.Single(operations.OfType<AlterTriggerEnabledModeOperation>());
        Assert.Equal("normalize_note_v2", mode.Name);
        Assert.Equal(BlueTuskTriggerEnabledMode.Disabled, mode.EnabledMode);
        Assert.Empty(operations.OfType<CreateTriggerOperation>());
        Assert.Empty(operations.OfType<DropTriggerOperation>());
    }

    [Fact]
    public void Invalid_trigger_combinations_are_rejected()
    {
        var modelBuilder = new ModelBuilder();
        ConfigureEntity(modelBuilder);
        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<Document>().HasTrigger(
            "empty",
            trigger => trigger.ExecuteFunction("noop")));
        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<Document>().HasTrigger(
            "truncate_row",
            trigger => trigger.OnTruncate().ForEachRow().ExecuteFunction("noop")));
        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<Document>().HasTrigger(
            "transition_columns",
            trigger => trigger
                .UseTiming(BlueTuskTriggerTiming.After)
                .OnUpdate(item => item.Note)
                .Referencing(newTable: "new_rows")
                .ExecuteFunction("noop")));
        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<Document>().HasTrigger(
            "invalid_constraint",
            trigger => trigger
                .OnInsert()
                .ForEachRow()
                .AsConstraint(deferrable: false, initiallyDeferred: true)
                .ExecuteFunction("noop")));
    }

    [Fact]
    public void Manual_lifecycle_operations_generate_CSharp_and_default_RESTRICT_drop()
    {
        using var context = CreateContext<TriggerContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var definition = Assert.Single(
            model.FindEntityType(typeof(Document))!.GetTriggerDefinitions());
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateTrigger("documents", definition, "trigger_tests", orReplace: true);
        migration.RenameTrigger("documents", "normalize_note", "normalize_note_v2", "trigger_tests");
        migration.AlterTriggerEnabledMode(
            "documents",
            "normalize_note_v2",
            BlueTuskTriggerEnabledMode.Replica,
            "trigger_tests");
        migration.DropTrigger("documents", "normalize_note_v2", "trigger_tests");
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.Operations, model);
        Assert.Equal(
            "DROP TRIGGER \"normalize_note_v2\" ON \"trigger_tests\".\"documents\" RESTRICT;" +
            Environment.NewLine,
            commands[^1].CommandText);

        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddEntityFrameworkBlueTusk();
        new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = services.BuildServiceProvider();
        var builder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, builder);
        var code = builder.ToString();
        Assert.Contains("CreateTrigger", code, StringComparison.Ordinal);
        Assert.Contains("RenameTrigger", code, StringComparison.Ordinal);
        Assert.Contains("AlterTriggerEnabledMode", code, StringComparison.Ordinal);
        Assert.Contains("DropTrigger", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trigger_executes_round_trips_scaffolds_renames_disables_and_drops_across_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await CleanupAsync(connectionString);
        try
        {
            using var initialContext = CreateContext<TriggerContext>(connectionString);
            var initialModel = initialContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(initialContext, null, initialModel, connectionString);
            await ExecuteNonQueryAsync(
                connectionString,
                "INSERT INTO trigger_tests.documents (id, note) VALUES (1, 'hello')");
            Assert.Equal("HELLO:suffix", await ExecuteScalarAsync(
                connectionString,
                "SELECT note FROM trigger_tests.documents WHERE id = 1"));

            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["trigger_tests"]));
            var table = Assert.Single(
                databaseModel.Tables,
                item => item.Schema == "trigger_tests" && item.Name == "documents");
            var discovered = Assert.Single(BlueTuskTriggerMetadata.Deserialize(
                Assert.IsType<string>(table[BlueTuskTriggerMetadata.AnnotationName])));
            Assert.Equal("normalize_note", discovered.Name);
            Assert.Equal(BlueTuskTriggerEnabledMode.Always, discovered.EnabledMode);
            Assert.StartsWith("CREATE TRIGGER normalize_note", discovered.CanonicalCreateSql, StringComparison.Ordinal);
            Assert.Contains("UPDATE OF note", discovered.CanonicalCreateSql, StringComparison.Ordinal);
            Assert.Contains("EXECUTE FUNCTION trigger_tests.normalize_document_note", discovered.CanonicalCreateSql, StringComparison.Ordinal);

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var provider = services.BuildServiceProvider())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], ["trigger_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "TriggerContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "TriggerModels",
                        ModelNamespace = "TriggerModels",
                        RootNamespace = "TriggerModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasTriggers(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
            }

            using var disabledContext = CreateContext<RenamedDisabledTriggerContext>(connectionString);
            var disabledModel = disabledContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(disabledContext, initialModel, disabledModel, connectionString);
            Assert.Equal("D", await ExecuteScalarAsync(
                connectionString,
                "SELECT tgenabled::text FROM pg_trigger WHERE tgname = 'normalize_note_v2'"));
            await ExecuteNonQueryAsync(
                connectionString,
                "INSERT INTO trigger_tests.documents (id, note) VALUES (2, 'unchanged')");
            Assert.Equal("unchanged", await ExecuteScalarAsync(
                connectionString,
                "SELECT note FROM trigger_tests.documents WHERE id = 2"));

            using var noTriggerContext = CreateContext<NoTriggerContext>(connectionString);
            var noTriggerModel = noTriggerContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(noTriggerContext, disabledModel, noTriggerModel, connectionString);
            Assert.Equal(false, await ExecuteScalarAsync(
                connectionString,
                "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'normalize_note_v2')"));
        }
        finally
        {
            await CleanupAsync(connectionString);
        }
    }

    private static void ConfigureEntity(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Document>();
        entity.ToTable("documents", "trigger_tests");
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.Note).HasColumnName("note");
    }

    private static void ConfigureFunction(ModelBuilder modelBuilder) => modelBuilder.HasFunction(
        "normalize_document_note",
        "trigger",
        "BEGIN NEW.note := upper(NEW.note) || ':' || TG_ARGV[0]; RETURN NEW; END",
        function => function.UseLanguage("plpgsql"),
        "trigger_tests");

    private static void ConfigureTrigger(
        ModelBuilder modelBuilder,
        string name,
        BlueTuskTriggerEnabledMode mode)
    {
        ConfigureEntity(modelBuilder);
        ConfigureFunction(modelBuilder);
        modelBuilder.Entity<Document>().HasTrigger(
            name,
            trigger => trigger
                .UseTiming(BlueTuskTriggerTiming.Before)
                .OnInsert()
                .OnUpdate(item => item.Note)
                .ForEachRow()
                .When("NEW.note IS NOT NULL")
                .ExecuteFunction(
                    "normalize_document_note",
                    "trigger_tests",
                    "suffix",
                    "quoted'value")
                .HasEnabledMode(mode));
    }

    private static TContext CreateContext<TContext>(string connectionString)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>().UseBlueTusk(connectionString).Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    private static async Task ApplyAsync(DbContext context, IModel? source, IModel target, string connectionString)
    {
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            source?.GetRelationalModel(),
            target.GetRelationalModel());
        foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(operations, target))
        {
            await ExecuteNonQueryAsync(connectionString, command.CommandText);
        }
    }

    private static async Task CleanupAsync(string connectionString) => await ExecuteNonQueryAsync(
        connectionString,
        "DROP SCHEMA IF EXISTS trigger_tests CASCADE");

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

    private sealed class TriggerContext(DbContextOptions<TriggerContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureTrigger(modelBuilder, "normalize_note", BlueTuskTriggerEnabledMode.Always);
    }

    private sealed class RenamedDisabledTriggerContext(DbContextOptions<RenamedDisabledTriggerContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureTrigger(modelBuilder, "normalize_note_v2", BlueTuskTriggerEnabledMode.Disabled);
    }

    private sealed class NoTriggerContext(DbContextOptions<NoTriggerContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntity(modelBuilder);
            ConfigureFunction(modelBuilder);
        }
    }

    private sealed class NoTriggerNoFunctionContext(DbContextOptions<NoTriggerNoFunctionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntity(modelBuilder);
    }

    private sealed class Document
    {
        public int Id { get; set; }

        public string? Note { get; set; }
    }
}

#pragma warning restore EF1001
