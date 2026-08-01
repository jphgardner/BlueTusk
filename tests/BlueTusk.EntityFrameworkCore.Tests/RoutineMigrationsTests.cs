using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Routines;
using BlueTusk.EntityFrameworkCore.Routines.Internal;
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

public sealed class RoutineMigrationsTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Model_metadata_creates_overloaded_functions_and_procedure_with_exact_SQL()
    {
        using var context = CreateContext<InitialRoutinesContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .ToArray();

        Assert.IsType<EnsureSchemaOperation>(operations[0]);
        Assert.Equal(4, operations.OfType<CreateBlueTuskRoutineOperation>().Count());
        var definitions = model.GetBlueTuskRoutines();
        Assert.Equal(4, definitions.Routines.Count);
        Assert.Equal(
            2,
            definitions.Routines.Count(definition => definition.Name == "format_value"));
        var calculate = definitions.Routines.Single(definition => definition.Name == "calculate_total");
        Assert.Equal("numeric, numeric", calculate.InputArgumentTypesSql);
        Assert.Contains("DEFAULT 0.2", calculate.ArgumentsSql, StringComparison.Ordinal);
        Assert.Equal("numeric", calculate.ResultSql);

        var sql = string.Concat(
            context.GetService<IMigrationsSqlGenerator>()
                .Generate(operations, model)
                .Select(command => command.CommandText));
        Assert.Contains(
            "CREATE FUNCTION \"routine_tests\".\"calculate_total\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("IMMUTABLE", sql, StringComparison.Ordinal);
        Assert.Contains("RETURNS NULL ON NULL INPUT", sql, StringComparison.Ordinal);
        Assert.Contains("PARALLEL SAFE", sql, StringComparison.Ordinal);
        Assert.Contains("DEFAULT 0.2", sql, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE PROCEDURE \"routine_tests\".\"record_call\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("LANGUAGE \"plpgsql\"", sql, StringComparison.Ordinal);
        Assert.Contains("SECURITY DEFINER", sql, StringComparison.Ordinal);
        Assert.Contains("SET search_path TO routine_tests, pg_temp", sql, StringComparison.Ordinal);
        Assert.Contains("AS $bluetusk$", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_differ_replaces_bodies_and_defaults_without_losing_overload_identity()
    {
        using var initialContext = CreateContext<InitialRoutinesContext>(OfflineConnectionString);
        using var alteredContext = CreateContext<AlteredRoutinesContext>(OfflineConnectionString);
        var source = initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var alteredModel = alteredContext.GetService<IDesignTimeModel>().Model;
        var operations = initialContext.GetService<IMigrationsModelDiffer>()
            .GetDifferences(source, alteredModel.GetRelationalModel())
            .ToArray();

        var replacements = operations.OfType<ReplaceBlueTuskRoutineOperation>().ToArray();
        Assert.Equal(2, replacements.Length);
        Assert.Contains(replacements, operation => operation.Definition.Name == "calculate_total");
        Assert.Contains(replacements, operation => operation.Definition.Name == "record_call");
        Assert.Empty(operations.OfType<CreateBlueTuskRoutineOperation>());
        Assert.Empty(operations.OfType<DropBlueTuskRoutineOperation>());
        var sql = string.Concat(
            alteredContext.GetService<IMigrationsSqlGenerator>()
                .Generate(operations, alteredModel)
                .Select(command => command.CommandText));
        Assert.Contains("DEFAULT 0.25", sql, StringComparison.Ordinal);
        Assert.Contains("amount * (1 + tax_rate) + 1", sql, StringComparison.Ordinal);
        Assert.Contains("upper(message)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_differ_rejects_changes_PostgreSQL_cannot_replace_in_place()
    {
        using var initialContext = CreateContext<InitialRoutinesContext>(OfflineConnectionString);
        using var returnContext = CreateContext<ChangedReturnContext>(OfflineConnectionString);
        using var parameterContext = CreateContext<RenamedParameterContext>(OfflineConnectionString);
        using var kindSourceContext = CreateContext<KindFunctionContext>(OfflineConnectionString);
        using var kindTargetContext = CreateContext<KindProcedureContext>(OfflineConnectionString);
        var differ = initialContext.GetService<IMigrationsModelDiffer>();
        var source = initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        Assert.Contains(
            "different return type",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                source,
                returnContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "parameter names, modes, or output arguments",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                source,
                parameterContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "between FUNCTION and PROCEDURE",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                kindSourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
                kindTargetContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Signature_changes_create_new_overload_and_drop_old_overload_destructively()
    {
        using var initialContext = CreateContext<InitialRoutinesContext>(OfflineConnectionString);
        using var changedContext = CreateContext<ChangedSignatureContext>(OfflineConnectionString);
        var operations = initialContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            changedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();

        var create = Assert.Single(operations.OfType<CreateBlueTuskRoutineOperation>());
        Assert.Equal("bigint, numeric", create.Definition.InputArgumentTypesSql);
        var drop = Assert.Single(operations.OfType<DropBlueTuskRoutineOperation>());
        Assert.Contains("numeric", drop.IdentityArgumentsSql, StringComparison.Ordinal);
        Assert.True(drop.IsDestructiveChange);
        Assert.True(Array.IndexOf(operations, create) < Array.IndexOf(operations, drop));
    }

    [Fact]
    public void Routine_and_user_defined_type_operations_preserve_cross_object_dependency_order()
    {
        using var context = CreateContext<RoutineWithDomainContext>(OfflineConnectionString);
        using var emptyContext = CreateContext<EmptyContext>(OfflineConnectionString);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var creates = differ.GetDifferences(null, model).ToArray();
        var drops = differ.GetDifferences(
            model,
            emptyContext.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();

        Assert.True(
            Array.FindIndex(creates, operation => operation is CreateBlueTuskDomainTypeOperation) <
            Array.FindIndex(creates, operation => operation is CreateBlueTuskRoutineOperation));
        Assert.True(
            Array.FindIndex(drops, operation => operation is DropBlueTuskRoutineOperation) <
            Array.FindIndex(drops, operation => operation is DropBlueTuskDomainTypeOperation));
    }

    [Fact]
    public void SQL_standard_bodies_are_created_after_and_dropped_before_relational_dependencies()
    {
        using var context = CreateContext<TrackedRoutineContext>(OfflineConnectionString);
        using var emptyContext = CreateContext<EmptyContext>(OfflineConnectionString);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var creates = differ.GetDifferences(null, model).ToArray();
        var drops = differ.GetDifferences(
            model,
            emptyContext.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();

        var createTableIndex = Array.FindIndex(creates, operation => operation is CreateTableOperation);
        var createRoutineIndex = Array.FindIndex(creates, operation => operation is CreateBlueTuskRoutineOperation);
        Assert.True(
            createTableIndex >= 0 && createTableIndex < createRoutineIndex,
            string.Join(", ", creates.Select(operation => operation.GetType().Name)));
        var dropRoutineIndex = Array.FindIndex(drops, operation => operation is DropBlueTuskRoutineOperation);
        var dropTableIndex = Array.FindIndex(drops, operation => operation is DropTableOperation);
        Assert.True(
            dropRoutineIndex >= 0 && dropRoutineIndex < dropTableIndex,
            string.Join(", ", drops.Select(operation => operation.GetType().Name)));
    }

    [Fact]
    public void Design_time_generator_scaffolds_routine_operation_families()
    {
        var initial = CreateDefinitions(altered: false);
        var altered = CreateDefinitions(altered: true);
        var oldFunction = initial.Routines.Single(definition => definition.Name == "calculate_total");
        var function = altered.Routines.Single(definition => definition.Name == "calculate_total");
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
                new CreateBlueTuskRoutineOperation { Definition = oldFunction },
                new ReplaceBlueTuskRoutineOperation
                {
                    OldDefinition = oldFunction,
                    Definition = function,
                },
                new DropBlueTuskRoutineOperation
                {
                    Kind = BlueTuskRoutineKind.Function,
                    Name = oldFunction.Name,
                    Schema = oldFunction.Schema,
                    IdentityArgumentsSql = oldFunction.IdentityArgumentsSql,
                },
                new RenameBlueTuskRoutineOperation
                {
                    Kind = BlueTuskRoutineKind.Function,
                    Name = oldFunction.Name,
                    Schema = oldFunction.Schema,
                    IdentityArgumentsSql = oldFunction.IdentityArgumentsSql,
                    NewName = "compute_total",
                    NewSchema = oldFunction.Schema,
                },
            ],
            builder);

        var code = builder.ToString();
        Assert.Contains("CreateBlueTuskRoutine", code, StringComparison.Ordinal);
        Assert.Contains("ReplaceBlueTuskRoutine", code, StringComparison.Ordinal);
        Assert.Contains("DropBlueTuskRoutine", code, StringComparison.Ordinal);
        Assert.Contains("RenameBlueTuskRoutine", code, StringComparison.Ordinal);
        Assert.Contains("BlueTuskRoutineKind.Function", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Routines_execute_round_trip_scaffold_replace_and_rename_across_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS routine_tests CASCADE");

        try
        {
            await ExecuteNonQueryAsync(
                connectionString,
                """
                CREATE SCHEMA routine_tests;
                CREATE TABLE routine_tests.call_log (
                    id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    message text NOT NULL);
                """);
            using var initialContext = CreateContext<InitialRoutinesContext>(connectionString);
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
                "SELECT routine_tests.calculate_total(100) = 120"));
            Assert.Equal("42", await ExecuteScalarAsync(
                connectionString,
                "SELECT routine_tests.format_value(42)"));
            Assert.Equal("HELLO", await ExecuteScalarAsync(
                connectionString,
                "SELECT routine_tests.format_value('hello'::text)"));
            await ExecuteNonQueryAsync(connectionString, "CALL routine_tests.record_call('first')");
            Assert.Equal(1, await ExecuteScalarAsync(
                connectionString,
                "SELECT count(*)::integer FROM routine_tests.call_log"));

            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["routine_tests"]));
            var discovered = BlueTuskRoutineMetadata.Deserialize(
                Assert.IsType<string>(databaseModel[BlueTuskRoutineMetadata.AnnotationName]));
            Assert.Equal(4, discovered.Routines.Count);
            Assert.Equal(
                ["integer", "text"],
                discovered.Routines.Where(definition => definition.Name == "format_value")
                    .Select(definition => definition.InputArgumentTypesSql)
                    .Order(StringComparer.Ordinal));
            Assert.Contains(
                "CREATE OR REPLACE FUNCTION routine_tests.calculate_total",
                discovered.Routines.Single(definition => definition.Name == "calculate_total").CreateOrReplaceSql,
                StringComparison.Ordinal);

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var provider = services.BuildServiceProvider())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], ["routine_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "RoutineContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "RoutineModels",
                        ModelNamespace = "RoutineModels",
                        RootNamespace = "RoutineModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasBlueTuskRoutines(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
                Assert.Single(scaffolded.AdditionalFiles);
            }

            using var alteredContext = CreateContext<AlteredRoutinesContext>(connectionString);
            var alteredModel = alteredContext.GetService<IDesignTimeModel>().Model;
            var alteredOperations = alteredContext.GetService<IMigrationsModelDiffer>().GetDifferences(
                initialModel.GetRelationalModel(),
                alteredModel.GetRelationalModel());
            foreach (var command in alteredContext.GetService<IMigrationsSqlGenerator>()
                         .Generate(alteredOperations, alteredModel))
            {
                await ExecuteNonQueryAsync(connectionString, command.CommandText);
            }

            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT routine_tests.calculate_total(100) = 126"));
            await ExecuteNonQueryAsync(connectionString, "CALL routine_tests.record_call('second')");
            Assert.Equal("SECOND", await ExecuteScalarAsync(
                connectionString,
                "SELECT message FROM routine_tests.call_log ORDER BY id DESC LIMIT 1"));

            var calculate = alteredModel.GetBlueTuskRoutines().Routines
                .Single(definition => definition.Name == "calculate_total");
            var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            migration.RenameBlueTuskRoutine(
                BlueTuskRoutineKind.Function,
                calculate.Name,
                calculate.IdentityArgumentsSql,
                "compute_total",
                calculate.Schema,
                calculate.Schema);
            var renameCommand = Assert.Single(alteredContext.GetService<IMigrationsSqlGenerator>()
                .Generate(migration.Operations, alteredModel));
            await ExecuteNonQueryAsync(connectionString, renameCommand.CommandText);
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT to_regprocedure('routine_tests.compute_total(numeric,numeric)') IS NOT NULL"));
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS routine_tests CASCADE");
        }
    }

    private static BlueTuskRoutineDefinitionSet CreateDefinitions(bool altered)
    {
        var modelBuilder = new ModelBuilder();
        ConfigureRoutines(modelBuilder, altered);
        return modelBuilder.Model.GetBlueTuskRoutines();
    }

    private static void ConfigureRoutines(ModelBuilder modelBuilder, bool altered)
    {
        modelBuilder.HasBlueTuskFunction(
            "calculate_total",
            "numeric",
            altered
                ? "SELECT amount * (1 + tax_rate) + 1"
                : "SELECT amount * (1 + tax_rate)",
            function => function
                .HasParameter("numeric", "amount")
                .HasParameter("numeric", "tax_rate", defaultSql: altered ? "0.25" : "0.2")
                .HasVolatility(BlueTuskFunctionVolatility.Immutable)
                .IsStrict()
                .HasParallelSafety(BlueTuskFunctionParallelSafety.Safe)
                .HasCost(1),
            "routine_tests");
        modelBuilder.HasBlueTuskFunction(
            "format_value",
            "text",
            "SELECT value::text",
            function => function
                .HasParameter("integer", "value")
                .HasVolatility(BlueTuskFunctionVolatility.Immutable)
                .IsStrict()
                .HasParallelSafety(BlueTuskFunctionParallelSafety.Safe),
            "routine_tests");
        modelBuilder.HasBlueTuskFunction(
            "format_value",
            "text",
            "SELECT upper(value)",
            function => function
                .HasParameter("text", "value")
                .HasVolatility(BlueTuskFunctionVolatility.Immutable)
                .IsStrict()
                .HasParallelSafety(BlueTuskFunctionParallelSafety.Safe),
            "routine_tests");
        modelBuilder.HasBlueTuskProcedure(
            "record_call",
            altered
                ? "BEGIN INSERT INTO routine_tests.call_log(message) VALUES (upper(message)); END"
                : "BEGIN INSERT INTO routine_tests.call_log(message) VALUES (message); END",
            procedure => procedure
                .UseLanguage("plpgsql")
                .IsSecurityDefiner()
                .HasConfiguration("search_path", "routine_tests, pg_temp")
                .HasParameter("text", "message"),
            "routine_tests");
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

    private sealed class InitialRoutinesContext(DbContextOptions<InitialRoutinesContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureRoutines(modelBuilder, altered: false);
    }

    private sealed class AlteredRoutinesContext(DbContextOptions<AlteredRoutinesContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureRoutines(modelBuilder, altered: true);
    }

    private sealed class ChangedReturnContext(DbContextOptions<ChangedReturnContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureRoutines(modelBuilder, altered: false);
            modelBuilder.HasBlueTuskFunction(
                "calculate_total",
                "integer",
                "SELECT 1",
                function => function
                    .HasParameter("numeric", "amount")
                    .HasParameter("numeric", "tax_rate", defaultSql: "0.2"),
                "routine_tests");
        }
    }

    private sealed class RenamedParameterContext(DbContextOptions<RenamedParameterContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureRoutines(modelBuilder, altered: false);
            modelBuilder.HasBlueTuskFunction(
                "calculate_total",
                "numeric",
                "SELECT subtotal * (1 + tax_rate)",
                function => function
                    .HasParameter("numeric", "subtotal")
                    .HasParameter("numeric", "tax_rate", defaultSql: "0.2"),
                "routine_tests");
        }
    }

    private sealed class ChangedSignatureContext(DbContextOptions<ChangedSignatureContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureRoutines(modelBuilder, altered: false);
            modelBuilder.HasNoBlueTuskRoutine(
                    BlueTuskRoutineKind.Function,
                    "calculate_total",
                    "numeric, numeric",
                    "routine_tests")
                .HasBlueTuskFunction(
                    "calculate_total",
                    "numeric",
                    "SELECT amount * (1 + tax_rate)",
                    function => function
                        .HasParameter("bigint", "amount")
                        .HasParameter("numeric", "tax_rate", defaultSql: "0.2"),
                    "routine_tests");
        }
    }

    private sealed class KindFunctionContext(DbContextOptions<KindFunctionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.HasBlueTuskFunction(
                "kind_change",
                "void",
                "SELECT NULL",
                schema: "routine_tests");
    }

    private sealed class KindProcedureContext(DbContextOptions<KindProcedureContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.HasBlueTuskProcedure(
                "kind_change",
                "SELECT NULL",
                schema: "routine_tests");
    }

    private sealed class RoutineWithDomainContext(DbContextOptions<RoutineWithDomainContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasBlueTuskDomain(
                "positive_integer",
                "integer",
                domain => domain.HasCheckConstraint("positive", "VALUE > 0"),
                "routine_tests");
            modelBuilder.HasBlueTuskFunction(
                "echo_positive",
                "routine_tests.positive_integer",
                "SELECT value",
                function => function.HasParameter("routine_tests.positive_integer", "value"),
                "routine_tests");
        }
    }

    private sealed class TrackedRoutineContext(DbContextOptions<TrackedRoutineContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TrackedRow>(entity =>
            {
                entity.ToTable("tracked_rows", "routine_tests");
                entity.HasKey(row => row.Id);
            });
            modelBuilder.HasBlueTuskRoutine(new BlueTuskRoutineDefinition(
                BlueTuskRoutineKind.Function,
                "tracked_count",
                "routine_tests",
                "",
                "",
                "",
                "bigint",
                """
                CREATE OR REPLACE FUNCTION routine_tests.tracked_count()
                RETURNS bigint
                LANGUAGE sql
                BEGIN ATOMIC
                    RETURN (SELECT count(*) FROM routine_tests.tracked_rows);
                END
                """,
                HasTrackedBodyDependencies: true));
        }
    }

    private sealed class TrackedRow
    {
        public int Id { get; set; }
    }

    private sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options);
}

#pragma warning restore EF1001
