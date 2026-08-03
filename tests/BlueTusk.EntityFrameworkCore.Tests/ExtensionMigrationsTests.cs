using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Extensions;
using BlueTusk.EntityFrameworkCore.Extensions.Internal;
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

public sealed class ExtensionMigrationsTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Extensions_are_created_before_and_dropped_after_dependent_schema_objects()
    {
        using var context = CreateContext<ExtensionWithDomainContext>(OfflineConnectionString);
        using var emptyContext = CreateContext<EmptyContext>(OfflineConnectionString);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var model = context.GetService<IDesignTimeModel>().Model;
        var creates = differ.GetDifferences(null, model.GetRelationalModel()).ToArray();
        var drops = differ.GetDifferences(
            model.GetRelationalModel(),
            emptyContext.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();

        var definition = Assert.Single(model.GetBlueTuskExtensions().Extensions);
        Assert.Equal("hstore", definition.Name);
        Assert.Equal("extension_tests", definition.Schema);
        Assert.True(definition.InstallDependencies);
        Assert.True(
            Array.FindIndex(creates, operation => operation is CreateBlueTuskExtensionOperation) <
            Array.FindIndex(creates, operation => operation is CreateBlueTuskDomainTypeOperation));
        Assert.True(
            Array.FindIndex(drops, operation => operation is DropBlueTuskDomainTypeOperation) <
            Array.FindIndex(drops, operation => operation is DropBlueTuskExtensionOperation));

        var sql = string.Concat(context.GetService<IMigrationsSqlGenerator>()
            .Generate(creates, model)
            .Select(command => command.CommandText));
        Assert.Contains(
            "CREATE EXTENSION \"hstore\" WITH SCHEMA \"extension_tests\" CASCADE",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("CREATE DOMAIN", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Extension_dependencies_are_topological_and_cycles_are_rejected()
    {
        using var context = CreateContext<DependentExtensionsContext>(OfflineConnectionString);
        using var emptyContext = CreateContext<EmptyContext>(OfflineConnectionString);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var creates = differ.GetDifferences(null, model).OfType<CreateBlueTuskExtensionOperation>().ToArray();
        var drops = differ.GetDifferences(
                model,
                emptyContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .OfType<DropBlueTuskExtensionOperation>()
            .ToArray();

        Assert.Equal(["alpha", "zeta"], creates.Select(operation => operation.Definition.Name));
        Assert.Equal(["zeta", "alpha"], drops.Select(operation => operation.Name));

        var modelBuilder = new ModelBuilder();
        modelBuilder.HasBlueTuskExtension(
            "alpha",
            extension => extension.DependsOnExtension("zeta"));
        Assert.Contains(
            "contains a cycle",
            Assert.Throws<ArgumentException>(() => modelBuilder.HasBlueTuskExtension(
                "zeta",
                extension => extension.DependsOnExtension("alpha"))).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Version_and_schema_changes_emit_update_then_relocation()
    {
        using var initialContext = CreateContext<VersionOneExtensionContext>(OfflineConnectionString);
        using var alteredContext = CreateContext<VersionTwoExtensionContext>(OfflineConnectionString);
        var model = alteredContext.GetService<IDesignTimeModel>().Model;
        var operations = initialContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            model.GetRelationalModel()).ToArray();

        var alter = Assert.Single(operations.OfType<AlterBlueTuskExtensionOperation>());
        Assert.Equal("2.0-beta.1", alter.Definition.Version);
        Assert.Equal("extension_tests_v2", alter.Definition.Schema);
        var sql = string.Concat(alteredContext.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, model)
            .Select(command => command.CommandText));
        var update = sql.IndexOf("UPDATE TO '2.0-beta.1'", StringComparison.Ordinal);
        var move = sql.IndexOf("SET SCHEMA \"extension_tests_v2\"", StringComparison.Ordinal);
        Assert.True(update >= 0 && update < move);
    }

    [Fact]
    public void Rename_is_create_drop_and_unspecified_schema_moves_are_rejected()
    {
        using var initialContext = CreateContext<VersionOneExtensionContext>(OfflineConnectionString);
        using var renamedContext = CreateContext<RenamedExtensionContext>(OfflineConnectionString);
        using var unspecifiedContext = CreateContext<UnspecifiedSchemaExtensionContext>(OfflineConnectionString);
        var differ = initialContext.GetService<IMigrationsModelDiffer>();
        var source = initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var renamed = differ.GetDifferences(
            source,
            renamedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();

        Assert.Single(renamed.OfType<CreateBlueTuskExtensionOperation>());
        Assert.True(Assert.Single(renamed.OfType<DropBlueTuskExtensionOperation>()).IsDestructiveChange);
        Assert.Contains(
            "cannot be moved to an unspecified schema",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                source,
                unspecifiedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_and_generated_migration_operations_preserve_safety_options()
    {
        using var context = CreateContext<InitialExtensionContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var definition = Assert.Single(model.GetBlueTuskExtensions().Extensions);
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateBlueTuskExtension(definition, ifNotExists: true);
        migration.CreateBlueTuskExtension(new BlueTuskExtensionDefinition(
            "quoted\"extension",
            "quoted\"schema",
            "version'one",
            []));
        migration.DropBlueTuskExtension("hstore", ifExists: true, cascade: true);
        var sql = string.Concat(context.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.Operations, model)
            .Select(command => command.CommandText));
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE EXTENSION \"quoted\"\"extension\" WITH SCHEMA \"quoted\"\"schema\" VERSION 'version''one'",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("DROP EXTENSION IF EXISTS \"hstore\" CASCADE", sql, StringComparison.Ordinal);

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
                new CreateBlueTuskExtensionOperation { Definition = definition, IfNotExists = true },
                new AlterBlueTuskExtensionOperation
                {
                    OldDefinition = definition,
                    Definition = definition with { Version = "2.0" },
                },
                new DropBlueTuskExtensionOperation
                {
                    Name = definition.Name,
                    IfExists = true,
                    Cascade = true,
                },
            ],
            builder);
        var code = builder.ToString();
        Assert.Contains("CreateBlueTuskExtension", code, StringComparison.Ordinal);
        Assert.Contains("AlterBlueTuskExtension", code, StringComparison.Ordinal);
        Assert.Contains("DropBlueTuskExtension", code, StringComparison.Ordinal);
        Assert.Contains(", true, true);", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extension_installs_round_trips_scaffolds_moves_and_drops_across_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(
            connectionString,
            """
            DROP EXTENSION IF EXISTS hstore CASCADE;
            DROP SCHEMA IF EXISTS extension_tests CASCADE;
            DROP SCHEMA IF EXISTS extension_tests_moved CASCADE;
            """);

        try
        {
            using var initialContext = CreateContext<InitialExtensionContext>(connectionString);
            var initialModel = initialContext.GetService<IDesignTimeModel>().Model;
            var initialOperations = initialContext.GetService<IMigrationsModelDiffer>()
                .GetDifferences(null, initialModel.GetRelationalModel());
            foreach (var command in initialContext.GetService<IMigrationsSqlGenerator>()
                         .Generate(initialOperations, initialModel))
            {
                await ExecuteNonQueryAsync(connectionString, command.CommandText);
            }

            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT to_regtype('extension_tests.hstore') IS NOT NULL"));
            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["extension_tests"]));
            var discovered = BlueTuskExtensionMetadata.Deserialize(
                Assert.IsType<string>(databaseModel[BlueTuskExtensionMetadata.AnnotationName]));
            var extension = Assert.Single(discovered.Extensions);
            Assert.Equal("hstore", extension.Name);
            Assert.Equal("extension_tests", extension.Schema);
            Assert.False(string.IsNullOrWhiteSpace(extension.Version));

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var provider = services.BuildServiceProvider())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], ["extension_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "ExtensionContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "ExtensionModels",
                        ModelNamespace = "ExtensionModels",
                        RootNamespace = "ExtensionModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasBlueTuskExtensions(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
            }

            using var movedContext = CreateContext<MovedExtensionContext>(connectionString);
            var movedModel = movedContext.GetService<IDesignTimeModel>().Model;
            var moveOperations = movedContext.GetService<IMigrationsModelDiffer>().GetDifferences(
                initialModel.GetRelationalModel(),
                movedModel.GetRelationalModel());
            foreach (var command in movedContext.GetService<IMigrationsSqlGenerator>()
                         .Generate(moveOperations, movedModel))
            {
                await ExecuteNonQueryAsync(connectionString, command.CommandText);
            }

            Assert.Equal("extension_tests_moved", await ExecuteScalarAsync(
                connectionString,
                "SELECT namespace.nspname FROM pg_extension AS extension_entry JOIN pg_namespace AS namespace ON namespace.oid = extension_entry.extnamespace WHERE extension_entry.extname = 'hstore'"));

            using var emptyContext = CreateContext<EmptyContext>(connectionString);
            var dropOperations = emptyContext.GetService<IMigrationsModelDiffer>().GetDifferences(
                movedModel.GetRelationalModel(),
                emptyContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
            foreach (var command in emptyContext.GetService<IMigrationsSqlGenerator>()
                         .Generate(dropOperations, emptyContext.GetService<IDesignTimeModel>().Model))
            {
                await ExecuteNonQueryAsync(connectionString, command.CommandText);
            }

            Assert.Equal(false, await ExecuteScalarAsync(
                connectionString,
                "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'hstore')"));
        }
        finally
        {
            await ExecuteNonQueryAsync(
                connectionString,
                """
                DROP EXTENSION IF EXISTS hstore CASCADE;
                DROP SCHEMA IF EXISTS extension_tests CASCADE;
                DROP SCHEMA IF EXISTS extension_tests_moved CASCADE;
                """);
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

    private sealed class InitialExtensionContext(DbContextOptions<InitialExtensionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.HasBlueTuskExtension(
                "hstore",
                extension => extension.UseSchema("extension_tests").InstallDependencies());
    }

    private sealed class MovedExtensionContext(DbContextOptions<MovedExtensionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.HasBlueTuskExtension(
                "hstore",
                extension => extension.UseSchema("extension_tests_moved").InstallDependencies());
    }

    private sealed class ExtensionWithDomainContext(DbContextOptions<ExtensionWithDomainContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasBlueTuskExtension(
                "hstore",
                extension => extension.UseSchema("extension_tests").InstallDependencies());
            modelBuilder.HasBlueTuskDomain(
                "application_hstore",
                "extension_tests.hstore",
                schema: "extension_tests");
        }
    }

    private sealed class DependentExtensionsContext(DbContextOptions<DependentExtensionsContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasBlueTuskExtension(
                "zeta",
                extension => extension.DependsOnExtension("alpha"));
            modelBuilder.HasBlueTuskExtension("alpha");
        }
    }

    private sealed class VersionOneExtensionContext(DbContextOptions<VersionOneExtensionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.HasBlueTuskExtension(
                "versioned_extension",
                extension => extension.UseSchema("extension_tests").HasVersion("1.0"));
    }

    private sealed class VersionTwoExtensionContext(DbContextOptions<VersionTwoExtensionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.HasBlueTuskExtension(
                "versioned_extension",
                extension => extension.UseSchema("extension_tests_v2").HasVersion("2.0-beta.1"));
    }

    private sealed class RenamedExtensionContext(DbContextOptions<RenamedExtensionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.HasBlueTuskExtension(
                "renamed_extension",
                extension => extension.UseSchema("extension_tests").HasVersion("1.0"));
    }

    private sealed class UnspecifiedSchemaExtensionContext(
        DbContextOptions<UnspecifiedSchemaExtensionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.HasBlueTuskExtension(
                "versioned_extension",
                extension => extension.HasVersion("1.0"));
    }

    private sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options);
}

#pragma warning restore EF1001
