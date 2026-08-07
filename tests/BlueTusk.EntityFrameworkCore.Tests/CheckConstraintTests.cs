using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.CheckConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Design.Internal;
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

#pragma warning disable EF1001 // Tests intentionally exercise provider and design-time infrastructure.

public sealed class CheckConstraintTests
{
    private const string Schema = "check_constraint_tests";
    private const string Table = "measurements";
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Model_metadata_generates_inline_and_deferred_PostgreSQL_CHECK_constraints()
    {
        using var context = CreateContext<ConfiguredContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel());
        var create = Assert.Single(operations.OfType<CreateTableOperation>());
        Assert.Collection(
            create.CheckConstraints.OrderBy(constraint => constraint.Name, StringComparer.Ordinal),
            constraint =>
            {
                Assert.Equal("measurements_bounded", constraint.Name);
                Assert.False(BlueTuskCheckConstraintMetadata.IsNotValid(constraint));
                Assert.True(BlueTuskCheckConstraintMetadata.HasNoInherit(constraint));
            },
            constraint =>
            {
                Assert.Equal("measurements_positive", constraint.Name);
                Assert.True(BlueTuskCheckConstraintMetadata.IsNotValid(constraint));
                Assert.False(BlueTuskCheckConstraintMetadata.HasNoInherit(constraint));
            });

