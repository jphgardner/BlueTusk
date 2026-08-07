using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.EventTriggers;
using BlueTusk.EntityFrameworkCore.EventTriggers.Internal;
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

#pragma warning disable EF1001

public sealed class EventTriggerMigrationsTests
{
    private const string Offline = "Host=localhost;Database=unused;Username=unused;Password=unused";
    private const string Schema = "event_trigger_tests";
    private const string TriggerName = "bluetusk_capture_ddl";

    [Fact]
    public void Event_trigger_SQL_diffs_ordering_guards_and_generated_CSharp_are_preserved()
    {
        using var initial = Create<EventTriggerContext>(Offline);
        using var disabled = Create<DisabledEventTriggerContext>(Offline);
        using var replaced = Create<ReplacedEventTriggerContext>(Offline);
        using var renamed = Create<RenamedEventTriggerContext>(Offline);
        using var removed = Create<NoEventTriggerContext>(Offline);
        var model = initial.GetService<IDesignTimeModel>().Model;
        var differ = initial.GetService<IMigrationsModelDiffer>();
        var creates = differ.GetDifferences(null, model.GetRelationalModel()).ToArray();
        var create = Assert.Single(creates.OfType<CreateEventTriggerOperation>());
        Assert.True(Array.FindIndex(creates, operation => operation is CreateTableOperation) <
                    Array.IndexOf(creates, create));
        Assert.True(Array.FindIndex(creates, operation => operation is CreateRoutineOperation) <
                    Array.IndexOf(creates, create));

        var generator = initial.GetService<IMigrationsSqlGenerator>();
        var createSql = string.Concat(generator.Generate(creates, model).Select(command => command.CommandText));
        Assert.Contains(
            "CREATE EVENT TRIGGER \"bluetusk_capture_ddl\" ON ddl_command_end " +
            "WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION \"event_trigger_tests\".\"capture_ddl\"()",
            createSql,
            StringComparison.Ordinal);

        var disabledModel = disabled.GetService<IDesignTimeModel>().Model;
        var modeChanges = differ.GetDifferences(
            model.GetRelationalModel(), disabledModel.GetRelationalModel()).ToArray();
        var mode = Assert.Single(modeChanges.OfType<AlterEventTriggerEnabledModeOperation>());
        Assert.Equal(BlueTuskEventTriggerEnabledMode.Disabled, mode.EnabledMode);
        Assert.Contains("ALTER EVENT TRIGGER \"bluetusk_capture_ddl\" DISABLE",
            generator.Generate(modeChanges, disabledModel).Single().CommandText, StringComparison.Ordinal);

        var replacementModel = replaced.GetService<IDesignTimeModel>().Model;
        var replacements = differ.GetDifferences(
            model.GetRelationalModel(), replacementModel.GetRelationalModel()).ToArray();
        Assert.Single(replacements.OfType<DropEventTriggerOperation>());
        Assert.Single(replacements.OfType<CreateEventTriggerOperation>());

        var renamedModel = renamed.GetService<IDesignTimeModel>().Model;
        var renames = differ.GetDifferences(
            disabledModel.GetRelationalModel(), renamedModel.GetRelationalModel()).ToArray();
        Assert.Single(renames.OfType<RenameEventTriggerOperation>());

        var removals = differ.GetDifferences(
            renamedModel.GetRelationalModel(),
            removed.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();
        var dropIndex = Array.FindIndex(removals, operation => operation is DropEventTriggerOperation);
        Assert.True(dropIndex < Array.FindIndex(removals, operation => operation is DropTableOperation));
        Assert.True(dropIndex < Array.FindIndex(removals, operation => operation is DropRoutineOperation));

        var login = create.Definition with
        {
            Name = "bluetusk_login_audit",
            Event = BlueTuskEventTriggerEvent.Login,
            Tags = [],
            EnabledMode = BlueTuskEventTriggerEnabledMode.Disabled,
        };
        var loginSql = string.Concat(generator.Generate(
                [new CreateEventTriggerOperation { Definition = login }])
            .Select(command => command.CommandText));
        Assert.Contains("server_version_num')::integer < 170000", loginSql, StringComparison.Ordinal);
        Assert.Contains(" ON login EXECUTE FUNCTION ", loginSql, StringComparison.Ordinal);
        Assert.Contains("ALTER EVENT TRIGGER \"bluetusk_login_audit\" DISABLE", loginSql,
            StringComparison.Ordinal);

        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateEventTrigger(create.Definition);
        migration.AlterEventTriggerEnabledMode(TriggerName, BlueTuskEventTriggerEnabledMode.Always);
        migration.RenameEventTrigger(TriggerName, $"{TriggerName}_v2");
        migration.DropEventTrigger($"{TriggerName}_v2");
        using var provider = DesignServices();
        var codeBuilder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, codeBuilder);
        var code = codeBuilder.ToString();
        Assert.Contains("CreateEventTrigger(", code, StringComparison.Ordinal);
        Assert.Contains("AlterEventTriggerEnabledMode(", code, StringComparison.Ordinal);
        Assert.Contains("RenameEventTrigger(", code, StringComparison.Ordinal);
        Assert.Contains("DropEventTrigger(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Login_event_rejects_command_tags()
    {
        var model = new ModelBuilder();
        Assert.Throws<ArgumentException>(() => model.HasEventTrigger(
            "login_audit",
            BlueTuskEventTriggerEvent.Login,
            "capture_ddl",
            trigger => trigger.HasTags("CREATE TABLE"),
            Schema));
    }

    [Fact]
    public async Task Event_triggers_execute_round_trip_disable_rename_scaffold_and_drop_across_PostgreSQL()
    {
        var cs = ConnectionString();
        await Cleanup(cs);
        try
        {
            using var initial = Create<EventTriggerContext>(cs);
            var initialModel = initial.GetService<IDesignTimeModel>().Model;
            await Apply(initial, null, initialModel, cs);
            await Execute(cs, "CREATE TABLE event_trigger_tests.probe_one (id integer)");
            Assert.Equal(1L, await Scalar(cs,
                "SELECT count(*) FROM event_trigger_tests.audit_entries WHERE tag = 'CREATE TABLE'"));
            await Execute(cs,
                "CREATE SCHEMA event_trigger_noise; " +
                "CREATE TABLE event_trigger_noise.unrelated_probe (id integer)");
            Assert.Equal(1L, await Scalar(cs,
                "SELECT count(*) FROM event_trigger_tests.audit_entries WHERE tag = 'CREATE TABLE'"));

            var database = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], [Schema]));
            var definitions = BlueTuskEventTriggerMetadata.Deserialize(Assert.IsType<string>(
                database[BlueTuskEventTriggerMetadata.AnnotationName]));
            var eventTrigger = Assert.Single(definitions.EventTriggers);
            Assert.Equal(TriggerName, eventTrigger.Name);
            Assert.Equal(BlueTuskEventTriggerEvent.DdlCommandEnd, eventTrigger.Event);
            Assert.Equal("capture_ddl", eventTrigger.Function.Name);
            Assert.Equal(Schema, eventTrigger.Function.Schema);
            Assert.Equal("CREATE TABLE", Assert.Single(eventTrigger.Tags));

            using (var provider = DesignServices())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    cs,
                    new DatabaseModelFactoryOptions([], [Schema]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "EventTriggerScaffoldContext",
                        ConnectionString = cs,
                        ContextNamespace = "EventTriggerModels",
                        ModelNamespace = "EventTriggerModels",
                        RootNamespace = "EventTriggerModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasEventTriggers(", scaffolded.ContextFile.Code,
                    StringComparison.Ordinal);
            }

