using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.SchemaPrograms;
using BlueTusk.EntityFrameworkCore.SchemaPrograms.Internal;
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

public sealed class SchemaProgramMigrationsTests
{
    private const string Offline = "Host=localhost;Database=unused;Username=unused;Password=unused";
    private const string Schema = "schema_program_tests";

    [Fact]
    public void Schema_program_SQL_diffs_ordering_and_generated_CSharp_are_preserved()
    {
        using var initial = Create<SchemaProgramContext>(Offline);
        using var changed = Create<ChangedSchemaProgramContext>(Offline);
        using var removed = Create<NoSchemaProgramContext>(Offline);
        var model = initial.GetService<IDesignTimeModel>().Model;
        var differ = initial.GetService<IMigrationsModelDiffer>();
        var creates = differ.GetDifferences(null, model.GetRelationalModel()).ToArray();
        var createOperator = Assert.Single(creates.OfType<CreateBlueTuskOperatorOperation>());
        var createFamily = Assert.Single(creates.OfType<CreateBlueTuskOperatorFamilyOperation>());
        var createClass = Assert.Single(creates.OfType<CreateBlueTuskOperatorClassOperation>());
        var createCast = Assert.Single(creates.OfType<CreateBlueTuskCastOperation>());
        var createAggregate = Assert.Single(creates.OfType<CreateBlueTuskAggregateOperation>());
        Assert.True(Array.IndexOf(creates, createOperator) < Array.IndexOf(creates, createFamily));
        Assert.True(Array.IndexOf(creates, createFamily) < Array.IndexOf(creates, createClass));
        Assert.True(Array.IndexOf(creates, createClass) < Array.IndexOf(creates, createCast));
        Assert.True(Array.IndexOf(creates, createCast) < Array.IndexOf(creates, createAggregate));

        var generator = initial.GetService<IMigrationsSqlGenerator>();
        var sql = string.Concat(generator.Generate(creates, model).Select(command => command.CommandText));
        Assert.Contains(
            "CREATE OPERATOR \"schema_program_tests\".=== (FUNCTION = \"pg_catalog\".\"int4eq\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("COMMUTATOR = OPERATOR(\"schema_program_tests\".===)", sql,
            StringComparison.Ordinal);
        Assert.Contains("CREATE OPERATOR FAMILY \"schema_program_tests\".\"int_family\" USING \"btree\"",
            sql, StringComparison.Ordinal);
        Assert.Contains("CREATE OPERATOR CLASS \"schema_program_tests\".\"int_ops\"", sql,
            StringComparison.Ordinal);
        Assert.Contains("OPERATOR 1 \"pg_catalog\".< (integer, integer) FOR SEARCH", sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "FUNCTION 1 (integer, integer) \"pg_catalog\".\"btint4cmp\" (integer, integer)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE CAST (schema_program_tests.mood AS text) WITH INOUT AS ASSIGNMENT",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE AGGREGATE \"schema_program_tests\".\"product\" (integer) " +
            "(SFUNC = \"pg_catalog\".\"int4mul\", STYPE = integer, INITCOND = '1', PARALLEL = SAFE)",
            sql,
            StringComparison.Ordinal);

        var changedModel = changed.GetService<IDesignTimeModel>().Model;
        var changes = differ.GetDifferences(model.GetRelationalModel(), changedModel.GetRelationalModel()).ToArray();
        Assert.Single(changes.OfType<ReplaceBlueTuskOperatorOperation>());
        Assert.Single(changes.OfType<AlterBlueTuskOperatorFamilyOperation>());
        Assert.Single(changes.OfType<ReplaceBlueTuskOperatorClassOperation>());
        Assert.Single(changes.OfType<ReplaceBlueTuskCastOperation>());
        Assert.Single(changes.OfType<ReplaceBlueTuskAggregateOperation>());
        var changedSql = string.Concat(generator.Generate(changes, changedModel)
            .Select(command => command.CommandText));
        Assert.Contains("ALTER OPERATOR FAMILY", changedSql, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE AGGREGATE", changedSql, StringComparison.Ordinal);

        var removals = differ.GetDifferences(
            changedModel.GetRelationalModel(),
            removed.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();
        var dropAggregate = Assert.Single(removals.OfType<DropBlueTuskAggregateOperation>());
        var dropCast = Assert.Single(removals.OfType<DropBlueTuskCastOperation>());
        var dropClass = Assert.Single(removals.OfType<DropBlueTuskOperatorClassOperation>());
        var dropFamily = Assert.Single(removals.OfType<DropBlueTuskOperatorFamilyOperation>());
        var dropOperator = Assert.Single(removals.OfType<DropBlueTuskOperatorOperation>());
        Assert.True(Array.IndexOf(removals, dropAggregate) < Array.IndexOf(removals, dropCast));
        Assert.True(Array.IndexOf(removals, dropCast) < Array.IndexOf(removals, dropClass));
        Assert.True(Array.IndexOf(removals, dropClass) < Array.IndexOf(removals, dropFamily));
        Assert.True(Array.IndexOf(removals, dropFamily) < Array.IndexOf(removals, dropOperator));

        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateBlueTuskOperator(createOperator.Definition);
        migration.ReplaceBlueTuskOperator(createOperator.Definition, createOperator.Definition with
        {
            SupportsHashJoins = false,
        });
        migration.DropBlueTuskOperator(createOperator.Definition);
        migration.CreateBlueTuskOperatorFamily(createFamily.Definition);
        migration.AlterBlueTuskOperatorFamily(createFamily.Definition, createFamily.Definition with
        {
            Operators = [OperatorMember("=", "integer", "bigint")],
        });
        migration.DropBlueTuskOperatorFamily(createFamily.Definition);
        migration.CreateBlueTuskOperatorClass(createClass.Definition);
        migration.ReplaceBlueTuskOperatorClass(createClass.Definition, createClass.Definition with
        {
            IsDefault = false,
        });
        migration.DropBlueTuskOperatorClass(createClass.Definition);
        migration.CreateBlueTuskCast(createCast.Definition);
        migration.ReplaceBlueTuskCast(createCast.Definition, createCast.Definition with
        {
            Context = BlueTuskCastContext.Explicit,
        });
        migration.DropBlueTuskCast(createCast.Definition);
        migration.CreateBlueTuskAggregate(createAggregate.Definition);
        migration.ReplaceBlueTuskAggregate(createAggregate.Definition, createAggregate.Definition with
        {
            InitialCondition = "2",
        });
        migration.DropBlueTuskAggregate(createAggregate.Definition);
        using var provider = DesignServices();
        var codeBuilder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, codeBuilder);
        var code = codeBuilder.ToString();
        Assert.Contains("CreateBlueTuskOperator(", code, StringComparison.Ordinal);
        Assert.Contains("AlterBlueTuskOperatorFamily(", code, StringComparison.Ordinal);
        Assert.Contains("ReplaceBlueTuskOperatorClass(", code, StringComparison.Ordinal);
        Assert.Contains("ReplaceBlueTuskCast(", code, StringComparison.Ordinal);
        Assert.Contains("ReplaceBlueTuskAggregate(", code, StringComparison.Ordinal);
        Assert.Contains("DropBlueTuskAggregate(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Extended_schema_program_forms_generate_their_PostgreSQL_options()
    {
        using var context = Create<SchemaProgramContext>(Offline);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var unary = new BlueTuskOperatorDefinition(
            "!!",
            Schema,
            null,
            "boolean",
            new BlueTuskSchemaProgramName("boolnot", "pg_catalog"),
            null,
            null,
            new BlueTuskSchemaProgramName("eqsel", "pg_catalog"),
            new BlueTuskSchemaProgramName("eqjoinsel", "pg_catalog"),
            false,
            false);
        var family = new BlueTuskOperatorFamilyDefinition(
            "distance_family",
            Schema,
            "gist",
            [new BlueTuskOperatorMemberDefinition(
                15,
                new BlueTuskOperatorName("<->", "pg_catalog"),
                "point",
                "point",
                BlueTuskOperatorPurpose.OrderBy,
                new BlueTuskSchemaProgramName("float_ops", "pg_catalog"))],
            [new BlueTuskOperatorFunctionDefinition(
                1,
                "point",
                "point",
                new BlueTuskSchemaProgramName("point_distance", "pg_catalog"),
                ["point", "point"])]);
        var operatorClass = new BlueTuskOperatorClassDefinition(
            "stored_ops",
            Schema,
            "btree",
            "integer",
            false,
            new BlueTuskSchemaProgramName("int_family", Schema),
            [OperatorMember("=", "integer", "integer")],
            [new BlueTuskOperatorFunctionDefinition(
                1,
                "integer",
                "integer",
                new BlueTuskSchemaProgramName("btint4cmp", "pg_catalog"),
                ["integer", "integer"])],
            "bigint");
        var functionCast = new BlueTuskCastDefinition(
            "application.source_type",
            "application.target_type",
            BlueTuskCastMethod.Function,
            new BlueTuskCastFunctionDefinition(
                new BlueTuskSchemaProgramName("convert_source", "application"),
                ["application.source_type", "integer", "boolean"]),
            BlueTuskCastContext.Implicit);
        var binaryCast = new BlueTuskCastDefinition(
            "application.binary_source",
            "application.binary_target",
            BlueTuskCastMethod.Binary,
            null,
            BlueTuskCastContext.Explicit);
        var aggregate = new BlueTuskAggregateDefinition(
            "rank_like",
            Schema,
            "integer ORDER BY integer",
            BlueTuskAggregateKind.HypotheticalSet,
            new BlueTuskSchemaProgramName("state_step", Schema),
            "internal",
            128,
            new BlueTuskSchemaProgramName("finish_state", Schema),
            true,
            BlueTuskAggregateFinalFunctionModify.Shareable,
            new BlueTuskSchemaProgramName("combine_state", Schema),
            new BlueTuskSchemaProgramName("serialize_state", Schema),
            new BlueTuskSchemaProgramName("deserialize_state", Schema),
            "seed",
            new BlueTuskSchemaProgramName("moving_step", Schema),
            new BlueTuskSchemaProgramName("moving_inverse", Schema),
            "internal",
            64,
            new BlueTuskSchemaProgramName("finish_moving", Schema),
            true,
            BlueTuskAggregateFinalFunctionModify.ReadWrite,
            "moving seed",
            new BlueTuskOperatorName("<", "pg_catalog"),
            BlueTuskAggregateParallelSafety.Restricted);

        var sql = string.Concat(generator.Generate(
                [
                    new CreateBlueTuskOperatorOperation { Definition = unary },
                    new CreateBlueTuskOperatorFamilyOperation { Definition = family },
                    new CreateBlueTuskOperatorClassOperation { Definition = operatorClass },
                    new CreateBlueTuskCastOperation { Definition = functionCast },
                    new CreateBlueTuskCastOperation { Definition = binaryCast },
                    new CreateBlueTuskAggregateOperation { Definition = aggregate },
                ])
            .Select(command => command.CommandText));
        Assert.Contains("RIGHTARG = boolean, RESTRICT = \"pg_catalog\".\"eqsel\", " +
                        "JOIN = \"pg_catalog\".\"eqjoinsel\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LEFTARG = boolean", sql, StringComparison.Ordinal);
        Assert.Contains("FOR ORDER BY \"pg_catalog\".\"float_ops\"", sql, StringComparison.Ordinal);
        Assert.Contains("STORAGE bigint", sql, StringComparison.Ordinal);
        Assert.Contains(
            "WITH FUNCTION \"application\".\"convert_source\" " +
            "(application.source_type, integer, boolean) AS IMPLICIT",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("WITHOUT FUNCTION", sql, StringComparison.Ordinal);
        Assert.Contains("SSPACE = 128", sql, StringComparison.Ordinal);
        Assert.Contains("FINALFUNC_EXTRA", sql, StringComparison.Ordinal);
        Assert.Contains("FINALFUNC_MODIFY = SHAREABLE", sql, StringComparison.Ordinal);
        Assert.Contains("COMBINEFUNC = \"schema_program_tests\".\"combine_state\"", sql,
            StringComparison.Ordinal);
        Assert.Contains("SERIALFUNC = \"schema_program_tests\".\"serialize_state\"", sql,
            StringComparison.Ordinal);
        Assert.Contains("MSFUNC = \"schema_program_tests\".\"moving_step\"", sql,
            StringComparison.Ordinal);
        Assert.Contains("MINVFUNC = \"schema_program_tests\".\"moving_inverse\"", sql,
            StringComparison.Ordinal);
        Assert.Contains("MFINALFUNC_MODIFY = READ_WRITE", sql, StringComparison.Ordinal);
        Assert.Contains("SORTOP = \"pg_catalog\".<", sql, StringComparison.Ordinal);
        Assert.Contains("HYPOTHETICAL", sql, StringComparison.Ordinal);
        Assert.Contains("PARALLEL = RESTRICTED", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_program_metadata_rejects_unsafe_or_incomplete_definitions()
    {
        var modelBuilder = new ModelBuilder();
        Assert.Throws<ArgumentException>(() => modelBuilder.HasBlueTuskOperator(
            "=>",
            value => value.HasRightType("integer").UsesFunction("int4eq", "pg_catalog"),
            Schema));
        Assert.Throws<ArgumentException>(() => modelBuilder.HasBlueTuskCast(
            "integer; DROP TABLE data",
            "text"));
        Assert.Throws<ArgumentException>(() => modelBuilder.HasBlueTuskAggregate(
            "broken",
            "integer ORDER BY integer",
            value => value.UsesState("int4mul", "integer", "pg_catalog"),
            Schema));
    }

    [Fact]
    public async Task Schema_programs_round_trip_alter_scaffold_and_drop_across_PostgreSQL()
    {
        var cs = ConnectionString();
        await Cleanup(cs);
        try
        {
            using var initial = Create<SchemaProgramContext>(cs);
            var initialModel = initial.GetService<IDesignTimeModel>().Model;
            await Apply(initial, null, initialModel, cs);
            var database = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], [Schema]));
            var definitions = BlueTuskSchemaProgramMetadata.Deserialize(Assert.IsType<string>(
                database[BlueTuskSchemaProgramMetadata.AnnotationName]));
            var operatorDefinition = Assert.Single(definitions.Operators);
            Assert.Equal("===", operatorDefinition.Name);
            Assert.True(operatorDefinition.SupportsHashJoins);
            var family = Assert.Single(definitions.OperatorFamilies);
            Assert.Empty(family.Operators);
            var operatorClass = Assert.Single(definitions.OperatorClasses);
            Assert.Equal(5, operatorClass.Operators.Count);
            Assert.Single(operatorClass.Functions);
            var cast = Assert.Single(definitions.Casts);
            Assert.Equal(BlueTuskCastContext.Assignment, cast.Context);
            var aggregate = Assert.Single(definitions.Aggregates);
            Assert.Equal("1", aggregate.InitialCondition);
            Assert.Equal(BlueTuskAggregateParallelSafety.Safe, aggregate.ParallelSafety);

            using (var provider = DesignServices())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    cs,
                    new DatabaseModelFactoryOptions([], [Schema]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "SchemaProgramScaffoldContext",
                        ConnectionString = cs,
                        ContextNamespace = "SchemaProgramModels",
                        ModelNamespace = "SchemaProgramModels",
                        RootNamespace = "SchemaProgramModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasBlueTuskSchemaPrograms(", scaffolded.ContextFile.Code,
                    StringComparison.Ordinal);
            }

            using var changed = Create<ChangedSchemaProgramContext>(cs);
            var changedModel = changed.GetService<IDesignTimeModel>().Model;
            await Apply(changed, initialModel, changedModel, cs);
            Assert.Equal(false, await Scalar(cs,
                "SELECT oprcanhash FROM pg_catalog.pg_operator AS operator_entry " +
                "WHERE operator_entry.oprnamespace = 'schema_program_tests'::regnamespace " +
                "AND operator_entry.oprname = '==='"));
            Assert.Equal("2", await Scalar(cs,
                "SELECT aggregate_entry.agginitval FROM pg_catalog.pg_aggregate AS aggregate_entry " +
                "JOIN pg_catalog.pg_proc AS aggregate_proc ON aggregate_proc.oid = aggregate_entry.aggfnoid " +
                "WHERE aggregate_proc.pronamespace = 'schema_program_tests'::regnamespace " +
                "AND aggregate_proc.proname = 'product'"));
            var changedDatabase = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], [Schema]));
            var changedDefinitions = BlueTuskSchemaProgramMetadata.Deserialize(Assert.IsType<string>(
                changedDatabase[BlueTuskSchemaProgramMetadata.AnnotationName]));
            Assert.Single(Assert.Single(changedDefinitions.OperatorFamilies).Operators);
            Assert.Equal(
                "===",
                Assert.Single(Assert.Single(changedDefinitions.OperatorClasses).Operators,
                    member => member.StrategyNumber == 3).Operator.Name);
            Assert.Equal(BlueTuskCastContext.Explicit, Assert.Single(changedDefinitions.Casts).Context);

            using var removed = Create<NoSchemaProgramContext>(cs);
            await Apply(removed, changedModel, removed.GetService<IDesignTimeModel>().Model, cs);
            Assert.Equal(false, await Scalar(cs,
                "SELECT pg_catalog.to_regtype('schema_program_tests.mood') IS NOT NULL OR " +
                "EXISTS (SELECT 1 FROM pg_catalog.pg_operator AS operator_entry " +
                "WHERE operator_entry.oprnamespace = 'schema_program_tests'::pg_catalog.regnamespace)"));
        }
        finally
        {
            await Cleanup(cs);
        }
    }