        var sql = string.Concat(context.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, model)
            .Select(command => command.CommandText));
        Assert.Contains(
            "CONSTRAINT \"measurements_bounded\" CHECK (\"value\" < 100) NO INHERIT",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CONSTRAINT \"measurements_positive\" CHECK (\"value\" > 0) NOT VALID\n)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE \"check_constraint_tests\".\"measurements\" ADD CONSTRAINT " +
            "\"measurements_positive\" CHECK (\"value\" > 0) NOT VALID;",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_operations_generate_safe_SQL_and_CSharp()
    {
        using var context = CreateContext<EmptyContext>(OfflineConnectionString);
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.AddCheckConstraint(
            "measurements_positive",
            Table,
            "\"value\" > 0",
            Schema,
            notValid: true,
            noInherit: true);
        migration.ValidateCheckConstraint("measurements_positive", Table, Schema);

        var commands = context.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.Operations, context.GetService<IDesignTimeModel>().Model);
        Assert.Equal(2, commands.Count);
        Assert.Equal(
            "ALTER TABLE \"check_constraint_tests\".\"measurements\" ADD CONSTRAINT " +
            "\"measurements_positive\" CHECK (\"value\" > 0) NO INHERIT NOT VALID;" +
            Environment.NewLine,
            commands[0].CommandText);
        Assert.Equal(
            "ALTER TABLE \"check_constraint_tests\".\"measurements\" VALIDATE CONSTRAINT " +
            "\"measurements_positive\";" + Environment.NewLine,
            commands[1].CommandText);

        using var provider = DesignServices();
        var builder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, builder);
        var code = builder.ToString();
        Assert.Contains("AddCheckConstraint(", code, StringComparison.Ordinal);
        Assert.Contains(BlueTuskCheckConstraintMetadata.NotValidAnnotationName, code, StringComparison.Ordinal);
        Assert.Contains(BlueTuskCheckConstraintMetadata.NoInheritAnnotationName, code, StringComparison.Ordinal);
        Assert.Contains("ValidateCheckConstraint(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Differ_validates_an_unchanged_NOT_VALID_constraint_without_recreating_it()
    {
        using var sourceContext = CreateContext<ConfiguredContext>(OfflineConnectionString);
        using var targetContext = CreateContext<ValidatedContext>(OfflineConnectionString);
        var targetModel = targetContext.GetService<IDesignTimeModel>().Model;
        var operations = sourceContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            targetModel.GetRelationalModel());

        var validate = Assert.Single(operations.OfType<ValidateCheckConstraintOperation>());
        Assert.Equal("measurements_positive", validate.Name);
        Assert.Empty(operations.OfType<AddCheckConstraintOperation>());
        Assert.Empty(operations.OfType<DropCheckConstraintOperation>());
        Assert.Equal(
            "ALTER TABLE \"check_constraint_tests\".\"measurements\" VALIDATE CONSTRAINT " +
            "\"measurements_positive\";" + Environment.NewLine,
            Assert.Single(targetContext.GetService<IMigrationsSqlGenerator>()
                .Generate(operations, targetModel)).CommandText);
    }

    [Fact]
    public void Differ_recreates_a_constraint_when_changing_to_NOT_VALID()
    {
        using var sourceContext = CreateContext<ValidatedContext>(OfflineConnectionString);
        using var targetContext = CreateContext<ConfiguredContext>(OfflineConnectionString);
        var operations = sourceContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            targetContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        var drop = Assert.Single(operations.OfType<DropCheckConstraintOperation>());
        Assert.Equal("measurements_positive", drop.Name);
        Assert.True(drop.IsDestructiveChange);
        var add = Assert.Single(operations.OfType<AddCheckConstraintOperation>());
        Assert.Equal("measurements_positive", add.Name);
        Assert.True(BlueTuskCheckConstraintMetadata.IsNotValid(add));
        Assert.Empty(operations.OfType<ValidateCheckConstraintOperation>());
    }

    [Fact]
    public void NOT_ENFORCED_is_version_guarded_and_enforceability_changes_recreate_the_constraint()
    {
        using var sourceContext = CreateContext<NotEnforcedContext>(OfflineConnectionString);
        using var targetContext = CreateContext<EnforcedContext>(OfflineConnectionString);
        var sourceModel = sourceContext.GetService<IDesignTimeModel>().Model;
        var createOperations = sourceContext.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, sourceModel.GetRelationalModel());
        var createSql = string.Concat(sourceContext.GetService<IMigrationsSqlGenerator>()
            .Generate(createOperations, sourceModel)
            .Select(command => command.CommandText));
        Assert.Contains(
            "BlueTusk NOT ENFORCED CHECK constraints require PostgreSQL 18 or later.",
            createSql,
            StringComparison.Ordinal);
        Assert.Contains("CHECK (\"value\" < 50) NOT ENFORCED", createSql, StringComparison.Ordinal);

        var changes = sourceContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            sourceModel.GetRelationalModel(),
            targetContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Single(changes.OfType<DropCheckConstraintOperation>());
        Assert.Single(changes.OfType<AddCheckConstraintOperation>());
        Assert.Empty(changes.OfType<ValidateCheckConstraintOperation>());
    }

    [Fact]
    public async Task CHECK_constraints_enforce_validate_reverse_engineer_and_scaffold_across_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await CleanupAsync(connectionString);

        try
        {
            using (var generatedContext = CreateContext<ConfiguredContext>(connectionString))
            {
                var generatedModel = generatedContext.GetService<IDesignTimeModel>().Model;
                var generatedOperations = generatedContext.GetService<IMigrationsModelDiffer>()
                    .GetDifferences(null, generatedModel.GetRelationalModel());
                await ExecuteOperationsAsync(generatedContext, generatedOperations, connectionString);
                Assert.Equal(false, await ExecuteScalarAsync(
                    connectionString,
                    "SELECT convalidated FROM pg_constraint WHERE conname = 'measurements_positive'"));
                Assert.Equal(true, await ExecuteScalarAsync(
                    connectionString,
                    "SELECT connoinherit FROM pg_constraint WHERE conname = 'measurements_bounded'"));
            }

            await CleanupAsync(connectionString);
            await ExecuteNonQueryAsync(
                connectionString,
                $"""
                CREATE SCHEMA {Schema};
                CREATE TABLE {Schema}.{Table} (
                    id integer PRIMARY KEY,
                    value integer NOT NULL,
                    CONSTRAINT measurements_bounded CHECK (value < 100) NO INHERIT);
                INSERT INTO {Schema}.{Table} (id, value) VALUES (1, -1);
                """);

            using var context = CreateContext<EmptyContext>(connectionString);
            var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            migration.AddCheckConstraint(
                "measurements_positive",
                Table,
                "value > 0",
                Schema,
                notValid: true);
            await ExecuteOperationsAsync(context, migration.Operations, connectionString);

            var serverVersion = Convert.ToInt32(await ExecuteScalarAsync(
                connectionString,
                "SELECT current_setting('server_version_num')"),
                System.Globalization.CultureInfo.InvariantCulture);
            var notEnforced = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            notEnforced.AddCheckConstraint(
                "measurements_legacy_limit",
                Table,
                "value < 50",
                Schema,
                notValid: true,
                notEnforced: true);
            if (serverVersion >= 180000)
            {
                await ExecuteOperationsAsync(context, notEnforced.Operations, connectionString);
                await ExecuteNonQueryAsync(
                    connectionString,
                    $"INSERT INTO {Schema}.{Table} (id, value) VALUES (3, 75)");
            }
            else
            {
                var unsupported = await Assert.ThrowsAsync<BlueTuskException>(() =>
                    ExecuteOperationsAsync(context, notEnforced.Operations, connectionString));
                Assert.Contains("require PostgreSQL 18 or later", unsupported.Message, StringComparison.Ordinal);
            }

            Assert.Equal(false, await ExecuteScalarAsync(
                connectionString,
                "SELECT convalidated FROM pg_constraint WHERE conname = 'measurements_positive'"));
            var rejected = await Assert.ThrowsAsync<BlueTuskException>(() => ExecuteNonQueryAsync(
                connectionString,
                $"INSERT INTO {Schema}.{Table} (id, value) VALUES (2, -2)"));
            Assert.Contains("measurements_positive", rejected.Message, StringComparison.Ordinal);

            await ExecuteNonQueryAsync(
                connectionString,
                $"""
                CREATE TABLE {Schema}.check_parent (
                    value integer CONSTRAINT check_parent_positive CHECK (value > 0));
                CREATE TABLE {Schema}.check_child () INHERITS ({Schema}.check_parent);
                """);

            var database = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], [Schema]));
            var table = Assert.Single(database.Tables, item => item.Schema == Schema && item.Name == Table);
            var inheritedChild = Assert.Single(
                database.Tables,
                item => item.Schema == Schema && item.Name == "check_child");
            Assert.Null(inheritedChild[BlueTuskCheckConstraintMetadata.ScaffoldAnnotationName]);
            var definitions = BlueTuskCheckConstraintMetadata.Deserialize(Assert.IsType<string>(
                table[BlueTuskCheckConstraintMetadata.ScaffoldAnnotationName]));
            var bounded = Assert.Single(definitions, constraint => constraint.Name == "measurements_bounded");
            Assert.False(bounded.IsNotValid);
            Assert.True(bounded.NoInherit);
            Assert.False(bounded.IsNotEnforced);
            Assert.Contains("value < 100", bounded.Sql, StringComparison.Ordinal);
            var positive = Assert.Single(definitions, constraint => constraint.Name == "measurements_positive");
            Assert.True(positive.IsNotValid);
            Assert.False(positive.NoInherit);
            Assert.False(positive.IsNotEnforced);
            Assert.Contains("value > 0", positive.Sql, StringComparison.Ordinal);
            if (serverVersion >= 180000)
            {
                var legacy = Assert.Single(
                    definitions,
                    constraint => constraint.Name == "measurements_legacy_limit");
                Assert.True(legacy.IsNotValid);
                Assert.True(legacy.IsNotEnforced);
            }
            else
            {
                Assert.DoesNotContain(definitions, constraint => constraint.IsNotEnforced);
            }

            await using (var provider = DesignServices())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], [Schema]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "CheckConstraintContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "CheckConstraintModels",
                        ModelNamespace = "CheckConstraintModels",
                        RootNamespace = "CheckConstraintModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasCheckConstraints(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
                Assert.Contains("measurements_positive", scaffolded.ContextFile.Code, StringComparison.Ordinal);
                Assert.Equal(
                    serverVersion >= 180000,
                    scaffolded.ContextFile.Code.Contains("measurements_legacy_limit", StringComparison.Ordinal));
            }

            var validate = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            validate.ValidateCheckConstraint("measurements_positive", Table, Schema);
            var failedValidation = await Assert.ThrowsAsync<BlueTuskException>(() =>
                ExecuteOperationsAsync(context, validate.Operations, connectionString));
            Assert.Contains("measurements_positive", failedValidation.Message, StringComparison.Ordinal);

            await ExecuteNonQueryAsync(
                connectionString,
                $"UPDATE {Schema}.{Table} SET value = 1 WHERE id = 1");
            await ExecuteOperationsAsync(context, validate.Operations, connectionString);
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT convalidated FROM pg_constraint WHERE conname = 'measurements_positive'"));
        }
        finally
        {
            await CleanupAsync(connectionString);
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

    private static ServiceProvider DesignServices()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddEntityFrameworkBlueTusk();
        new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
        return services.BuildServiceProvider();
    }

    private static async Task ExecuteOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        string connectionString)
    {
        foreach (var command in context.GetService<IMigrationsSqlGenerator>()
                     .Generate(operations, context.GetService<IDesignTimeModel>().Model))
        {
            await ExecuteNonQueryAsync(connectionString, command.CommandText);
        }
    }

    private static async Task CleanupAsync(string connectionString) =>
        await ExecuteNonQueryAsync(connectionString, $"DROP SCHEMA IF EXISTS {Schema} CASCADE");

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

    private static void Configure(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Measurement>();
        entity.ToTable(Table, Schema);
        entity.HasKey(measurement => measurement.Id);
        entity.Property(measurement => measurement.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(measurement => measurement.Value).HasColumnName("value");
        entity.ToTable(Table, Schema, table =>
        {
            table.HasCheckConstraint("measurements_bounded", "\"value\" < 100")
                .IsNoInherit();
            table.HasCheckConstraint("measurements_positive", "\"value\" > 0")
                .IsNotValid();
        });
    }

    private static void ConfigureValidated(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Measurement>();
        entity.ToTable(Table, Schema);
        entity.HasKey(measurement => measurement.Id);
        entity.Property(measurement => measurement.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(measurement => measurement.Value).HasColumnName("value");
        entity.ToTable(Table, Schema, table =>
        {
            table.HasCheckConstraint("measurements_bounded", "\"value\" < 100")
                .IsNoInherit();
            table.HasCheckConstraint("measurements_positive", "\"value\" > 0");
        });
    }

    private static void ConfigureEnforcement(ModelBuilder modelBuilder, bool notEnforced)
    {
        var entity = modelBuilder.Entity<Measurement>();
        entity.ToTable(Table, Schema);
        entity.HasKey(measurement => measurement.Id);
        entity.Property(measurement => measurement.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(measurement => measurement.Value).HasColumnName("value");
        entity.ToTable(Table, Schema, table =>
        {
            var constraint = table.HasCheckConstraint("measurements_legacy_limit", "\"value\" < 50");
            if (notEnforced)
            {
                constraint.IsNotValid().IsNotEnforced();
            }
        });
    }

    private sealed class ConfiguredContext(DbContextOptions<ConfiguredContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder);
    }

    private sealed class ValidatedContext(DbContextOptions<ValidatedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureValidated(modelBuilder);
    }

    private sealed class NotEnforcedContext(DbContextOptions<NotEnforcedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureEnforcement(modelBuilder, notEnforced: true);
    }

    private sealed class EnforcedContext(DbContextOptions<EnforcedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureEnforcement(modelBuilder, notEnforced: false);
    }

    private sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options);

    private sealed class Measurement
    {
        public int Id { get; set; }

        public int Value { get; set; }
    }
}

#pragma warning restore EF1001