            var version = Convert.ToInt32(await Scalar(cs, "SHOW server_version_num"),
                System.Globalization.CultureInfo.InvariantCulture);
            if (version >= 170000)
            {
                var login = eventTrigger with
                {
                    Name = "bluetusk_login_audit",
                    Event = BlueTuskEventTriggerEvent.Login,
                    Tags = [],
                    EnabledMode = BlueTuskEventTriggerEnabledMode.Disabled,
                };
                foreach (var command in initial.GetService<IMigrationsSqlGenerator>().Generate(
                             [new CreateEventTriggerOperation { Definition = login }]))
                {
                    await Execute(cs, command.CommandText);
                }

                Assert.Equal(true, await Scalar(cs,
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger " +
                    "WHERE evtname = 'bluetusk_login_audit' AND evtevent = 'login' AND evtenabled = 'D')"));
                await Execute(cs, "DROP EVENT TRIGGER bluetusk_login_audit");
            }

            using var disabled = Create<DisabledEventTriggerContext>(cs);
            var disabledModel = disabled.GetService<IDesignTimeModel>().Model;
            await Apply(disabled, initialModel, disabledModel, cs);
            await Execute(cs, "CREATE TABLE event_trigger_tests.probe_two (id integer)");
            Assert.Equal(1L, await Scalar(cs,
                "SELECT count(*) FROM event_trigger_tests.audit_entries WHERE tag = 'CREATE TABLE'"));

