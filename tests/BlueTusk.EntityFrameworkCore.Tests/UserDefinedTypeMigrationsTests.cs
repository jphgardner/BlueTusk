using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes;
using BlueTusk.EntityFrameworkCore.UserDefinedTypes.Internal;
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

public sealed class UserDefinedTypeMigrationsTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Model_metadata_creates_enum_domain_and_composite_in_dependency_order()
    {
        using var context = CreateContext<InitialTypesContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .ToArray();

        Assert.IsType<EnsureSchemaOperation>(operations[0]);
        Assert.IsType<CreateEnumTypeOperation>(operations[1]);
        Assert.IsType<CreateDomainTypeOperation>(operations[2]);
        Assert.IsType<CreateCompositeTypeOperation>(operations[3]);
        var definitions = model.GetUserDefinedTypes();
        Assert.Single(definitions.Enums);
        Assert.Single(definitions.Domains);
        Assert.Single(definitions.Composites);

        var sql = string.Concat(
            context.GetService<IMigrationsSqlGenerator>()
                .Generate(operations, model)
                .Select(command => command.CommandText));
        Assert.Contains(
            "CREATE TYPE \"udt_tests\".\"mood\" AS ENUM ('sad', 'ok');",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE DOMAIN \"udt_tests\".\"positive_integer\" AS integer DEFAULT 1 NOT NULL;",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ADD CONSTRAINT \"value_positive\" CHECK (VALUE > 0) NOT VALID;",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TYPE \"udt_tests\".\"address\" AS (\"street\" text, \"zip\" udt_tests.positive_integer);",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Model_differ_emits_supported_alterations_and_rename_operations()
    {
        using var initialContext = CreateContext<InitialTypesContext>(OfflineConnectionString);
        using var alteredContext = CreateContext<AlteredTypesContext>(OfflineConnectionString);
        using var renamedContext = CreateContext<RenamedEnumContext>(OfflineConnectionString);
        var differ = initialContext.GetService<IMigrationsModelDiffer>();
        var source = initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var alteredModel = alteredContext.GetService<IDesignTimeModel>().Model;
        var altered = differ.GetDifferences(source, alteredModel.GetRelationalModel()).ToArray();

        Assert.Single(altered.OfType<AlterEnumTypeOperation>());
        Assert.Single(altered.OfType<AlterDomainTypeOperation>());
        Assert.Single(altered.OfType<AlterCompositeTypeOperation>());
        var countryDomain = Assert.Single(altered.OfType<CreateDomainTypeOperation>());
        Assert.Equal("country_code", countryDomain.Definition.Name);
        Assert.True(
            Array.IndexOf(altered, countryDomain) <
            Array.FindIndex(altered, operation => operation is AlterCompositeTypeOperation));
        var commands = alteredContext.GetService<IMigrationsSqlGenerator>().Generate(altered, alteredModel);
        Assert.Contains(commands, command =>
            command.TransactionSuppressed &&
            command.CommandText.Contains("ADD VALUE 'meh' BEFORE 'ok'", StringComparison.Ordinal));
        var sql = string.Concat(commands.Select(command => command.CommandText));
        Assert.Contains("ALTER DOMAIN \"udt_tests\".\"positive_integer\" SET DEFAULT 2", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER DOMAIN \"udt_tests\".\"positive_integer\" DROP NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("VALIDATE CONSTRAINT \"value_positive\"", sql, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT \"value_small\" CHECK (VALUE < 1000) NOT VALID", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE DOMAIN \"udt_tests\".\"country_code\" AS varchar(2)", sql, StringComparison.Ordinal);
        Assert.Contains("RENAME ATTRIBUTE \"street\" TO \"line1\"", sql, StringComparison.Ordinal);
        Assert.Contains("ADD ATTRIBUTE \"country\" udt_tests.country_code", sql, StringComparison.Ordinal);

        var renamed = differ.GetDifferences(
            source,
            renamedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        var rename = Assert.Single(renamed.OfType<RenameUserDefinedTypeOperation>());
        Assert.Equal(BlueTuskUserDefinedTypeKind.Enum, rename.Kind);
        Assert.Equal("mood", rename.Name);
        Assert.Equal("emotional_state", rename.NewName);
        Assert.Empty(renamed.OfType<DropEnumTypeOperation>());
        Assert.Empty(renamed.OfType<CreateEnumTypeOperation>());
    }

    [Fact]
    public void Model_differ_rejects_destructive_or_ambiguous_in_place_changes()
    {
        using var initialContext = CreateContext<InitialTypesContext>(OfflineConnectionString);
        using var enumContext = CreateContext<RemovedEnumLabelContext>(OfflineConnectionString);
        using var domainContext = CreateContext<ChangedDomainBaseContext>(OfflineConnectionString);
        using var compositeContext = CreateContext<InsertedCompositeAttributeContext>(OfflineConnectionString);
        var differ = initialContext.GetService<IMigrationsModelDiffer>();
        var source = initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        Assert.Contains(
            "cannot remove or reorder enum labels",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                source,
                enumContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "cannot change the base store type or collation",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                source,
                domainContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "cannot reorder existing attributes or insert attributes",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                source,
                compositeContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_types_drops_dependants_first_and_marks_operations_destructive()
    {
        using var initialContext = CreateContext<InitialTypesContext>(OfflineConnectionString);
        using var emptyContext = CreateContext<EmptyTypesContext>(OfflineConnectionString);
        var operations = initialContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            emptyContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        var drops = operations.Where(operation => operation is
                DropEnumTypeOperation or
                DropDomainTypeOperation or
                DropCompositeTypeOperation)
            .ToArray();

        Assert.Collection(
            drops,
            operation => Assert.IsType<DropCompositeTypeOperation>(operation),
            operation => Assert.IsType<DropDomainTypeOperation>(operation),
            operation => Assert.IsType<DropEnumTypeOperation>(operation));
        Assert.All(drops, operation => Assert.True(operation.IsDestructiveChange));
    }

    [Fact]
    public void Design_time_generator_scaffolds_all_type_operation_families()
    {
        var initial = CreateDefinitions(altered: false);
        var altered = CreateDefinitions(altered: true);
        var initialDomain = initial.Domains.Single(domain => domain.Name == "positive_integer");
        var alteredDomain = altered.Domains.Single(domain => domain.Name == "positive_integer");
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
                new CreateEnumTypeOperation { Definition = initial.Enums[0] },
                new AlterEnumTypeOperation
                {
                    OldDefinition = initial.Enums[0],
                    Definition = altered.Enums[0],
                },
                new DropEnumTypeOperation { Name = "mood", Schema = "udt_tests" },
                new CreateDomainTypeOperation { Definition = initialDomain },
                new AlterDomainTypeOperation
                {
                    OldDefinition = initialDomain,
                    Definition = alteredDomain,
                },
                new DropDomainTypeOperation { Name = "positive_integer", Schema = "udt_tests" },
                new CreateCompositeTypeOperation { Definition = initial.Composites[0] },
                new AlterCompositeTypeOperation
                {
                    OldDefinition = initial.Composites[0],
                    Definition = altered.Composites[0],
                },
                new DropCompositeTypeOperation { Name = "address", Schema = "udt_tests" },
                new RenameUserDefinedTypeOperation
                {
                    Kind = BlueTuskUserDefinedTypeKind.Enum,
                    Name = "mood",
                    Schema = "udt_tests",
                    NewName = "emotional_state",
                    NewSchema = "udt_tests",
                },
            ],
            builder);

        var code = builder.ToString();
        Assert.Contains("CreateEnumType", code, StringComparison.Ordinal);
        Assert.Contains("AlterEnumType", code, StringComparison.Ordinal);
        Assert.Contains("DropEnumType", code, StringComparison.Ordinal);
        Assert.Contains("CreateDomainType", code, StringComparison.Ordinal);
        Assert.Contains("AlterDomainType", code, StringComparison.Ordinal);
        Assert.Contains("DropDomainType", code, StringComparison.Ordinal);
        Assert.Contains("CreateCompositeType", code, StringComparison.Ordinal);
        Assert.Contains("AlterCompositeType", code, StringComparison.Ordinal);
        Assert.Contains("DropCompositeType", code, StringComparison.Ordinal);
        Assert.Contains("BlueTuskUserDefinedTypeKind.Enum", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Types_enforce_round_trip_scaffold_and_alter_across_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS udt_tests CASCADE");

        try
        {
            using var initialContext = CreateContext<InitialTypesContext>(connectionString);
            var initialModel = initialContext.GetService<IDesignTimeModel>().Model;
            var initialOperations = initialContext.GetService<IMigrationsModelDiffer>()
                .GetDifferences(null, initialModel.GetRelationalModel());
            foreach (var command in initialContext.GetService<IMigrationsSqlGenerator>()
                         .Generate(initialOperations, initialModel))
            {
                await ExecuteNonQueryAsync(connectionString, command.CommandText);
            }

            await ExecuteNonQueryAsync(
                connectionString,
                """
                CREATE TABLE udt_tests.type_values (
                    id integer PRIMARY KEY,
                    mood udt_tests.mood NOT NULL,
                    score udt_tests.positive_integer);
                INSERT INTO udt_tests.type_values
                    (id, mood, score)
                VALUES
                    (1, 'ok', 2)
                """);
            await Assert.ThrowsAsync<BlueTuskException>(() => ExecuteNonQueryAsync(
                connectionString,
                "INSERT INTO udt_tests.type_values (id, mood, score) VALUES (2, 'ok', -1)"));

            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["udt_tests"]));
            var discovered = BlueTuskUserDefinedTypeMetadata.Deserialize(
                Assert.IsType<string>(databaseModel[BlueTuskUserDefinedTypeMetadata.AnnotationName]));
            Assert.Equal(["sad", "ok"], Assert.Single(discovered.Enums).Labels);
            var domain = Assert.Single(discovered.Domains);
            Assert.Equal("integer", domain.BaseStoreType);
            Assert.Equal("1", domain.DefaultSql);
            Assert.True(domain.IsNotNull);
            Assert.Contains("VALUE > 0", Assert.Single(domain.Constraints).CheckSql, StringComparison.Ordinal);
            Assert.False(domain.Constraints[0].IsValidated);
            var composite = Assert.Single(discovered.Composites);
            Assert.Equal(["street", "zip"], composite.Attributes.Select(attribute => attribute.Name));

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var serviceProvider = services.BuildServiceProvider())
            {
                var scaffolded = serviceProvider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], ["udt_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "TypesContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "TypeModels",
                        ModelNamespace = "TypeModels",
                        RootNamespace = "TypeModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains(
                    "HasUserDefinedTypes(",
                    scaffolded.ContextFile.Code,
                    StringComparison.Ordinal);
                Assert.Single(scaffolded.AdditionalFiles);
            }

            using var alteredContext = CreateContext<AlteredTypesContext>(connectionString);
            var alteredModel = alteredContext.GetService<IDesignTimeModel>().Model;
            var alteredOperations = alteredContext.GetService<IMigrationsModelDiffer>().GetDifferences(
                initialModel.GetRelationalModel(),
                alteredModel.GetRelationalModel());
            foreach (var command in alteredContext.GetService<IMigrationsSqlGenerator>()
                         .Generate(alteredOperations, alteredModel))
            {
                await ExecuteNonQueryAsync(connectionString, command.CommandText);
            }

            await ExecuteNonQueryAsync(
                connectionString,
                "INSERT INTO udt_tests.type_values (id, mood, score) VALUES (3, 'meh', 3)");
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_type AS type_entry
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = type_entry.typnamespace
                    JOIN pg_catalog.pg_attribute AS attribute_entry
                      ON attribute_entry.attrelid = type_entry.typrelid
                    WHERE namespace.nspname = 'udt_tests'
                      AND type_entry.typname = 'address'
                      AND attribute_entry.attname = 'line1'
                      AND NOT attribute_entry.attisdropped)
                """));
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_type AS type_entry
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = type_entry.typnamespace
                    JOIN pg_catalog.pg_attribute AS attribute_entry
                      ON attribute_entry.attrelid = type_entry.typrelid
                    WHERE namespace.nspname = 'udt_tests'
                      AND type_entry.typname = 'address'
                      AND attribute_entry.attname = 'country'
                      AND NOT attribute_entry.attisdropped)
                """));
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                """
                SELECT constraint_entry.convalidated
                FROM pg_catalog.pg_constraint AS constraint_entry
                JOIN pg_catalog.pg_type AS type_entry
                  ON type_entry.oid = constraint_entry.contypid
                JOIN pg_catalog.pg_namespace AS namespace
                  ON namespace.oid = type_entry.typnamespace
                WHERE namespace.nspname = 'udt_tests'
                  AND type_entry.typname = 'positive_integer'
                  AND constraint_entry.conname = 'value_positive'
                """));

            var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            migration.RenameUserDefinedType(
                BlueTuskUserDefinedTypeKind.Enum,
                "mood",
                "emotional_state",
                "udt_tests",
                "udt_tests");
            var renameCommand = Assert.Single(alteredContext.GetService<IMigrationsSqlGenerator>()
                .Generate(migration.Operations, alteredModel));
            await ExecuteNonQueryAsync(connectionString, renameCommand.CommandText);
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT to_regtype('udt_tests.emotional_state') IS NOT NULL"));
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS udt_tests CASCADE");
        }
    }

    private static BlueTuskUserDefinedTypeDefinitionSet CreateDefinitions(bool altered)
    {
        var modelBuilder = new ModelBuilder();
        ConfigureTypes(modelBuilder, altered);
        return modelBuilder.Model.GetUserDefinedTypes();
    }

    private static void ConfigureTypes(ModelBuilder modelBuilder, bool altered)
    {
        modelBuilder.HasEnum(
            "mood",
            altered ? ["sad", "meh", "ok"] : ["sad", "ok"],
            "udt_tests");
        modelBuilder.HasDomain(
            "positive_integer",
            "integer",
            domain =>
            {
                domain.HasDefaultSql(altered ? "2" : "1")
                    .IsRequired(!altered)
                    .HasCheckConstraint("value_positive", "VALUE > 0", isValidated: altered);
                if (altered)
                {
                    domain.HasCheckConstraint("value_small", "VALUE < 1000", isValidated: false);
                }
            },
            "udt_tests");
        if (altered)
        {
            modelBuilder.HasDomain(
                "country_code",
                "varchar(2)",
                schema: "udt_tests");
        }

        modelBuilder.HasComposite(
            "address",
            composite =>
            {
                composite.HasAttribute(altered ? "line1" : "street", "text")
                    .HasAttribute("zip", "udt_tests.positive_integer");
                if (altered)
                {
                    composite.HasAttribute("country", "udt_tests.country_code");
                }
            },
            "udt_tests");
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

    private sealed class InitialTypesContext(DbContextOptions<InitialTypesContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureTypes(modelBuilder, altered: false);
    }

    private sealed class AlteredTypesContext(DbContextOptions<AlteredTypesContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureTypes(modelBuilder, altered: true);
    }

    private sealed class RenamedEnumContext(DbContextOptions<RenamedEnumContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureTypes(modelBuilder, altered: false);
            modelBuilder.HasNoUserDefinedType("mood", "udt_tests")
                .HasEnum("emotional_state", ["sad", "ok"], "udt_tests");
        }
    }

    private sealed class RemovedEnumLabelContext(DbContextOptions<RemovedEnumLabelContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureTypes(modelBuilder, altered: false);
            modelBuilder.HasEnum("mood", ["sad"], "udt_tests");
        }
    }

    private sealed class ChangedDomainBaseContext(DbContextOptions<ChangedDomainBaseContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureTypes(modelBuilder, altered: false);
            modelBuilder.HasDomain("positive_integer", "bigint", schema: "udt_tests");
        }
    }

    private sealed class InsertedCompositeAttributeContext(
        DbContextOptions<InsertedCompositeAttributeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureTypes(modelBuilder, altered: false);
            modelBuilder.HasComposite(
                "address",
                composite => composite.HasAttribute("country", "text")
                    .HasAttribute("street", "text")
                    .HasAttribute("zip", "udt_tests.positive_integer"),
                "udt_tests");
        }
    }

    private sealed class EmptyTypesContext(DbContextOptions<EmptyTypesContext> options) : DbContext(options);
}

#pragma warning restore EF1001
