using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Collations;
using BlueTusk.EntityFrameworkCore.Collations.Internal;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
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

#pragma warning disable EF1001 // Tests intentionally exercise provider and design-time infrastructure.

public sealed class CollationMigrationsTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Collations_are_created_before_and_dropped_after_dependent_types()
    {
        using var context = CreateContext<CollationWithDomainContext>(OfflineConnectionString);
        using var emptyContext = CreateContext<EmptyContext>(OfflineConnectionString);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var model = context.GetService<IDesignTimeModel>().Model;
        var creates = differ.GetDifferences(null, model.GetRelationalModel()).ToArray();
        var drops = differ.GetDifferences(
            model.GetRelationalModel(),
            emptyContext.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();

        Assert.True(
            Array.FindIndex(creates, operation => operation is CreateCollationOperation) <
            Array.FindIndex(creates, operation => operation is CreateDomainTypeOperation));
        Assert.True(
            Array.FindIndex(drops, operation => operation is DropDomainTypeOperation) <
            Array.FindIndex(drops, operation => operation is DropCollationOperation));
        Assert.True(Assert.Single(drops.OfType<DropCollationOperation>()).IsDestructiveChange);
    }

    [Fact]
    public void ICU_rules_are_quoted_and_capability_guarded()
    {
        using var context = CreateContext<RulesCollationContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel());
        var sql = string.Concat(context.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, model)
            .Select(command => command.CommandText));

        Assert.Contains("current_setting('server_version_num')::integer < 160000", sql, StringComparison.Ordinal);
        Assert.Contains("PROVIDER = icu", sql, StringComparison.Ordinal);
        Assert.Contains("DETERMINISTIC = false", sql, StringComparison.Ordinal);
        Assert.Contains("RULES = ''&V << w <<< W''", sql, StringComparison.Ordinal);
        Assert.Contains("VERSION = ''version''''one''", sql, StringComparison.Ordinal);

        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateCollation(new BlueTuskCollationDefinition(
            "builtin_test",
            "collation_tests",
            BlueTuskCollationProvider.Builtin,
            "C.UTF-8",
            null,
            null,
            true,
            null,
            null));
        var builtinSql = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.Operations, model)).CommandText;
        Assert.Contains("current_setting('server_version_num')::integer < 170000", builtinSql, StringComparison.Ordinal);
        Assert.Contains("built-in collations require PostgreSQL 17 or later", builtinSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Rename_and_schema_move_are_safe_but_definition_changes_are_rejected()
    {
        using var initialContext = CreateContext<InitialCollationContext>(OfflineConnectionString);
        using var renamedContext = CreateContext<RenamedCollationContext>(OfflineConnectionString);
        using var alteredContext = CreateContext<AlteredCollationContext>(OfflineConnectionString);
        using var unspecifiedContext = CreateContext<UnspecifiedSchemaCollationContext>(OfflineConnectionString);
        var differ = initialContext.GetService<IMigrationsModelDiffer>();
        var source = initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var targetModel = renamedContext.GetService<IDesignTimeModel>().Model;
        var operations = differ.GetDifferences(source, targetModel.GetRelationalModel()).ToArray();

        var rename = Assert.Single(operations.OfType<RenameCollationOperation>());
        Assert.Equal("case_insensitive", rename.Name);
        Assert.Equal("case_insensitive_v2", rename.NewName);
        Assert.Equal("collation_tests_moved", rename.NewSchema);
        var sql = string.Concat(renamedContext.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, targetModel)
            .Select(command => command.CommandText));
        var move = sql.IndexOf("SET SCHEMA \"collation_tests_moved\"", StringComparison.Ordinal);
        var renameIndex = sql.IndexOf("RENAME TO \"case_insensitive_v2\"", StringComparison.Ordinal);
        Assert.True(move >= 0 && move < renameIndex);

        Assert.Contains(
            "cannot change its provider definition in place",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                source,
                alteredContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "cannot be moved to an unspecified schema",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                source,
                unspecifiedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_operations_and_generated_CSharp_preserve_safety_options()
    {
        using var context = CreateContext<InitialCollationContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateCollationFrom(
            "quoted\"copy",
            "C",
            "quoted\"schema",
            "pg_catalog",
            ifNotExists: true);
        migration.RefreshCollationVersion("case_insensitive", "collation_tests");
        migration.DropCollation(
            "case_insensitive",
            "collation_tests",
            ifExists: true,
            cascade: true);
        var sql = string.Concat(context.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.Operations, model)
            .Select(command => command.CommandText));

        Assert.Contains(
            "CREATE COLLATION IF NOT EXISTS \"quoted\"\"schema\".\"quoted\"\"copy\" FROM \"pg_catalog\".\"C\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER COLLATION \"collation_tests\".\"case_insensitive\" REFRESH VERSION",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "DROP COLLATION IF EXISTS \"collation_tests\".\"case_insensitive\" CASCADE",
            sql,
            StringComparison.Ordinal);

        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddEntityFrameworkBlueTusk();
        new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = services.BuildServiceProvider();
        var generator = provider.GetRequiredService<ICSharpMigrationOperationGenerator>();
        var builder = new IndentedStringBuilder();
        generator.Generate("migrationBuilder", migration.Operations, builder);
        var code = builder.ToString();
        Assert.Contains("CreateCollationFrom", code, StringComparison.Ordinal);
        Assert.Contains("RefreshCollationVersion", code, StringComparison.Ordinal);
        Assert.Contains("DropCollation", code, StringComparison.Ordinal);
        Assert.Contains(", true, true);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_provider_option_combinations_are_rejected()
    {
        var modelBuilder = new ModelBuilder();
        Assert.Throws<ArgumentException>(() => modelBuilder.HasCollation(
            "invalid_libc",
            collation => collation
                .UseProvider(BlueTuskCollationProvider.Libc)
                .UseLocale("C")
                .IsDeterministic(false)));
        Assert.Throws<ArgumentException>(() => modelBuilder.HasCollation(
            "invalid_icu",
            collation => collation
                .UseProvider(BlueTuskCollationProvider.Icu)
                .UseLibcLocales("C", "C")));
        Assert.Throws<ArgumentException>(() => modelBuilder.HasCollation(
            "missing_locale",
            collation => collation.UseProvider(BlueTuskCollationProvider.Icu)));
    }

    [Fact]
    public async Task Collations_execute_round_trip_scaffold_move_and_drop_across_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await CleanupAsync(connectionString);

        try
        {
            using var initialContext = CreateContext<InitialCollationContext>(connectionString);
            var initialModel = initialContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(initialContext, null, initialModel, connectionString);
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT 'BlueTusk' COLLATE collation_tests.case_insensitive = 'bluetusk'"));

            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["collation_tests"]));
            var definitions = BlueTuskCollationMetadata.Deserialize(
                Assert.IsType<string>(databaseModel[BlueTuskCollationMetadata.AnnotationName]));
            var discovered = Assert.Single(definitions.Collations);
            Assert.Equal("case_insensitive", discovered.Name);
            Assert.Equal("collation_tests", discovered.Schema);
            Assert.Equal(BlueTuskCollationProvider.Icu, discovered.Provider);
            Assert.Equal("und-u-ks-level2", discovered.Locale);
            Assert.False(discovered.IsDeterministic);
            Assert.False(string.IsNullOrWhiteSpace(discovered.Version));

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var provider = services.BuildServiceProvider())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], ["collation_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "CollationContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "CollationModels",
                        ModelNamespace = "CollationModels",
                        RootNamespace = "CollationModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasCollations(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
            }

            await VerifyVersionedFeaturesAsync(initialContext, connectionString);

            using var movedContext = CreateContext<RenamedCollationContext>(connectionString);
            var movedModel = movedContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(movedContext, initialModel, movedModel, connectionString);
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT to_regcollation('collation_tests_moved.case_insensitive_v2') IS NOT NULL"));
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT 'BlueTusk' COLLATE collation_tests_moved.case_insensitive_v2 = 'bluetusk'"));

            using var emptyContext = CreateContext<EmptyContext>(connectionString);
            var emptyModel = emptyContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(emptyContext, movedModel, emptyModel, connectionString);
            Assert.Equal(false, await ExecuteScalarAsync(
                connectionString,
                "SELECT to_regcollation('collation_tests_moved.case_insensitive_v2') IS NOT NULL"));
        }
        finally
        {
            await CleanupAsync(connectionString);
        }
    }

    private static async Task VerifyVersionedFeaturesAsync(DbContext context, string connectionString)
    {
        var serverVersion = Convert.ToInt32(await ExecuteScalarAsync(
            connectionString,
            "SELECT current_setting('server_version_num')"),
            System.Globalization.CultureInfo.InvariantCulture);

        await VerifyAsync(
            new BlueTuskCollationDefinition(
                "rules_test",
                "collation_tests",
                BlueTuskCollationProvider.Icu,
                "und",
                null,
                null,
                true,
                "&V << w <<< W",
                null),
            160000,
            "require PostgreSQL 16 or later");
        await VerifyAsync(
            new BlueTuskCollationDefinition(
                "builtin_test",
                "collation_tests",
                BlueTuskCollationProvider.Builtin,
                "C.UTF-8",
                null,
                null,
                true,
                null,
                null),
            170000,
            "require PostgreSQL 17 or later");
        return;

        async Task VerifyAsync(
            BlueTuskCollationDefinition definition,
            int minimumVersion,
            string errorMessage)
        {
            var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            migration.CreateCollation(definition);
            var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
                .Generate(migration.Operations, context.GetService<IDesignTimeModel>().Model)).CommandText;
            if (serverVersion >= minimumVersion)
            {
                await ExecuteNonQueryAsync(connectionString, sql);
                Assert.Equal(true, await ExecuteScalarAsync(
                    connectionString,
                    $"SELECT to_regcollation('collation_tests.{definition.Name}') IS NOT NULL"));
            }
            else
            {
                var exception = await Assert.ThrowsAsync<BlueTuskException>(() =>
                    ExecuteNonQueryAsync(connectionString, sql));
                Assert.Contains(errorMessage, exception.Message, StringComparison.Ordinal);
            }
        }
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

    private static TContext CreateContext<TContext>(string connectionString)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    private static async Task CleanupAsync(string connectionString) => await ExecuteNonQueryAsync(
        connectionString,
        """
        DROP SCHEMA IF EXISTS collation_tests CASCADE;
        DROP SCHEMA IF EXISTS collation_tests_moved CASCADE;
        """);

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

    private static void ConfigureCaseInsensitiveCollation(
        ModelBuilder modelBuilder,
        string name,
        string? schema) => modelBuilder.HasCollation(
        name,
        collation => collation
            .UseProvider(BlueTuskCollationProvider.Icu)
            .UseLocale("und-u-ks-level2")
            .IsDeterministic(false),
        schema);

    private sealed class InitialCollationContext(DbContextOptions<InitialCollationContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureCaseInsensitiveCollation(modelBuilder, "case_insensitive", "collation_tests");
    }

    private sealed class RenamedCollationContext(DbContextOptions<RenamedCollationContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureCaseInsensitiveCollation(modelBuilder, "case_insensitive_v2", "collation_tests_moved");
    }

    private sealed class AlteredCollationContext(DbContextOptions<AlteredCollationContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasCollation(
            "case_insensitive",
            collation => collation
                .UseProvider(BlueTuskCollationProvider.Icu)
                .UseLocale("und-u-ks-level1")
                .IsDeterministic(false),
            "collation_tests");
    }

    private sealed class UnspecifiedSchemaCollationContext(
        DbContextOptions<UnspecifiedSchemaCollationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureCaseInsensitiveCollation(modelBuilder, "case_insensitive", null);
    }

    private sealed class RulesCollationContext(DbContextOptions<RulesCollationContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasCollation(
            "quoted\"collation",
            collation => collation
                .UseProvider(BlueTuskCollationProvider.Icu)
                .UseLocale("und")
                .IsDeterministic(false)
                .HasRules("&V << w <<< W")
                .HasVersion("version'one"),
            "quoted\"schema");
    }

    private sealed class CollationWithDomainContext(DbContextOptions<CollationWithDomainContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureCaseInsensitiveCollation(modelBuilder, "case_insensitive", "collation_tests");
            modelBuilder.HasDomain(
                "case_insensitive_text",
                "text",
                domain => domain.UseCollation("collation_tests.case_insensitive"),
                "collation_tests");
        }
    }

    private sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options);
}