            using var renamed = Create<RenamedEventTriggerContext>(cs);
            var renamedModel = renamed.GetService<IDesignTimeModel>().Model;
            await Apply(renamed, disabledModel, renamedModel, cs);
            Assert.Equal(true, await Scalar(cs,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger " +
                "WHERE evtname = 'bluetusk_capture_ddl_v2' AND evtenabled = 'D')"));

            using var removed = Create<NoEventTriggerContext>(cs);
            await Apply(removed, renamedModel, removed.GetService<IDesignTimeModel>().Model, cs);
            Assert.Equal(false, await Scalar(cs,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger " +
                "WHERE evtname = 'bluetusk_capture_ddl_v2')"));
        }
        finally
        {
            await Cleanup(cs);
        }
    }

    private static void Configure(
        ModelBuilder modelBuilder,
        bool disabled,
        bool replaced,
        bool renamed)
    {
        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("audit_entries", Schema);
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(item => item.Tag).HasColumnName("tag").IsRequired();
        });
        modelBuilder.HasFunction(
            "capture_ddl",
            "event_trigger",
            """
            BEGIN
                IF TG_EVENT = 'ddl_command_end' AND EXISTS (
                    SELECT 1
                    FROM pg_event_trigger_ddl_commands()
                    WHERE schema_name = 'event_trigger_tests')
                THEN
                    INSERT INTO event_trigger_tests.audit_entries(tag) VALUES (TG_TAG);
                END IF;
            END
            """,
            function => function.UseLanguage("plpgsql"),
            Schema);
        modelBuilder.HasEventTrigger(
            renamed ? $"{TriggerName}_v2" : TriggerName,
            replaced ? BlueTuskEventTriggerEvent.DdlCommandStart : BlueTuskEventTriggerEvent.DdlCommandEnd,
            "capture_ddl",
            trigger => trigger.HasTags("CREATE TABLE").IsDisabled(disabled),
            Schema);
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

    private static async Task Cleanup(string cs) => await Execute(
        cs,
        "DROP EVENT TRIGGER IF EXISTS bluetusk_capture_ddl; " +
        "DROP EVENT TRIGGER IF EXISTS bluetusk_capture_ddl_v2; " +
        "DROP EVENT TRIGGER IF EXISTS bluetusk_login_audit; " +
        "DROP SCHEMA IF EXISTS event_trigger_noise CASCADE; " +
        "DROP SCHEMA IF EXISTS event_trigger_tests CASCADE");

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

    private sealed class EventTriggerContext(DbContextOptions<EventTriggerContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Configure(modelBuilder, disabled: false, replaced: false, renamed: false);
    }

    private sealed class DisabledEventTriggerContext(DbContextOptions<DisabledEventTriggerContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Configure(modelBuilder, disabled: true, replaced: false, renamed: false);
    }

    private sealed class ReplacedEventTriggerContext(DbContextOptions<ReplacedEventTriggerContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Configure(modelBuilder, disabled: false, replaced: true, renamed: false);
    }

    private sealed class RenamedEventTriggerContext(DbContextOptions<RenamedEventTriggerContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Configure(modelBuilder, disabled: true, replaced: false, renamed: true);
    }

    private sealed class NoEventTriggerContext(DbContextOptions<NoEventTriggerContext> options) : DbContext(options);

    private sealed class AuditEntry
    {
        public int Id { get; set; }

        public required string Tag { get; set; }
    }
}

#pragma warning restore EF1001