    private static void Configure(ModelBuilder modelBuilder, bool changed)
    {
        modelBuilder.HasBlueTuskEnum("mood", ["calm", "busy"], Schema);
        modelBuilder.HasBlueTuskOperator(
            "===",
            value => value
                .HasLeftType("integer")
                .HasRightType("integer")
                .UsesFunction("int4eq", "pg_catalog")
                .HasCommutator("===", Schema)
                .SupportsHashJoin(!changed)
                .SupportsMergeJoin(),
            Schema);
        modelBuilder.HasBlueTuskOperatorFamily(
            "int_family",
            "btree",
            changed ? value => value.HasOperator(3, "=", "integer", "bigint", "pg_catalog") : null,
            Schema);
        modelBuilder.HasBlueTuskOperatorClass(
            "int_ops",
            "integer",
            "btree",
            value =>
            {
                value.IsInFamily("int_family", Schema)
                    .HasOperator(1, "<", "integer", "integer", "pg_catalog")
                    .HasOperator(2, "<=", "integer", "integer", "pg_catalog")
                    .HasOperator(3, changed ? "===" : "=", "integer", "integer",
                        changed ? Schema : "pg_catalog")
                    .HasOperator(4, ">=", "integer", "integer", "pg_catalog")
                    .HasOperator(5, ">", "integer", "integer", "pg_catalog")
                    .HasFunction(1, "btint4cmp", "integer", "integer", ["integer", "integer"], "pg_catalog");
            },
            Schema);
        modelBuilder.HasBlueTuskCast(
            "schema_program_tests.mood",
            "text",
            value =>
            {
                value.UsesInputOutput();
                if (!changed)
                {
                    value.IsAssignment();
                }
            });
        modelBuilder.HasBlueTuskAggregate(
            "product",
            "integer",
            value => value
                .UsesState("int4mul", "integer", "pg_catalog")
                .HasInitialCondition(changed ? "2" : "1")
                .IsParallelSafe(BlueTuskAggregateParallelSafety.Safe),
            Schema);
    }

    private static BlueTuskOperatorMemberDefinition OperatorMember(
        string name,
        string leftType,
        string rightType) => new(
        3,
        new BlueTuskOperatorName(name, "pg_catalog"),
        leftType,
        rightType);

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
        "DROP SCHEMA IF EXISTS schema_program_tests CASCADE");

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

    private sealed class SchemaProgramContext(DbContextOptions<SchemaProgramContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder, changed: false);
    }

    private sealed class ChangedSchemaProgramContext(DbContextOptions<ChangedSchemaProgramContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder, changed: true);
    }

    private sealed class NoSchemaProgramContext(DbContextOptions<NoSchemaProgramContext> options)
        : DbContext(options);
}

#pragma warning restore EF1001
