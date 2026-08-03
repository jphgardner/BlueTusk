using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Subscriptions;
using BlueTusk.EntityFrameworkCore.Subscriptions.Internal;
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

public sealed class SubscriptionMigrationsTests
{
    private const string Offline = "Host=localhost;Database=unused;Username=unused;Password=unused";
    private const string PublisherConnection = "host=publisher dbname=app user=replicator";
    private const string ChangedPublisherConnection = "host=publisher-next dbname=app user=replicator";

    [Fact]
    public void Subscription_SQL_diffs_ordering_and_generated_CSharp_are_preserved()
    {
        using var initial = Create<SubscriptionContext>(Offline);
        using var changed = Create<ChangedSubscriptionContext>(Offline);
        using var renamed = Create<RenamedSubscriptionContext>(Offline);
        using var removed = Create<NoSubscriptionContext>(Offline);
        using var changedCreateBehavior = Create<ChangedCreateBehaviorSubscriptionContext>(Offline);
        var model = initial.GetService<IDesignTimeModel>().Model;
        var differ = initial.GetService<IMigrationsModelDiffer>();
        var creates = differ.GetDifferences(null, model.GetRelationalModel()).ToArray();
        var create = Assert.Single(creates.OfType<CreateBlueTuskSubscriptionOperation>());
        Assert.True(Array.FindIndex(creates, operation => operation is CreateBlueTuskPublicationOperation) <
                    Array.FindIndex(creates, operation => operation is CreateBlueTuskSubscriptionOperation));
        var createCommand = Assert.Single(initial.GetService<IMigrationsSqlGenerator>().Generate([create], model));
        Assert.False(createCommand.TransactionSuppressed);
        Assert.Contains(
            "CREATE SUBSCRIPTION \"application_subscription\" " +
            "CONNECTION 'host=publisher dbname=app user=replicator' " +
            "PUBLICATION \"remote_publication\"",
            createCommand.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("connect = false", createCommand.CommandText, StringComparison.Ordinal);
        Assert.Contains("slot_name = NONE", createCommand.CommandText, StringComparison.Ordinal);
        Assert.Contains("streaming = off", createCommand.CommandText, StringComparison.Ordinal);

        var alters = differ.GetDifferences(
            model.GetRelationalModel(),
            changed.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        var alter = Assert.Single(alters.OfType<AlterBlueTuskSubscriptionOperation>());
        var alterSql = string.Concat(initial.GetService<IMigrationsSqlGenerator>().Generate([alter], model)
            .Select(command => command.CommandText));
        Assert.Contains(
            "ALTER SUBSCRIPTION \"application_subscription\" " +
            "CONNECTION 'host=publisher-next dbname=app user=replicator'",
            alterSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "SET PUBLICATION \"audit_publication\", \"remote_publication\" WITH (refresh = false)",
            alterSql,
            StringComparison.Ordinal);
        Assert.Contains("binary = true", alterSql, StringComparison.Ordinal);
        Assert.Contains("streaming = on", alterSql, StringComparison.Ordinal);
        Assert.Contains("synchronous_commit = 'local'", alterSql, StringComparison.Ordinal);
        Assert.Contains("disable_on_error = true", alterSql, StringComparison.Ordinal);

        var changedCreateBehaviorModel = changedCreateBehavior.GetService<IDesignTimeModel>().Model;
        Assert.False(differ.HasDifferences(
            model.GetRelationalModel(), changedCreateBehaviorModel.GetRelationalModel()));
        Assert.Empty(differ.GetDifferences(
            model.GetRelationalModel(), changedCreateBehaviorModel.GetRelationalModel()));

        var renameOperations = differ.GetDifferences(
            changed.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            renamed.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Single(renameOperations.OfType<RenameBlueTuskSubscriptionOperation>());
        Assert.Empty(renameOperations.OfType<CreateBlueTuskSubscriptionOperation>());
        Assert.Empty(renameOperations.OfType<DropBlueTuskSubscriptionOperation>());

        var removals = differ.GetDifferences(
            model.GetRelationalModel(),
            removed.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();
        Assert.True(Array.FindIndex(removals, operation => operation is DropBlueTuskSubscriptionOperation) <
                    Array.FindIndex(removals, operation => operation is DropBlueTuskPublicationOperation));
        var drop = Assert.Single(removals.OfType<DropBlueTuskSubscriptionOperation>());
        Assert.False(drop.HasSlot);
        Assert.True(drop.IsDestructiveChange);
        var dropCommand = Assert.Single(initial.GetService<IMigrationsSqlGenerator>().Generate([drop], model));
        Assert.False(dropCommand.TransactionSuppressed);
        Assert.Contains("DROP SUBSCRIPTION \"application_subscription\" RESTRICT", dropCommand.CommandText,
            StringComparison.Ordinal);

        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateBlueTuskSubscription(create.Definition);
        migration.AlterBlueTuskSubscription(create.Definition, alter.Definition);
        migration.RenameBlueTuskSubscription("application_subscription", "application_subscription_v2");
        migration.RefreshBlueTuskSubscription("application_subscription_v2", copyData: false);
        migration.RefreshBlueTuskSubscriptionSequences("application_subscription_v2");
        migration.SkipBlueTuskSubscriptionTransaction("application_subscription_v2", "0/16B6C50");
        migration.DropBlueTuskSubscription("application_subscription_v2", hasSlot: false);
        using var provider = DesignServices();
        var builder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, builder);
        var code = builder.ToString();
        Assert.Contains("CreateBlueTuskSubscription", code, StringComparison.Ordinal);
        Assert.Contains("AlterBlueTuskSubscription", code, StringComparison.Ordinal);
        Assert.Contains("RenameBlueTuskSubscription", code, StringComparison.Ordinal);
        Assert.Contains("RefreshBlueTuskSubscription", code, StringComparison.Ordinal);
        Assert.Contains("RefreshBlueTuskSubscriptionSequences", code, StringComparison.Ordinal);
        Assert.Contains("SkipBlueTuskSubscriptionTransaction", code, StringComparison.Ordinal);
        Assert.Contains("DropBlueTuskSubscription", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Subscription_version_guards_and_nontransactional_commands_are_explicit()
    {
        using var context = Create<SubscriptionContext>(Offline);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var parallel = Definition() with { Streaming = BlueTuskSubscriptionStreamingMode.Parallel };
        var parallelCommands = generator.Generate(
            [new CreateBlueTuskSubscriptionOperation { Definition = parallel }]);
        Assert.Contains("server_version_num')::integer < 160000", parallelCommands[0].CommandText,
            StringComparison.Ordinal);

        var failover = Definition() with { Failover = true, SlotName = "application_slot" };
        var failoverCommands = generator.Generate(
            [new CreateBlueTuskSubscriptionOperation { Definition = failover }]);
        Assert.Contains("server_version_num')::integer < 170000", failoverCommands[0].CommandText,
            StringComparison.Ordinal);

        var foreignServer = Definition() with
        {
            Connection = BlueTuskSubscriptionConnection.FromForeignServer("publisher_server"),
            RetainDeadTuples = true,
            MaxRetentionDuration = 60,
            WalReceiverTimeout = "30s",
        };
        var version19Commands = generator.Generate(
            [new CreateBlueTuskSubscriptionOperation { Definition = foreignServer }]);
        Assert.Contains("server_version_num')::integer < 190000", version19Commands[0].CommandText,
            StringComparison.Ordinal);
        Assert.Contains("SERVER \"publisher_server\"", version19Commands[1].CommandText,
            StringComparison.Ordinal);

        var connected = Definition() with
        {
            SlotName = "application_slot",
            Enabled = true,
            ConnectOnCreate = true,
            CreateSlot = true,
            CopyData = true,
        };
        Assert.True(Assert.Single(generator.Generate(
            [new CreateBlueTuskSubscriptionOperation { Definition = connected }])).TransactionSuppressed);
        Assert.True(Assert.Single(generator.Generate(
            [new DropBlueTuskSubscriptionOperation { Name = "application_subscription", HasSlot = true }]))
            .TransactionSuppressed);
        Assert.True(Assert.Single(generator.Generate(
            [new RefreshBlueTuskSubscriptionOperation { Name = "application_subscription", CopyData = false }]))
            .TransactionSuppressed);
        var refreshSequences = generator.Generate(
            [new RefreshBlueTuskSubscriptionSequencesOperation { Name = "application_subscription" }]);
        Assert.Contains("server_version_num')::integer < 190000", refreshSequences[0].CommandText,
            StringComparison.Ordinal);
        Assert.True(refreshSequences[1].TransactionSuppressed);
        Assert.Contains("REFRESH SEQUENCES", refreshSequences[1].CommandText, StringComparison.Ordinal);
        Assert.Contains(
            "SKIP (lsn = NONE)",
            Assert.Single(generator.Generate(
                [new SkipBlueTuskSubscriptionTransactionOperation { Name = "application_subscription" }]))
                .CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Sensitive_or_redacted_model_connections_cannot_leak_into_generated_migrations()
    {
        var passwordModel = new ModelBuilder();
        Assert.Throws<ArgumentException>(() => passwordModel.HasBlueTuskSubscription(
            Definition() with
            {
                Connection = BlueTuskSubscriptionConnection.FromConnectionString(
                    "host=publisher user=replicator password=do-not-store"),
            }));

        var uriModel = new ModelBuilder();
        Assert.Throws<ArgumentException>(() => uriModel.HasBlueTuskSubscription(
            Definition() with
            {
                Connection = BlueTuskSubscriptionConnection.FromConnectionString(
                    "postgresql://replicator:do-not-store@publisher/app"),
            }));
        var queryModel = new ModelBuilder();
        Assert.Throws<ArgumentException>(() => queryModel.HasBlueTuskSubscription(
            Definition() with
            {
                Connection = BlueTuskSubscriptionConnection.FromConnectionString(
                    "postgresql://replicator@publisher/app?password=do-not-store"),
            }));

        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        Assert.Throws<ArgumentException>(() => migration.CreateBlueTuskSubscription(
            Definition() with { Failover = true }));

        using var context = Create<SubscriptionContext>(Offline);
        Assert.Throws<InvalidOperationException>(() => context.GetService<IMigrationsSqlGenerator>().Generate(
            [new CreateBlueTuskSubscriptionOperation
            {
                Definition = Definition() with { Connection = BlueTuskSubscriptionConnection.Redacted },
            }]));

        migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateBlueTuskSubscription(Definition() with
        {
            Connection = BlueTuskSubscriptionConnection.FromConnectionString(
                "host=publisher user=replicator password=runtime-secret"),
        });
        using var provider = DesignServices();
        Assert.Throws<ArgumentException>(() => provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, new IndentedStringBuilder()));
    }

    [Fact]
    public async Task Subscriptions_round_trip_alter_rename_scaffold_and_drop_across_PostgreSQL()
    {
        var cs = ConnectionString();
        await Cleanup(cs);
        try
        {
            using var initial = Create<SubscriptionContext>(cs);
            var initialModel = initial.GetService<IDesignTimeModel>().Model;
            await Apply(initial, null, initialModel, cs);

            var database = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], []));
            var definitions = BlueTuskSubscriptionMetadata.Deserialize(Assert.IsType<string>(
                database[BlueTuskSubscriptionMetadata.AnnotationName]));
            var discovered = Assert.Single(
                definitions.Subscriptions,
                subscription => subscription.Name == "application_subscription");
            Assert.Equal(BlueTuskSubscriptionConnectionKind.Redacted, discovered.Connection.Kind);
            Assert.Null(discovered.Connection.Value);
            Assert.Equal(["remote_publication"], discovered.Publications);
            Assert.False(discovered.Enabled);
            Assert.Null(discovered.SlotName);
            Assert.Equal(BlueTuskSubscriptionStreamingMode.Off, discovered.Streaming);
            await VerifyRestrictedSubscriptionDiscovery(cs);

            var version = Convert.ToInt32(await Scalar(cs, "SHOW server_version_num"),
                System.Globalization.CultureInfo.InvariantCulture);
            if (version >= 160000)
            {
                var version16Definition = Definition() with
                {
                    Streaming = BlueTuskSubscriptionStreamingMode.Parallel,
                    PasswordRequired = false,
                    RunAsOwner = true,
                    Origin = BlueTuskSubscriptionOrigin.None,
                };
                await ExecuteOperations(initial,
                    [new AlterBlueTuskSubscriptionOperation
                    {
                        OldDefinition = Definition(),
                        Definition = version16Definition,
                    }],
                    cs);
                var version16Database = new BlueTuskDatabaseModelFactory().Create(
                    cs,
                    new DatabaseModelFactoryOptions([], []));
                var version16Definitions = BlueTuskSubscriptionMetadata.Deserialize(Assert.IsType<string>(
                    version16Database[BlueTuskSubscriptionMetadata.AnnotationName]));
                var version16RoundTrip = Assert.Single(version16Definitions.Subscriptions,
                    subscription => subscription.Name == "application_subscription");
                Assert.Equal(BlueTuskSubscriptionStreamingMode.Parallel, version16RoundTrip.Streaming);
                Assert.False(version16RoundTrip.PasswordRequired);
                Assert.True(version16RoundTrip.RunAsOwner);
                Assert.Equal(BlueTuskSubscriptionOrigin.None, version16RoundTrip.Origin);
                await ExecuteOperations(initial,
                    [new AlterBlueTuskSubscriptionOperation
                    {
                        OldDefinition = version16Definition,
                        Definition = Definition(),
                    }],
                    cs);
            }

            if (version >= 170000)
            {
                var failover = Definition() with
                {
                    Name = "failover_subscription",
                    SlotName = "failover_slot",
                    Failover = true,
                };
                await ExecuteOperations(initial,
                    [new CreateBlueTuskSubscriptionOperation { Definition = failover }],
                    cs);
                var failoverDatabase = new BlueTuskDatabaseModelFactory().Create(
                    cs,
                    new DatabaseModelFactoryOptions([], []));
                var failoverDefinitions = BlueTuskSubscriptionMetadata.Deserialize(Assert.IsType<string>(
                    failoverDatabase[BlueTuskSubscriptionMetadata.AnnotationName]));
                Assert.True(Assert.Single(failoverDefinitions.Subscriptions,
                    subscription => subscription.Name == failover.Name).Failover);
                await Execute(cs, "ALTER SUBSCRIPTION failover_subscription SET (slot_name = NONE)");
                await ExecuteOperations(initial,
                    [new DropBlueTuskSubscriptionOperation { Name = failover.Name, HasSlot = false }],
                    cs);
            }

            if (version >= 190000)
            {
                var retained = Definition() with
                {
                    Name = "retained_subscription",
                    RetainDeadTuples = true,
                    MaxRetentionDuration = 60,
                    WalReceiverTimeout = "30s",
                };
                await ExecuteOperations(initial,
                    [new CreateBlueTuskSubscriptionOperation { Definition = retained }],
                    cs);
                var retainedDatabase = new BlueTuskDatabaseModelFactory().Create(
                    cs,
                    new DatabaseModelFactoryOptions([], []));
                var retainedDefinitions = BlueTuskSubscriptionMetadata.Deserialize(Assert.IsType<string>(
                    retainedDatabase[BlueTuskSubscriptionMetadata.AnnotationName]));
                var retainedRoundTrip = Assert.Single(retainedDefinitions.Subscriptions,
                    subscription => subscription.Name == retained.Name);
                Assert.True(retainedRoundTrip.RetainDeadTuples);
                Assert.Equal(60, retainedRoundTrip.MaxRetentionDuration);
                Assert.Equal("30s", retainedRoundTrip.WalReceiverTimeout);
                await ExecuteOperations(initial,
                    [new DropBlueTuskSubscriptionOperation { Name = retained.Name, HasSlot = false }],
                    cs);

                await VerifyForeignServerSubscription(initial, cs);
            }

            using (var provider = DesignServices())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    cs,
                    new DatabaseModelFactoryOptions([], []),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "SubscriptionContext",
                        ConnectionString = cs,
                        ContextNamespace = "SubscriptionModels",
                        ModelNamespace = "SubscriptionModels",
                        RootNamespace = "SubscriptionModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasBlueTuskSubscriptions(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
                Assert.DoesNotContain("host=publisher", scaffolded.ContextFile.Code, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("user=replicator", scaffolded.ContextFile.Code, StringComparison.OrdinalIgnoreCase);
            }

            using var changed = Create<ChangedSubscriptionContext>(cs);
            var changedModel = changed.GetService<IDesignTimeModel>().Model;
            await Apply(changed, initialModel, changedModel, cs);
            Assert.Equal(ChangedPublisherConnection, await Scalar(
                cs,
                "SELECT subconninfo FROM pg_catalog.pg_subscription " +
                "WHERE subname = 'application_subscription'"));
            Assert.Equal("{audit_publication,remote_publication}", await Scalar(
                cs,
                "SELECT subpublications::text FROM pg_catalog.pg_subscription " +
                "WHERE subname = 'application_subscription'"));
            Assert.Equal(true, await Scalar(
                cs,
                "SELECT subbinary FROM pg_catalog.pg_subscription " +
                "WHERE subname = 'application_subscription'"));

            using var renamed = Create<RenamedSubscriptionContext>(cs);
            var renamedModel = renamed.GetService<IDesignTimeModel>().Model;
            await Apply(renamed, changedModel, renamedModel, cs);
            Assert.Equal(true, await Scalar(
                cs,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_subscription " +
                "WHERE subname = 'application_subscription_v2')"));

            using var noSubscription = Create<NoSubscriptionContext>(cs);
            await Apply(noSubscription, renamedModel,
                noSubscription.GetService<IDesignTimeModel>().Model, cs);
            Assert.Equal(false, await Scalar(
                cs,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_subscription " +
                "WHERE subname = 'application_subscription_v2')"));
        }
        finally
        {
            await Cleanup(cs);
        }
    }

    private static BlueTuskSubscriptionDefinition Definition() => new(
        "application_subscription",
        BlueTuskSubscriptionConnection.FromConnectionString(PublisherConnection),
        ["remote_publication"],
        SlotName: null,
        Enabled: false,
        Binary: false,
        BlueTuskSubscriptionStreamingMode.Off,
        BlueTuskSubscriptionSynchronousCommit.Off,
        TwoPhase: false,
        DisableOnError: false,
        PasswordRequired: true,
        RunAsOwner: false,
        BlueTuskSubscriptionOrigin.Any,
        Failover: false,
        RetainDeadTuples: false,
        MaxRetentionDuration: 0,
        WalReceiverTimeout: null,
        ConnectOnCreate: false,
        CreateSlot: false,
        CopyData: false);

    private static void ConfigurePublication(ModelBuilder modelBuilder) =>
        modelBuilder.HasBlueTuskPublication(
            "remote_publication",
            publication => publication.ForAllTables());

    private static void ConfigureSubscription(
        ModelBuilder modelBuilder,
        string name,
        bool changed)
    {
        ConfigurePublication(modelBuilder);
        modelBuilder.HasBlueTuskSubscription(name, subscription =>
        {
            subscription
                .UseConnectionString(changed ? ChangedPublisherConnection : PublisherConnection)
                .FromPublication("remote_publication")
                .WithoutSlot();
            if (changed)
            {
                subscription
                    .FromPublication("audit_publication")
                    .UsesBinary()
                    .UsesStreaming(BlueTuskSubscriptionStreamingMode.On)
                    .UsesSynchronousCommit(BlueTuskSubscriptionSynchronousCommit.Local)
                    .DisableOnError();
            }
        });
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

    private static async Task VerifyForeignServerSubscription(DbContext context, string cs)
    {
        var extensionWasInstalled = Convert.ToBoolean(await Scalar(
            cs,
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_extension WHERE extname = 'postgres_fdw')"),
            System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await Execute(cs, "CREATE EXTENSION IF NOT EXISTS postgres_fdw");
            await Execute(cs,
                "CREATE SERVER subscription_publisher_server FOREIGN DATA WRAPPER postgres_fdw " +
                "OPTIONS (host 'publisher', dbname 'app')");
            await Execute(cs,
                "CREATE USER MAPPING FOR CURRENT_USER SERVER subscription_publisher_server " +
                "OPTIONS (user 'replicator')");
            var foreignServer = Definition() with
            {
                Name = "foreign_server_subscription",
                Connection = BlueTuskSubscriptionConnection.FromForeignServer("subscription_publisher_server"),
            };
            await ExecuteOperations(context,
                [new CreateBlueTuskSubscriptionOperation { Definition = foreignServer }],
                cs);
            var database = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], []));
            var definitions = BlueTuskSubscriptionMetadata.Deserialize(Assert.IsType<string>(
                database[BlueTuskSubscriptionMetadata.AnnotationName]));
            Assert.Equal(
                BlueTuskSubscriptionConnection.FromForeignServer("subscription_publisher_server"),
                Assert.Single(definitions.Subscriptions,
                    subscription => subscription.Name == foreignServer.Name).Connection);
        }
        finally
        {
            await Execute(cs, "DROP SUBSCRIPTION IF EXISTS foreign_server_subscription");
            await Execute(cs, "DROP SERVER IF EXISTS subscription_publisher_server CASCADE");
            if (!extensionWasInstalled)
            {
                await Execute(cs, "DROP EXTENSION IF EXISTS postgres_fdw CASCADE");
            }
        }
    }

    private static async Task VerifyRestrictedSubscriptionDiscovery(string cs)
    {
        const string role = "bluetusk_subscription_reader";
        await Execute(cs, $"DROP ROLE IF EXISTS {role}");
        try
        {
            await Execute(cs, $"CREATE ROLE {role} LOGIN PASSWORD 'catalog-reader-password'");
            var restrictedConnection = new BlueTuskConnectionStringBuilder(cs)
            {
                Username = role,
                Password = "catalog-reader-password",
            }.ConnectionString;
            var database = new BlueTuskDatabaseModelFactory().Create(
                restrictedConnection,
                new DatabaseModelFactoryOptions([], []));
            var definitions = BlueTuskSubscriptionMetadata.Deserialize(Assert.IsType<string>(
                database[BlueTuskSubscriptionMetadata.AnnotationName]));
            Assert.Equal(
                BlueTuskSubscriptionConnection.Redacted,
                Assert.Single(definitions.Subscriptions,
                    subscription => subscription.Name == "application_subscription").Connection);
        }
        finally
        {
            await Execute(cs, $"DROP ROLE IF EXISTS {role}");
        }
    }

    private static async Task<object?> Scalar(string cs, string sql)
    {
        await using var connection = new BlueTuskConnection(cs);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async Task Cleanup(string cs)
    {
        if (Convert.ToBoolean(await Scalar(
                cs,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_subscription " +
                "WHERE subname = 'failover_subscription')"),
                System.Globalization.CultureInfo.InvariantCulture))
        {
            await Execute(cs, "ALTER SUBSCRIPTION failover_subscription SET (slot_name = NONE)");
        }

        await Execute(
            cs,
            "DROP SUBSCRIPTION IF EXISTS application_subscription; " +
            "DROP SUBSCRIPTION IF EXISTS application_subscription_v2; " +
            "DROP SUBSCRIPTION IF EXISTS failover_subscription; " +
            "DROP SUBSCRIPTION IF EXISTS retained_subscription; " +
            "DROP SUBSCRIPTION IF EXISTS foreign_server_subscription; " +
            "DROP SERVER IF EXISTS subscription_publisher_server CASCADE; " +
            "DROP PUBLICATION IF EXISTS remote_publication");
        await Execute(cs, "DROP ROLE IF EXISTS bluetusk_subscription_reader");
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

    private sealed class SubscriptionContext(DbContextOptions<SubscriptionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureSubscription(modelBuilder, "application_subscription", changed: false);
    }

    private sealed class ChangedSubscriptionContext(DbContextOptions<ChangedSubscriptionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureSubscription(modelBuilder, "application_subscription", changed: true);
    }

    private sealed class ChangedCreateBehaviorSubscriptionContext(
        DbContextOptions<ChangedCreateBehaviorSubscriptionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigurePublication(modelBuilder);
            modelBuilder.HasBlueTuskSubscription("application_subscription", subscription => subscription
                .UseConnectionString(PublisherConnection)
                .FromPublication("remote_publication")
                .WithoutSlot()
                .ConnectOnCreate(createSlot: false, copyData: false, enabled: false));
        }
    }

    private sealed class RenamedSubscriptionContext(DbContextOptions<RenamedSubscriptionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureSubscription(modelBuilder, "application_subscription_v2", changed: true);
    }

    private sealed class NoSubscriptionContext(DbContextOptions<NoSubscriptionContext> options) : DbContext(options);
}

#pragma warning restore EF1001
