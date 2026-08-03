using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Rules;
using BlueTusk.EntityFrameworkCore.Rules.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

#pragma warning disable EF1001

public sealed class RuleMigrationsTests
{
    private const string Offline = "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Rule_SQL_and_diffs_preserve_replace_rename_mode_and_RESTRICT()
    {
        using var source = Create<RuleContext>(Offline);
        using var changed = Create<ChangedRuleContext>(Offline);
        using var renamed = Create<RenamedDisabledRuleContext>(Offline);
        using var empty = Create<NoRuleContext>(Offline);
        var model = source.GetService<IDesignTimeModel>().Model;
        var differ = source.GetService<IMigrationsModelDiffer>();
        var creates = differ.GetDifferences(null, model.GetRelationalModel()).ToArray();
        var create = Assert.Single(creates.OfType<CreateBlueTuskRuleOperation>());
        Assert.True(Array.FindIndex(creates, item => item is Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation) <
                    Array.FindIndex(creates, item => item is CreateBlueTuskRuleOperation));
        var sql = string.Concat(source.GetService<IMigrationsSqlGenerator>().Generate([create], model)
            .Select(command => command.CommandText));
        Assert.Contains(
            "CREATE RULE \"audit_insert\" AS ON INSERT TO \"rule_tests\".\"documents\" " +
            "WHERE (NEW.note IS NOT NULL) DO ALSO INSERT INTO rule_tests.audit(document_id, note) " +
            "VALUES (NEW.id, NEW.note)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("ENABLE ALWAYS RULE \"audit_insert\"", sql, StringComparison.Ordinal);

        Assert.True(Assert.Single(differ.GetDifferences(
                model.GetRelationalModel(),
                changed.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .OfType<CreateBlueTuskRuleOperation>()).OrReplace);
        var renameOperations = differ.GetDifferences(
            model.GetRelationalModel(),
            renamed.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Single(renameOperations.OfType<RenameBlueTuskRuleOperation>());
        Assert.Equal(BlueTuskRuleEnabledMode.Disabled, Assert.Single(
            renameOperations.OfType<AlterBlueTuskRuleEnabledModeOperation>()).EnabledMode);
        var drop = Assert.Single(differ.GetDifferences(
                model.GetRelationalModel(),
                empty.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .OfType<DropBlueTuskRuleOperation>());
        Assert.True(drop.IsDestructiveChange);
        Assert.Contains(" RESTRICT", Assert.Single(source.GetService<IMigrationsSqlGenerator>()
            .Generate([drop], model)).CommandText, StringComparison.Ordinal);

        using var canonical = Create<CanonicalRuleContext>(Offline);
        using var renamedCanonical = Create<RenamedCanonicalRuleContext>(Offline);
        var canonicalOperations = canonical.GetService<IMigrationsModelDiffer>().GetDifferences(
            canonical.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            renamedCanonical.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Single(canonicalOperations.OfType<RenameBlueTuskRuleOperation>());
        Assert.Empty(canonicalOperations.OfType<CreateBlueTuskRuleOperation>());
        Assert.Empty(canonicalOperations.OfType<DropBlueTuskRuleOperation>());
    }

    [Fact]
    public void Invalid_SELECT_rule_is_rejected()
    {
        var model = new ModelBuilder();
        ConfigureEntities(model);
        Assert.Throws<ArgumentException>(() => model.Entity<Document>().HasBlueTuskRule(
            "select_rule",
            BlueTuskRuleEvent.Select,
            "SELECT id, note FROM rule_tests.documents",
            instead: true));
    }

    [Fact]
    public void Manual_lifecycle_operations_generate_CSharp()
    {
        using var context = Create<RuleContext>(Offline);
        var model = context.GetService<IDesignTimeModel>().Model;
        var definition = Assert.Single(model.FindEntityType(typeof(Document))!.GetBlueTuskRules());
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateBlueTuskRule("documents", definition, "rule_tests", orReplace: true);
        migration.RenameBlueTuskRule("documents", "audit_insert", "audit_insert_v2", "rule_tests");
        migration.AlterBlueTuskRuleEnabledMode(
            "documents",
            "audit_insert_v2",
            BlueTuskRuleEnabledMode.Replica,
            "rule_tests");
        migration.DropBlueTuskRule("documents", "audit_insert_v2", "rule_tests");

        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddEntityFrameworkBlueTusk();
        new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = services.BuildServiceProvider();
        var builder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, builder);
        var code = builder.ToString();
        Assert.Contains("CreateBlueTuskRule", code, StringComparison.Ordinal);
        Assert.Contains("RenameBlueTuskRule", code, StringComparison.Ordinal);
        Assert.Contains("AlterBlueTuskRuleEnabledMode", code, StringComparison.Ordinal);
        Assert.Contains("DropBlueTuskRule", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rule_executes_round_trips_scaffolds_renames_disables_and_drops_across_PostgreSQL()
    {
        var cs = ConnectionString();
        await Execute(cs, "DROP SCHEMA IF EXISTS rule_tests CASCADE");
        try
        {
            using var initial = Create<RuleContext>(cs);
            var initialModel = initial.GetService<IDesignTimeModel>().Model;
            await Apply(initial, null, initialModel, cs);
            await Execute(cs, "INSERT INTO rule_tests.documents (id, note) VALUES (1, 'one')");
            Assert.Equal("one", await Scalar(cs, "SELECT note FROM rule_tests.audit WHERE document_id = 1"));

            var database = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], ["rule_tests"]));
            var table = Assert.Single(database.Tables, item => item.Name == "documents");
            var discovered = Assert.Single(BlueTuskRuleMetadata.Deserialize(
                Assert.IsType<string>(table[BlueTuskRuleMetadata.AnnotationName])));
            Assert.Equal(BlueTuskRuleEnabledMode.Always, discovered.EnabledMode);
            Assert.StartsWith("CREATE RULE audit_insert", discovered.CanonicalCreateSql, StringComparison.Ordinal);

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var provider = services.BuildServiceProvider())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    cs,
                    new DatabaseModelFactoryOptions([], ["rule_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "RuleContext",
                        ConnectionString = cs,
                        ContextNamespace = "RuleModels",
                        ModelNamespace = "RuleModels",
                        RootNamespace = "RuleModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasBlueTuskRules(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
            }

            using var disabled = Create<RenamedDisabledRuleContext>(cs);
            var disabledModel = disabled.GetService<IDesignTimeModel>().Model;
            await Apply(disabled, initialModel, disabledModel, cs);
            await Execute(cs, "INSERT INTO rule_tests.documents (id, note) VALUES (2, 'two')");
            Assert.Equal(0, Convert.ToInt32(await Scalar(
                cs,
                "SELECT count(*) FROM rule_tests.audit WHERE document_id = 2"),
                System.Globalization.CultureInfo.InvariantCulture));

            using var noRule = Create<NoRuleContext>(cs);
            var noRuleModel = noRule.GetService<IDesignTimeModel>().Model;
            await Apply(noRule, disabledModel, noRuleModel, cs);
            Assert.Equal(false, await Scalar(
                cs,
                "SELECT EXISTS (SELECT 1 FROM pg_rewrite WHERE rulename = 'audit_insert_v2')"));
        }
        finally
        {
            await Execute(cs, "DROP SCHEMA IF EXISTS rule_tests CASCADE");
        }
    }

    private static void ConfigureEntities(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<Document>();
        document.ToTable("documents", "rule_tests");
        document.HasKey(item => item.Id);
        document.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        document.Property(item => item.Note).HasColumnName("note");
        var audit = modelBuilder.Entity<Audit>();
        audit.ToTable("audit", "rule_tests");
        audit.HasKey(item => item.Id);
        audit.Property(item => item.Id).HasColumnName("id");
        audit.Property(item => item.DocumentId).HasColumnName("document_id");
        audit.Property(item => item.Note).HasColumnName("note");
    }

    private static void ConfigureRule(
        ModelBuilder modelBuilder,
        string name,
        BlueTuskRuleEnabledMode mode,
        string condition = "NEW.note IS NOT NULL")
    {
        ConfigureEntities(modelBuilder);
        modelBuilder.Entity<Document>().HasBlueTuskRule(
            name,
            BlueTuskRuleEvent.Insert,
            "INSERT INTO rule_tests.audit(document_id, note) VALUES (NEW.id, NEW.note)",
            conditionSql: condition,
            enabledMode: mode);
    }

    private static T Create<T>(string cs) where T : DbContext =>
        (T)Activator.CreateInstance(typeof(T), new DbContextOptionsBuilder<T>().UseBlueTusk(cs).Options)!;

    private static async Task Apply(DbContext context, IModel? source, IModel target, string cs)
    {
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            source?.GetRelationalModel(), target.GetRelationalModel());
        foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(operations, target))
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

    private sealed class RuleContext(DbContextOptions<RuleContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureRule(modelBuilder, "audit_insert", BlueTuskRuleEnabledMode.Always);
    }

    private sealed class ChangedRuleContext(DbContextOptions<ChangedRuleContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureRule(modelBuilder, "audit_insert", BlueTuskRuleEnabledMode.Always, "NEW.note <> ''");
    }

    private sealed class RenamedDisabledRuleContext(DbContextOptions<RenamedDisabledRuleContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureRule(modelBuilder, "audit_insert_v2", BlueTuskRuleEnabledMode.Disabled);
    }

    private sealed class NoRuleContext(DbContextOptions<NoRuleContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
    }

    private sealed class CanonicalRuleContext(DbContextOptions<CanonicalRuleContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureCanonicalRule(modelBuilder, "audit_insert");
    }

    private sealed class RenamedCanonicalRuleContext(DbContextOptions<RenamedCanonicalRuleContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureCanonicalRule(modelBuilder, "audit_insert_v2");
    }

    private static void ConfigureCanonicalRule(ModelBuilder modelBuilder, string name)
    {
        ConfigureEntities(modelBuilder);
        var definition = new BlueTuskRuleDefinition(
            name,
            BlueTuskRuleEvent.Insert,
            IsInstead: false,
            ConditionSql: null,
            ActionSql: null,
            BlueTuskRuleEnabledMode.Origin,
            $"CREATE RULE \"{name}\" AS ON INSERT TO rule_tests.documents DO ALSO NOTHING");
        modelBuilder.Entity<Document>().HasBlueTuskRules(BlueTuskRuleMetadata.Serialize([definition]));
    }

    private sealed class Document { public int Id { get; set; } public string? Note { get; set; } }
    private sealed class Audit { public int Id { get; set; } public int DocumentId { get; set; } public string? Note { get; set; } }
}

#pragma warning restore EF1001
