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

public sealed class RangeTypeMigrationsTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Range_metadata_generates_all_options_and_orders_multirange_dependants()
    {
        using var context = CreateContext<RangeContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .ToArray();

        var createRange = Assert.Single(operations.OfType<CreateRangeTypeOperation>());
        var createDomain = Assert.Single(operations.OfType<CreateDomainTypeOperation>());
        Assert.True(Array.IndexOf(operations, createRange) < Array.IndexOf(operations, createDomain));
        var definition = Assert.Single(model.GetUserDefinedTypes().Ranges);
        Assert.Equal("measurement_multirange", definition.MultirangeType.Name);
        Assert.Equal("range_tests", definition.MultirangeType.Schema);

        var sql = string.Concat(context.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, model)
            .Select(command => command.CommandText));
        Assert.Contains(
            "CREATE TYPE \"range_tests\".\"measurement_range\" AS RANGE (" +
            "SUBTYPE = \"pg_catalog\".\"float8\", " +
            "SUBTYPE_OPCLASS = \"pg_catalog\".\"float8_ops\", " +
            "COLLATION = \"range_tests\".\"measurement_collation\", " +
            "CANONICAL = \"range_tests\".\"normalize_measurement\", " +
            "SUBTYPE_DIFF = \"pg_catalog\".\"float8mi\", " +
            "MULTIRANGE_TYPE_NAME = \"range_tests\".\"measurement_multirange\");",
            sql,
            StringComparison.Ordinal);

        using var emptyContext = CreateContext<EmptyRangeContext>(OfflineConnectionString);
        var drops = context.GetService<IMigrationsModelDiffer>().GetDifferences(
                model.GetRelationalModel(),
                emptyContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .Where(operation => operation is DropDomainTypeOperation or DropRangeTypeOperation)
            .ToArray();
        Assert.Collection(
            drops,
            operation => Assert.IsType<DropDomainTypeOperation>(operation),
            operation => Assert.IsType<DropRangeTypeOperation>(operation));
        var dropSql = string.Concat(context.GetService<IMigrationsSqlGenerator>()
            .Generate(drops, model)
            .Select(command => command.CommandText));
        Assert.Contains(
            "DROP TYPE \"range_tests\".\"measurement_range\" RESTRICT;",
            dropSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Range_default_multirange_name_matches_PostgreSQL_and_names_are_validated_globally()
    {
        var modelBuilder = new ModelBuilder();
        modelBuilder.HasRange("foo_range_range", "integer", schema: "range_tests");
        var range = Assert.Single(modelBuilder.Model.GetUserDefinedTypes().Ranges);
        Assert.Equal("foo_multirange_range", range.MultirangeType.Name);
        Assert.Empty(BlueTuskUserDefinedTypeMetadata.Deserialize(
            "{\"enums\":[],\"domains\":[],\"composites\":[]}").Ranges);

        var collision = new ModelBuilder();
        collision.HasEnum("measurement_multirange", ["one"], "range_tests");
        Assert.Throws<ArgumentException>(() => collision.HasRange(
            "measurement_range",
            "float8",
            rangeBuilder => rangeBuilder.HasMultirangeType("measurement_multirange"),
            "range_tests"));
    }

    [Fact]
    public void Range_body_changes_require_an_explicit_replacement()
    {
        using var sourceContext = CreateContext<LiveRangeContext>(OfflineConnectionString);
        using var targetContext = CreateContext<ChangedRangeContext>(OfflineConnectionString);
        var source = sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var target = targetContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var error = Assert.Throws<InvalidOperationException>(() =>
            sourceContext.GetService<IMigrationsModelDiffer>().GetDifferences(source, target));
        Assert.Contains("cannot change its subtype", error.Message, StringComparison.Ordinal);
        Assert.Contains("replacement migration", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_and_multirange_move_and_rename_together_in_dependency_safe_order()
    {
        using var sourceContext = CreateContext<LiveRangeContext>(OfflineConnectionString);
        using var targetContext = CreateContext<MovedRangeContext>(OfflineConnectionString);
        var targetModel = targetContext.GetService<IDesignTimeModel>().Model;
        var operations = sourceContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            targetModel.GetRelationalModel());
        var rename = Assert.Single(operations.OfType<RenameRangeTypeOperation>());
        Assert.Equal("measurement_multirange", rename.MultirangeName);
        Assert.Equal("reading_multirange", rename.NewMultirangeName);
        Assert.Contains(operations, operation =>
            operation is EnsureSchemaOperation { Name: "range_tests_next" });

        var sql = string.Concat(targetContext.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, targetModel)
            .Select(command => command.CommandText));
        var multirangeMove = sql.IndexOf(
            "ALTER TYPE \"range_tests\".\"measurement_multirange\" SET SCHEMA \"range_tests_next\"",
            StringComparison.Ordinal);
        var rangeMove = sql.IndexOf(
            "ALTER TYPE \"range_tests\".\"measurement_range\" SET SCHEMA \"range_tests_next\"",
            StringComparison.Ordinal);
        Assert.True(multirangeMove >= 0 && multirangeMove < rangeMove);
        Assert.Contains(
            "ALTER TYPE \"range_tests_next\".\"measurement_multirange\" RENAME TO \"reading_multirange\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TYPE \"range_tests_next\".\"measurement_range\" RENAME TO \"reading_range\"",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Design_time_generator_scaffolds_range_operations()
    {
        var definition = CreateLiveRangeDefinition("measurement_range", "range_tests", "measurement_multirange");
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
                new CreateRangeTypeOperation { Definition = definition },
                new RenameRangeTypeOperation
                {
                    Name = "measurement_range",
                    Schema = "range_tests",
                    NewName = "reading_range",
                    NewSchema = "range_tests_next",
                    MultirangeName = "measurement_multirange",
                    MultirangeSchema = "range_tests",
                    NewMultirangeName = "reading_multirange",
                    NewMultirangeSchema = "range_tests_next",
                },
                new DropRangeTypeOperation { Name = "reading_range", Schema = "range_tests_next" },
            ],
            builder);

        var code = builder.ToString();
        Assert.Contains("CreateRangeType", code, StringComparison.Ordinal);
        Assert.Contains("RenameRangeType", code, StringComparison.Ordinal);
        Assert.Contains("DropRangeType", code, StringComparison.Ordinal);
        Assert.Contains("measurement_multirange", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Range_and_multirange_round_trip_scaffold_move_and_drop_across_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(
            connectionString,
            "DROP SCHEMA IF EXISTS range_tests_next CASCADE; DROP SCHEMA IF EXISTS range_tests CASCADE");

        try
        {
            using var initialContext = CreateContext<LiveRangeContext>(connectionString);
            var initialModel = initialContext.GetService<IDesignTimeModel>().Model;
            var differ = initialContext.GetService<IMigrationsModelDiffer>();
            await ExecuteAsync(
                connectionString,
                initialContext.GetService<IMigrationsSqlGenerator>().Generate(
                    differ.GetDifferences(null, initialModel.GetRelationalModel()),
                    initialModel));

            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT range_tests.measurement_range(1::float8, 3::float8, '[)') @> 2::float8"));
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT range_tests.measurement_multirange(" +
                "range_tests.measurement_range(1::float8, 3::float8, '[)')) @> 2::float8"));

            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["range_tests"]));
            var discovered = BlueTuskUserDefinedTypeMetadata.Deserialize(
                Assert.IsType<string>(databaseModel[BlueTuskUserDefinedTypeMetadata.AnnotationName]));
            var range = Assert.Single(discovered.Ranges);
            Assert.Equal(new BlueTuskQualifiedName("float8", "pg_catalog"), range.Subtype);
            Assert.Equal(new BlueTuskQualifiedName("float8_ops", "pg_catalog"), range.SubtypeOperatorClass);
            Assert.Equal(new BlueTuskQualifiedName("float8mi", "pg_catalog"), range.SubtypeDifferenceFunction);
            Assert.Equal(new BlueTuskQualifiedName("measurement_multirange", "range_tests"), range.MultirangeType);

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var serviceProvider = services.BuildServiceProvider())
            {
                var scaffolded = serviceProvider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], ["range_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "RangeContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "RangeModels",
                        ModelNamespace = "RangeModels",
                        RootNamespace = "RangeModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasUserDefinedTypes(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
            }

            using var movedContext = CreateContext<MovedRangeContext>(connectionString);
            var movedModel = movedContext.GetService<IDesignTimeModel>().Model;
            await ExecuteAsync(
                connectionString,
                movedContext.GetService<IMigrationsSqlGenerator>().Generate(
                    differ.GetDifferences(initialModel.GetRelationalModel(), movedModel.GetRelationalModel()),
                    movedModel));
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT to_regtype('range_tests_next.reading_range') IS NOT NULL " +
                "AND to_regtype('range_tests_next.reading_multirange') IS NOT NULL"));
            Assert.Equal(false, await ExecuteScalarAsync(
                connectionString,
                "SELECT to_regtype('range_tests.measurement_range') IS NOT NULL " +
                "OR to_regtype('range_tests.measurement_multirange') IS NOT NULL"));

            using var emptyContext = CreateContext<EmptyRangeContext>(connectionString);
            var emptyModel = emptyContext.GetService<IDesignTimeModel>().Model;
            var drop = Assert.Single(differ.GetDifferences(
                movedModel.GetRelationalModel(),
                emptyModel.GetRelationalModel()).OfType<DropRangeTypeOperation>());
            Assert.True(drop.IsDestructiveChange);
            await ExecuteAsync(
                connectionString,
                emptyContext.GetService<IMigrationsSqlGenerator>().Generate([drop], emptyModel));
            Assert.Equal(false, await ExecuteScalarAsync(
                connectionString,
                "SELECT to_regtype('range_tests_next.reading_range') IS NOT NULL " +
                "OR to_regtype('range_tests_next.reading_multirange') IS NOT NULL"));
        }
        finally
        {
            await ExecuteNonQueryAsync(
                connectionString,
                "DROP SCHEMA IF EXISTS range_tests_next CASCADE; DROP SCHEMA IF EXISTS range_tests CASCADE");
        }
    }

    private static void ConfigureLiveRange(
        ModelBuilder modelBuilder,
        string name,
        string schema,
        string multirangeName,
        string subtypeName = "float8") =>
        modelBuilder.HasRange(
            name,
            subtypeName,
            range => range.UseSubtypeOperatorClass(subtypeName == "float8" ? "float8_ops" : "int4_ops")
                .HasSubtypeDifferenceFunction(subtypeName == "float8" ? "float8mi" : "int4mi")
                .HasMultirangeType(multirangeName),
            schema);

    private static BlueTuskRangeTypeDefinition CreateLiveRangeDefinition(
        string name,
        string schema,
        string multirangeName)
    {
        var modelBuilder = new ModelBuilder();
        ConfigureLiveRange(modelBuilder, name, schema, multirangeName);
        return Assert.Single(modelBuilder.Model.GetUserDefinedTypes().Ranges);
    }

    private static TContext CreateContext<TContext>(string connectionString)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    private static async Task ExecuteAsync(
        string connectionString,
        IReadOnlyList<MigrationCommand> commands)
    {
        foreach (var command in commands)
        {
            await ExecuteNonQueryAsync(connectionString, command.CommandText);
        }
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

    private sealed class RangeContext(DbContextOptions<RangeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasRange(
                "measurement_range",
                "float8",
                range => range.UseSubtypeOperatorClass("float8_ops", "pg_catalog")
                    .UseCollation("measurement_collation", "range_tests")
                    .HasCanonicalFunction("normalize_measurement", "range_tests")
                    .HasSubtypeDifferenceFunction("float8mi", "pg_catalog")
                    .HasMultirangeType("measurement_multirange"),
                "range_tests",
                "pg_catalog");
            modelBuilder.HasDomain(
                "measurement_set",
                "range_tests.measurement_multirange",
                schema: "range_tests");
        }
    }

    private sealed class LiveRangeContext(DbContextOptions<LiveRangeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureLiveRange(modelBuilder, "measurement_range", "range_tests", "measurement_multirange");
    }

    private sealed class ChangedRangeContext(DbContextOptions<ChangedRangeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureLiveRange(modelBuilder, "measurement_range", "range_tests", "measurement_multirange", "int4");
    }

    private sealed class MovedRangeContext(DbContextOptions<MovedRangeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureLiveRange(modelBuilder, "reading_range", "range_tests_next", "reading_multirange");
    }

    private sealed class EmptyRangeContext(DbContextOptions<EmptyRangeContext> options) : DbContext(options);
}

#pragma warning restore EF1001
