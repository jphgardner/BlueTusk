using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity;
using BlueTusk.EntityFrameworkCore.RowLevelSecurity.Internal;
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

public sealed class RowLevelSecurityTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    private const string TenantPredicate =
        "tenant_id = current_setting('bluetusk.tenant_id')::integer";

    [Fact]
    public void Model_metadata_generates_policies_and_table_security_settings()
    {
        using var context = CreateContext<SecuredContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel());

        var createTable = Assert.Single(operations.OfType<CreateTableOperation>());
        var tableMetadata = BlueTuskRowLevelSecurityMetadata.Deserialize(
            Assert.IsType<string>(createTable[BlueTuskRowLevelSecurityMetadata.AnnotationName]));
        Assert.True(tableMetadata.Enabled);
        Assert.True(tableMetadata.Forced);
        Assert.Empty(tableMetadata.Policies);

        var policies = operations.OfType<CreateBlueTuskRowSecurityPolicyOperation>().ToArray();
        Assert.Equal(3, policies.Length);
        var select = Assert.Single(policies, operation => operation.Definition.Name == "tenant_select");
        Assert.Equal(BlueTuskRowSecurityPolicyCommand.Select, select.Definition.Command);
        Assert.Equal(BlueTuskRowSecurityPolicyBehavior.Permissive, select.Definition.Behavior);
        Assert.Equal("bluetusk_rls_user", Assert.Single(select.Definition.Roles).Name);
        var settings = Assert.Single(operations.OfType<AlterBlueTuskRowLevelSecurityOperation>());
        Assert.True(settings.Enabled);
        Assert.True(settings.Forced);

        var sql = string.Concat(
            context.GetService<IMigrationsSqlGenerator>()
                .Generate(operations, model)
                .Select(command => command.CommandText));
        Assert.Contains(
            "CREATE POLICY \"tenant_select\" ON \"rls_tests\".\"documents\" AS PERMISSIVE FOR SELECT TO \"bluetusk_rls_user\" USING (" + TenantPredicate + ");",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE POLICY \"tenant_insert\" ON \"rls_tests\".\"documents\" AS PERMISSIVE FOR INSERT TO \"bluetusk_rls_user\" WITH CHECK (" + TenantPredicate + ");",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE POLICY \"tenant_update\" ON \"rls_tests\".\"documents\" AS RESTRICTIVE FOR UPDATE TO PUBLIC USING (" + TenantPredicate + ") WITH CHECK (" + TenantPredicate + ");",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE \"rls_tests\".\"documents\" ENABLE ROW LEVEL SECURITY;",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE \"rls_tests\".\"documents\" FORCE ROW LEVEL SECURITY;",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_configuration_rejects_invalid_command_expressions_and_roles()
    {
        var modelBuilder = new ModelBuilder();
        ConfigureEntity(modelBuilder);
        var builder = modelBuilder.Entity<SecureDocument>().UseBlueTuskRowLevelSecurity();

        Assert.Throws<ArgumentException>(() => builder.HasPolicy(
            "bad_insert",
            BlueTuskRowSecurityPolicyCommand.Insert,
            usingSql: "true"));
        Assert.Throws<ArgumentException>(() => builder.HasPolicy(
            "bad_select",
            BlueTuskRowSecurityPolicyCommand.Select,
            withCheckSql: "true"));
        Assert.ThrowsAny<ArgumentException>(() => builder.HasPolicy(
            "bad_role",
            roles: [new BlueTuskRowSecurityRoleDefinition(BlueTuskRowSecurityRoleKind.Named)]));
        Assert.Throws<ArgumentException>(() => builder.HasPolicy(" "));
    }

    [Fact]
    public void Model_differ_renames_replaces_disables_and_removes_policies()
    {
        using var sourceContext = CreateContext<OldPolicyContext>(OfflineConnectionString);
        using var renamedContext = CreateContext<RenamedPolicyContext>(OfflineConnectionString);
        using var changedContext = CreateContext<ChangedPolicyContext>(OfflineConnectionString);
        using var changedBehaviorContext = CreateContext<ChangedBehaviorPolicyContext>(OfflineConnectionString);
        using var disabledContext = CreateContext<DisabledPolicyContext>(OfflineConnectionString);
        using var unsecuredContext = CreateContext<UnsecuredContext>(OfflineConnectionString);
        using var renamedTableContext = CreateContext<RenamedTablePolicyContext>(OfflineConnectionString);
        var differ = sourceContext.GetService<IMigrationsModelDiffer>();
        var source = sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var rename = Assert.Single(
            differ.GetDifferences(
                    source,
                    renamedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
                .OfType<RenameBlueTuskRowSecurityPolicyOperation>());
        Assert.Equal("tenant_policy", rename.Name);
        Assert.Equal("renamed_policy", rename.NewName);

        var replacement = differ.GetDifferences(
            source,
            changedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Single(replacement.OfType<AlterBlueTuskRowSecurityPolicyOperation>());
        Assert.Empty(replacement.OfType<DropBlueTuskRowSecurityPolicyOperation>());
        Assert.Empty(replacement.OfType<CreateBlueTuskRowSecurityPolicyOperation>());

        var behaviorReplacement = differ.GetDifferences(
            source,
            changedBehaviorContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Single(behaviorReplacement.OfType<DropBlueTuskRowSecurityPolicyOperation>());
        Assert.Single(behaviorReplacement.OfType<CreateBlueTuskRowSecurityPolicyOperation>());

        var disabled = Assert.Single(
            differ.GetDifferences(
                    source,
                    disabledContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
                .OfType<AlterBlueTuskRowLevelSecurityOperation>());
        Assert.False(disabled.Enabled);
        Assert.False(disabled.Forced);

        var removed = differ.GetDifferences(
            source,
            unsecuredContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Single(removed.OfType<DropBlueTuskRowSecurityPolicyOperation>());
        var removedSettings = Assert.Single(removed.OfType<AlterBlueTuskRowLevelSecurityOperation>());
        Assert.False(removedSettings.Enabled);
        Assert.False(removedSettings.Forced);

        var tableRename = differ.GetDifferences(
            source,
            renamedTableContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Equal("secured_documents", Assert.Single(tableRename.OfType<RenameTableOperation>()).NewName);
        Assert.Empty(tableRename.OfType<CreateBlueTuskRowSecurityPolicyOperation>());
        Assert.Empty(tableRename.OfType<DropBlueTuskRowSecurityPolicyOperation>());
    }

    [Fact]
    public void Manual_policy_operations_quote_roles_and_generate_setting_changes()
    {
        using var context = CreateContext<UnsecuredContext>(OfflineConnectionString);
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateBlueTuskRowSecurityPolicy(
            "documents",
            new BlueTuskRowSecurityPolicyDefinition(
                "Mixed Policy",
                BlueTuskRowSecurityPolicyBehavior.Restrictive,
                BlueTuskRowSecurityPolicyCommand.All,
                [
                    BlueTuskRowSecurityRoleDefinition.Named("Role Name"),
                    BlueTuskRowSecurityRoleDefinition.Public,
                    BlueTuskRowSecurityRoleDefinition.CurrentUser,
                ],
                "true",
                "true"),
            "rls_tests");
        migration.RenameBlueTuskRowSecurityPolicy(
            "documents",
            "Mixed Policy",
            "Renamed Policy",
            "rls_tests");
        migration.AlterBlueTuskRowSecurityPolicy(
            "documents",
            new BlueTuskRowSecurityPolicyDefinition(
                "Renamed Policy",
                BlueTuskRowSecurityPolicyBehavior.Restrictive,
                BlueTuskRowSecurityPolicyCommand.All,
                [BlueTuskRowSecurityRoleDefinition.Named("Changed Role")],
                "id > 0",
                "id > 0"),
            "rls_tests");
        migration.AlterBlueTuskRowLevelSecurity(
            "documents",
            enabled: false,
            forced: false,
            schema: "rls_tests");
        migration.DropBlueTuskRowSecurityPolicy(
            "documents",
            "Renamed Policy",
            "rls_tests");

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            migration.Operations,
            context.GetService<IDesignTimeModel>().Model);
        Assert.Equal(6, commands.Count);
        Assert.Equal(
            "CREATE POLICY \"Mixed Policy\" ON \"rls_tests\".\"documents\" AS RESTRICTIVE FOR ALL TO \"Role Name\", PUBLIC, CURRENT_USER USING (true) WITH CHECK (true);" + Environment.NewLine,
            commands[0].CommandText);
        Assert.Equal(
            "ALTER POLICY \"Mixed Policy\" ON \"rls_tests\".\"documents\" RENAME TO \"Renamed Policy\";" + Environment.NewLine,
            commands[1].CommandText);
        Assert.Equal(
            "ALTER POLICY \"Renamed Policy\" ON \"rls_tests\".\"documents\" TO \"Changed Role\" USING (id > 0) WITH CHECK (id > 0);" + Environment.NewLine,
            commands[2].CommandText);
        Assert.Equal(
            "ALTER TABLE \"rls_tests\".\"documents\" DISABLE ROW LEVEL SECURITY;" + Environment.NewLine,
            commands[3].CommandText);
        Assert.Equal(
            "ALTER TABLE \"rls_tests\".\"documents\" NO FORCE ROW LEVEL SECURITY;" + Environment.NewLine,
            commands[4].CommandText);
        Assert.Equal(
            "DROP POLICY \"Renamed Policy\" ON \"rls_tests\".\"documents\";" + Environment.NewLine,
            commands[5].CommandText);
    }

    [Fact]
    public void Design_time_generator_scaffolds_all_row_security_operations()
    {
        var policy = CreatePolicy("tenant_policy");
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
                new CreateBlueTuskRowSecurityPolicyOperation
                {
                    Table = "documents",
                    Schema = "rls_tests",
                    Definition = policy,
                },
                new RenameBlueTuskRowSecurityPolicyOperation
                {
                    Table = "documents",
                    Schema = "rls_tests",
                    Name = "tenant_policy",
                    NewName = "renamed_policy",
                },
                new AlterBlueTuskRowSecurityPolicyOperation
                {
                    Table = "documents",
                    Schema = "rls_tests",
                    Definition = policy with { Name = "renamed_policy" },
                },
                new AlterBlueTuskRowLevelSecurityOperation
                {
                    Table = "documents",
                    Schema = "rls_tests",
                    Enabled = true,
                    Forced = false,
                },
                new DropBlueTuskRowSecurityPolicyOperation
                {
                    Table = "documents",
                    Schema = "rls_tests",
                    Name = "renamed_policy",
                },
            ],
            builder);

        var code = builder.ToString();
        Assert.Contains("migrationBuilder.CreateBlueTuskRowSecurityPolicy(\"documents\"", code, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.RenameBlueTuskRowSecurityPolicy(\"documents\"", code, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.AlterBlueTuskRowSecurityPolicy(\"documents\"", code, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.AlterBlueTuskRowLevelSecurity(\"documents\", true, false", code, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.DropBlueTuskRowSecurityPolicy(\"documents\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policies_enforce_tenant_access_and_round_trip_through_scaffolding()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS rls_tests CASCADE");
        await ExecuteNonQueryAsync(connectionString, "DROP ROLE IF EXISTS bluetusk_rls_user");

        try
        {
            await ExecuteNonQueryAsync(connectionString, "CREATE ROLE bluetusk_rls_user");
            using var context = CreateContext<SecuredContext>(connectionString);
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
                INSERT INTO rls_tests.documents (id, tenant_id, content)
                VALUES (1, 1, 'visible'), (2, 2, 'hidden');
                GRANT USAGE ON SCHEMA rls_tests TO bluetusk_rls_user;
                GRANT SELECT, INSERT, UPDATE ON rls_tests.documents TO bluetusk_rls_user
                """);

            await using (var connection = new BlueTuskConnection(connectionString))
            {
                await connection.OpenAsync(CancellationToken.None);
                await ExecuteOnConnectionAsync(connection, "SET ROLE bluetusk_rls_user");
                await ExecuteOnConnectionAsync(connection, "SET bluetusk.tenant_id = '1'");
                Assert.Equal(
                    1L,
                    await ExecuteScalarOnConnectionAsync(
                        connection,
                        "SELECT count(*) FROM rls_tests.documents"));
                Assert.Equal(
                    true,
                    await ExecuteScalarOnConnectionAsync(
                        connection,
                        "SELECT row_security_active('rls_tests.documents')"));
                await ExecuteOnConnectionAsync(
                    connection,
                    "INSERT INTO rls_tests.documents VALUES (3, 1, 'allowed')");
                await Assert.ThrowsAsync<BlueTuskException>(() => ExecuteOnConnectionAsync(
                    connection,
                    "INSERT INTO rls_tests.documents VALUES (4, 2, 'rejected')"));
                await ExecuteOnConnectionAsync(connection, "RESET ROLE");
            }

            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["rls_tests"]));
            var table = Assert.Single(databaseModel.Tables);
            var discovered = BlueTuskRowLevelSecurityMetadata.Deserialize(
                Assert.IsType<string>(table[BlueTuskRowLevelSecurityMetadata.AnnotationName]));
            Assert.True(discovered.Enabled);
            Assert.True(discovered.Forced);
            Assert.Equal(3, discovered.Policies.Count);
            var selectPolicy = Assert.Single(
                discovered.Policies,
                policy => policy.Name == "tenant_select");
            Assert.Equal(BlueTuskRowSecurityPolicyCommand.Select, selectPolicy.Command);
            Assert.Contains("tenant_id", selectPolicy.UsingSql, StringComparison.Ordinal);
            Assert.Equal("bluetusk_rls_user", Assert.Single(selectPolicy.Roles).Name);

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using var serviceProvider = services.BuildServiceProvider();
            var scaffolded = serviceProvider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                connectionString,
                new DatabaseModelFactoryOptions([], ["rls_tests"]),
                new ModelReverseEngineerOptions(),
                new ModelCodeGenerationOptions
                {
                    ContextName = "RowSecurityContext",
                    ConnectionString = connectionString,
                    ContextNamespace = "RowSecurityModels",
                    ModelNamespace = "RowSecurityModels",
                    RootNamespace = "RowSecurityModels",
                    Language = "C#",
                    ProjectDir = AppContext.BaseDirectory,
                    UseNullableReferenceTypes = true,
                });
            Assert.Contains(
                "HasBlueTuskRowLevelSecurity(",
                scaffolded.ContextFile.Code,
                StringComparison.Ordinal);
            Assert.Single(scaffolded.AdditionalFiles);

            var lifecycle = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            lifecycle.AlterBlueTuskRowSecurityPolicy(
                "documents",
                new BlueTuskRowSecurityPolicyDefinition(
                    "tenant_select",
                    BlueTuskRowSecurityPolicyBehavior.Permissive,
                    BlueTuskRowSecurityPolicyCommand.Select,
                    [BlueTuskRowSecurityRoleDefinition.Named("bluetusk_rls_user")],
                    "tenant_id > 0"),
                "rls_tests");
            lifecycle.RenameBlueTuskRowSecurityPolicy(
                "documents",
                "tenant_select",
                "tenant_select_renamed",
                "rls_tests");
            lifecycle.DropBlueTuskRowSecurityPolicy(
                "documents",
                "tenant_select_renamed",
                "rls_tests");
            var lifecycleCommands = context.GetService<IMigrationsSqlGenerator>()
                .Generate(lifecycle.Operations, model);
            await ExecuteNonQueryAsync(connectionString, lifecycleCommands[0].CommandText);
            Assert.Equal(3L, await CountVisibleRowsAsync(connectionString));
            await ExecuteNonQueryAsync(connectionString, lifecycleCommands[1].CommandText);
            await ExecuteNonQueryAsync(connectionString, lifecycleCommands[2].CommandText);
            Assert.Equal(0L, await CountVisibleRowsAsync(connectionString));
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS rls_tests CASCADE");
            await ExecuteNonQueryAsync(connectionString, "DROP ROLE IF EXISTS bluetusk_rls_user");
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

    private static void ConfigureEntity(ModelBuilder modelBuilder, string table = "documents")
    {
        var entity = modelBuilder.Entity<SecureDocument>();
        entity.ToTable(table, "rls_tests");
        entity.HasKey(document => document.Id);
        entity.Property(document => document.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(document => document.TenantId).HasColumnName("tenant_id");
        entity.Property(document => document.Content).HasColumnName("content");
    }

    private static BlueTuskRowLevelSecurityBuilder ConfigurePolicy(
        ModelBuilder modelBuilder,
        string policyName,
        string predicate = TenantPredicate,
        bool enabled = true,
        bool forced = true,
        string table = "documents")
    {
        ConfigureEntity(modelBuilder, table);
        return modelBuilder.Entity<SecureDocument>()
            .UseBlueTuskRowLevelSecurity(enabled, forced)
            .HasPolicy(
                policyName,
                BlueTuskRowSecurityPolicyCommand.Select,
                usingSql: predicate,
                roles: [BlueTuskRowSecurityRoleDefinition.Named("bluetusk_rls_user")]);
    }

    private static BlueTuskRowSecurityPolicyDefinition CreatePolicy(string name) =>
        new(
            name,
            BlueTuskRowSecurityPolicyBehavior.Permissive,
            BlueTuskRowSecurityPolicyCommand.Select,
            [BlueTuskRowSecurityRoleDefinition.Named("bluetusk_rls_user")],
            TenantPredicate);

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteOnConnectionAsync(connection, sql);
    }

    private static async Task ExecuteOnConnectionAsync(BlueTuskConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<object?> ExecuteScalarOnConnectionAsync(
        BlueTuskConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async Task<long> CountVisibleRowsAsync(string connectionString)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await ExecuteOnConnectionAsync(connection, "SET ROLE bluetusk_rls_user");
        await ExecuteOnConnectionAsync(connection, "SET bluetusk.tenant_id = '1'");
        var count = Assert.IsType<long>(await ExecuteScalarOnConnectionAsync(
            connection,
            "SELECT count(*) FROM rls_tests.documents"));
        await ExecuteOnConnectionAsync(connection, "RESET ROLE");
        return count;
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

    private sealed class SecuredContext(DbContextOptions<SecuredContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntity(modelBuilder);
            modelBuilder.Entity<SecureDocument>()
                .UseBlueTuskRowLevelSecurity(enabled: true, forced: true)
                .HasPolicy(
                    "tenant_select",
                    BlueTuskRowSecurityPolicyCommand.Select,
                    usingSql: TenantPredicate,
                    roles: [BlueTuskRowSecurityRoleDefinition.Named("bluetusk_rls_user")])
                .HasPolicy(
                    "tenant_insert",
                    BlueTuskRowSecurityPolicyCommand.Insert,
                    withCheckSql: TenantPredicate,
                    roles: [BlueTuskRowSecurityRoleDefinition.Named("bluetusk_rls_user")])
                .HasPolicy(
                    "tenant_update",
                    BlueTuskRowSecurityPolicyCommand.Update,
                    BlueTuskRowSecurityPolicyBehavior.Restrictive,
                    TenantPredicate,
                    TenantPredicate);
        }
    }

    private sealed class OldPolicyContext(DbContextOptions<OldPolicyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigurePolicy(modelBuilder, "tenant_policy");
    }

    private sealed class RenamedPolicyContext(DbContextOptions<RenamedPolicyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigurePolicy(modelBuilder, "renamed_policy");
    }

    private sealed class ChangedPolicyContext(DbContextOptions<ChangedPolicyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigurePolicy(modelBuilder, "tenant_policy", "tenant_id > 0");
    }

    private sealed class ChangedBehaviorPolicyContext(DbContextOptions<ChangedBehaviorPolicyContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntity(modelBuilder);
            modelBuilder.Entity<SecureDocument>()
                .UseBlueTuskRowLevelSecurity(enabled: true, forced: true)
                .HasPolicy(
                    "tenant_policy",
                    BlueTuskRowSecurityPolicyCommand.Select,
                    BlueTuskRowSecurityPolicyBehavior.Restrictive,
                    usingSql: TenantPredicate,
                    roles: [BlueTuskRowSecurityRoleDefinition.Named("bluetusk_rls_user")]);
        }
    }

    private sealed class DisabledPolicyContext(DbContextOptions<DisabledPolicyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigurePolicy(modelBuilder, "tenant_policy", enabled: false, forced: false);
    }

    private sealed class UnsecuredContext(DbContextOptions<UnsecuredContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntity(modelBuilder);
    }

    private sealed class RenamedTablePolicyContext(DbContextOptions<RenamedTablePolicyContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigurePolicy(modelBuilder, "tenant_policy", table: "secured_documents");
    }

    private sealed class SecureDocument
    {
        public int Id { get; set; }

        public int TenantId { get; set; }

        public string? Content { get; set; }
    }
}

#pragma warning restore EF1001
