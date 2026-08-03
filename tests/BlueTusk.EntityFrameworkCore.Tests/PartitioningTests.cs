using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Partitioning;
using BlueTusk.EntityFrameworkCore.Partitioning.Internal;
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

public sealed class PartitioningTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Model_metadata_generates_range_default_and_nested_hash_partitions()
    {
        using var context = CreateContext<PartitionContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel());

        var createTable = Assert.Single(operations.OfType<CreateTableOperation>());
        var tableDefinition = BlueTuskPartitionMetadata.Deserialize(
            Assert.IsType<string>(createTable[BlueTuskPartitionMetadata.AnnotationName]));
        Assert.Equal(BlueTuskPartitionStrategy.Range, tableDefinition.Strategy);
        Assert.Equal("occurred_on", Assert.Single(tableDefinition.Keys).Expression);
        Assert.Empty(tableDefinition.Partitions);

        var partitions = operations.OfType<CreatePartitionOperation>().ToArray();
        Assert.Equal(5, partitions.Length);
        var year2026 = Assert.Single(partitions, operation => operation.Definition.Name == "events_2026");
        Assert.Equal("partition_tests", year2026.Definition.Schema);
        Assert.Equal(BlueTuskPartitionStrategy.Hash, year2026.Definition.Partitioning?.Strategy);
        Assert.Equal("tenant_id", Assert.Single(year2026.Definition.Partitioning!.Keys).Expression);

        var sql = string.Concat(
            context.GetService<IMigrationsSqlGenerator>()
                .Generate(operations, model)
                .Select(command => command.CommandText));
        Assert.Contains(
            "CREATE TABLE \"partition_tests\".\"partitioned_events\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("PARTITION BY RANGE (\"occurred_on\")", sql, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE \"partition_tests\".\"events_2026\" PARTITION OF \"partition_tests\".\"partitioned_events\" FOR VALUES FROM (DATE '2026-01-01') TO (DATE '2027-01-01') PARTITION BY HASH (\"tenant_id\")",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE \"partition_tests\".\"events_2026_0\" PARTITION OF \"partition_tests\".\"events_2026\" FOR VALUES WITH (MODULUS 2, REMAINDER 0)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE \"partition_tests\".\"events_default\" PARTITION OF \"partition_tests\".\"partitioned_events\" DEFAULT",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Model_differ_renames_and_replaces_partitions_but_rejects_strategy_changes()
    {
        using var sourceContext = CreateContext<SinglePartitionContext>(OfflineConnectionString);
        using var renamedContext = CreateContext<RenamedPartitionContext>(OfflineConnectionString);
        using var renamedRootContext = CreateContext<RenamedRootPartitionContext>(OfflineConnectionString);
        using var changedBoundContext = CreateContext<ChangedBoundPartitionContext>(OfflineConnectionString);
        using var changedStrategyContext = CreateContext<ListPartitionContext>(OfflineConnectionString);
        var differ = sourceContext.GetService<IMigrationsModelDiffer>();
        var source = sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var rename = Assert.Single(
            differ.GetDifferences(
                    source,
                    renamedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
                .OfType<AlterPartitionOperation>());
        Assert.Equal("events_2025", rename.Name);
        Assert.Equal("events_archive", rename.NewName);

        var rootRename = differ.GetDifferences(
            source,
            renamedRootContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Equal("renamed_events", Assert.Single(rootRename.OfType<RenameTableOperation>()).NewName);
        Assert.Empty(rootRename.OfType<CreatePartitionOperation>());
        Assert.Empty(rootRename.OfType<DropPartitionOperation>());

        var replacement = differ.GetDifferences(
            source,
            changedBoundContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.True(Assert.Single(replacement.OfType<DropPartitionOperation>()).IsDestructiveChange);
        Assert.Equal("events_2025", Assert.Single(
            replacement.OfType<CreatePartitionOperation>()).Definition.Name);

        var exception = Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
            source,
            changedStrategyContext.GetService<IDesignTimeModel>().Model.GetRelationalModel()));
        Assert.Contains("cannot change partition strategy or keys", exception.Message, StringComparison.Ordinal);
        Assert.Contains("data-preserving replacement migration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_attach_and_detach_operations_generate_quoted_transaction_aware_SQL()
    {
        using var context = CreateContext<SinglePartitionContext>(OfflineConnectionString);
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.AttachPartition(
            "events",
            "events_2027",
            BlueTuskPartitionBound.Range(
                BlueTuskPartitionValue.Literal(new DateOnly(2027, 1, 1)),
                BlueTuskPartitionValue.Literal(new DateOnly(2028, 1, 1))),
            "partition_tests",
            "archive");
        migration.DetachPartition(
            "events",
            "events_2027",
            BlueTuskPartitionDetachMode.Concurrently,
            "partition_tests",
            "archive");
        migration.DetachPartition(
            "events",
            "events_2027",
            BlueTuskPartitionDetachMode.Finalize,
            "partition_tests",
            "archive");

        var commands = context.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.Operations, context.GetService<IDesignTimeModel>().Model);
        Assert.Equal(3, commands.Count);
        Assert.Equal(
            "ALTER TABLE \"partition_tests\".\"events\" ATTACH PARTITION \"archive\".\"events_2027\" FOR VALUES FROM (DATE '2027-01-01') TO (DATE '2028-01-01');" + Environment.NewLine,
            commands[0].CommandText);
        Assert.False(commands[0].TransactionSuppressed);
        Assert.Equal(
            "ALTER TABLE \"partition_tests\".\"events\" DETACH PARTITION \"archive\".\"events_2027\" CONCURRENTLY;" + Environment.NewLine,
            commands[1].CommandText);
        Assert.True(commands[1].TransactionSuppressed);
        Assert.Equal(
            "ALTER TABLE \"partition_tests\".\"events\" DETACH PARTITION \"archive\".\"events_2027\" FINALIZE;" + Environment.NewLine,
            commands[2].CommandText);
    }

    [Fact]
    public void Partition_configuration_rejects_invalid_list_keys_and_uninitialized_values()
    {
        var modelBuilder = new ModelBuilder();
        ConfigureEntity(modelBuilder);

        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<PartitionEvent>()
            .HasPartitioning(
                BlueTuskPartitionStrategy.List,
                BlueTuskPartitionKeyDefinition.Column(nameof(PartitionEvent.TenantId)),
                BlueTuskPartitionKeyDefinition.Column(nameof(PartitionEvent.OccurredOn))));
        Assert.Throws<ArgumentException>(() => BlueTuskPartitionBound.Range(
            default,
            BlueTuskPartitionValue.Literal(10)));
        Assert.Throws<ArgumentException>(() => modelBuilder.Entity<PartitionEvent>()
            .HasPartitioning(
                BlueTuskPartitionStrategy.Range,
                BlueTuskPartitionKeyDefinition.Column(
                    nameof(PartitionEvent.TenantId),
                    collation: "public.")));
    }

    [Fact]
    public void List_hash_and_expression_partition_keys_generate_native_PostgreSQL_SQL()
    {
        using var listContext = CreateContext<ListPartitionContext>(OfflineConnectionString);
        using var hashContext = CreateContext<HashPartitionContext>(OfflineConnectionString);
        using var expressionContext = CreateContext<ExpressionPartitionContext>(OfflineConnectionString);

        Assert.Contains(
            "PARTITION BY LIST (\"occurred_on\")",
            GenerateCreateSql(listContext),
            StringComparison.Ordinal);
        Assert.Contains(
            "FOR VALUES IN (DATE '2025-01-01')",
            GenerateCreateSql(listContext),
            StringComparison.Ordinal);
        var hashSql = GenerateCreateSql(hashContext);
        Assert.Contains(
            "PARTITION BY HASH (\"tenant_id\", \"id\")",
            hashSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "FOR VALUES WITH (MODULUS 2, REMAINDER 1)",
            hashSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "PARTITION BY RANGE ((lower(payload)) COLLATE \"pg_catalog\".\"C\" \"pg_catalog\".\"text_ops\")",
            GenerateCreateSql(expressionContext),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Partition_only_schemas_are_ensured_before_child_creation()
    {
        using var context = CreateContext<CrossSchemaPartitionContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .ToArray();

        var ensureIndex = Array.FindIndex(
            operations,
            operation => operation is EnsureSchemaOperation { Name: "partition_archive" });
        var createIndex = Array.FindIndex(
            operations,
            operation => operation is CreatePartitionOperation
            {
                Definition.Name: "archived_events",
            });
        Assert.True(ensureIndex >= 0);
        Assert.True(createIndex > ensureIndex);
    }

    [Fact]
    public void Design_time_generator_scaffolds_all_partition_operations()
    {
        var definition = new BlueTuskPartitionDefinition(
            "events_2027",
            "partition_tests",
            BlueTuskPartitionBound.Range(
                BlueTuskPartitionValue.Literal(new DateOnly(2027, 1, 1)),
                BlueTuskPartitionValue.Literal(new DateOnly(2028, 1, 1))));
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
                new CreatePartitionOperation
                {
                    ParentName = "events",
                    ParentSchema = "partition_tests",
                    Definition = definition,
                },
                new AlterPartitionOperation
                {
                    Name = "events_2027",
                    Schema = "partition_tests",
                    NewName = "events_archive",
                    NewSchema = "archive",
                },
                new AttachPartitionOperation
                {
                    ParentName = "events",
                    ParentSchema = "partition_tests",
                    PartitionName = "events_2027",
                    PartitionSchema = "archive",
                    Bound = definition.Bound,
                },
                new DetachPartitionOperation
                {
                    ParentName = "events",
                    ParentSchema = "partition_tests",
                    PartitionName = "events_2027",
                    PartitionSchema = "archive",
                    Mode = BlueTuskPartitionDetachMode.Concurrently,
                },
                new DropPartitionOperation { Name = "events_archive", Schema = "archive" },
            ],
            builder);

        var code = builder.ToString();
        Assert.Contains("migrationBuilder.CreatePartition(\"events\"", code, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.AlterPartition(\"events_2027\"", code, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.AttachPartition(\"events\"", code, StringComparison.Ordinal);
        Assert.Contains("BlueTuskPartitionDetachMode.Concurrently", code, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.DropPartition(\"events_archive\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partition_tree_routes_rows_and_round_trips_through_reverse_engineering()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS partition_tests CASCADE");

        try
        {
            using var context = CreateContext<PartitionContext>(connectionString);
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
                INSERT INTO partition_tests.partitioned_events (id, tenant_id, occurred_on, payload)
                VALUES
                    (1, 10, DATE '2025-04-01', 'range'),
                    (2, 20, DATE '2026-04-01', 'nested hash'),
                    (3, 30, DATE '2028-04-01', 'default')
                """);
            Assert.EndsWith(
                "events_2025",
                Assert.IsType<string>(await ExecuteScalarAsync(
                    connectionString,
                    "SELECT tableoid::regclass::text FROM partition_tests.partitioned_events WHERE id = 1")),
                StringComparison.Ordinal);
            Assert.Contains(
                "events_2026_",
                Assert.IsType<string>(await ExecuteScalarAsync(
                    connectionString,
                    "SELECT tableoid::regclass::text FROM partition_tests.partitioned_events WHERE id = 2")),
                StringComparison.Ordinal);
            Assert.EndsWith(
                "events_default",
                Assert.IsType<string>(await ExecuteScalarAsync(
                    connectionString,
                    "SELECT tableoid::regclass::text FROM partition_tests.partitioned_events WHERE id = 3")),
                StringComparison.Ordinal);

            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["partition_tests"]));
            var root = Assert.Single(databaseModel.Tables);
            Assert.Equal("partitioned_events", root.Name);
            var discovered = BlueTuskPartitionMetadata.Deserialize(
                Assert.IsType<string>(root[BlueTuskPartitionMetadata.AnnotationName]));
            Assert.Equal(BlueTuskPartitionStrategy.Range, discovered.Strategy);
            Assert.Equal("occurred_on", discovered.KeySql);
            Assert.Equal(3, discovered.Partitions.Count);
            var discovered2026 = Assert.Single(
                discovered.Partitions,
                partition => partition.Name == "events_2026");
            Assert.Equal(BlueTuskPartitionStrategy.Hash, discovered2026.Partitioning?.Strategy);
            Assert.Equal(2, discovered2026.Partitioning?.Partitions.Count);
            Assert.All(
                discovered.Partitions,
                partition => Assert.Equal(BlueTuskPartitionBoundKind.Sql, partition.Bound.Kind));

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var serviceProvider = services.BuildServiceProvider())
            {
                var scaffolded = serviceProvider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], ["partition_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "PartitionContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "PartitionModels",
                        ModelNamespace = "PartitionModels",
                        RootNamespace = "PartitionModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains(
                    "HasPartitioning(",
                    scaffolded.ContextFile.Code,
                    StringComparison.Ordinal);
                Assert.Single(scaffolded.AdditionalFiles);
            }

            await ExecuteNonQueryAsync(
                connectionString,
                "CREATE TABLE partition_tests.events_2027 (LIKE partition_tests.partitioned_events INCLUDING ALL)");
            var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            migration.AttachPartition(
                "partitioned_events",
                "events_2027",
                BlueTuskPartitionBound.Range(
                    BlueTuskPartitionValue.Literal(new DateOnly(2027, 1, 1)),
                    BlueTuskPartitionValue.Literal(new DateOnly(2028, 1, 1))),
                "partition_tests",
                "partition_tests");
            migration.DetachPartition(
                "partitioned_events",
                "events_2027",
                BlueTuskPartitionDetachMode.Normal,
                "partition_tests",
                "partition_tests");
            var lifecycleCommands = context.GetService<IMigrationsSqlGenerator>()
                .Generate(migration.Operations, model);
            await ExecuteNonQueryAsync(connectionString, lifecycleCommands[0].CommandText);
            await ExecuteNonQueryAsync(
                connectionString,
                "INSERT INTO partition_tests.partitioned_events VALUES (4, 40, DATE '2027-04-01', 'attached')");
            Assert.EndsWith(
                "events_2027",
                Assert.IsType<string>(await ExecuteScalarAsync(
                    connectionString,
                    "SELECT tableoid::regclass::text FROM partition_tests.partitioned_events WHERE id = 4")),
                StringComparison.Ordinal);
            await ExecuteNonQueryAsync(connectionString, lifecycleCommands[1].CommandText);
            Assert.Equal(
                true,
                await ExecuteScalarAsync(
                    connectionString,
                    "SELECT to_regclass('partition_tests.events_2027') IS NOT NULL"));
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS partition_tests CASCADE");
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

    private static string GenerateCreateSql(DbContext context)
    {
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel());
        return string.Concat(
            context.GetService<IMigrationsSqlGenerator>()
                .Generate(operations, model)
                .Select(command => command.CommandText));
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

    private static void ConfigureEntity(
        ModelBuilder modelBuilder,
        string tableName = "partitioned_events")
    {
        var entity = modelBuilder.Entity<PartitionEvent>();
        entity.ToTable(tableName, "partition_tests");
        entity.HasNoKey();
        entity.Property(item => item.Id).HasColumnName("id");
        entity.Property(item => item.TenantId).HasColumnName("tenant_id");
        entity.Property(item => item.OccurredOn).HasColumnName("occurred_on");
        entity.Property(item => item.Payload).HasColumnName("payload");
    }

    private static BlueTuskPartitioningBuilder ConfigureRange(
        ModelBuilder modelBuilder,
        string tableName = "partitioned_events")
    {
        ConfigureEntity(modelBuilder, tableName);
        return modelBuilder.Entity<PartitionEvent>()
            .HasRangePartitioning(item => item.OccurredOn);
    }

    private sealed class PartitionContext(DbContextOptions<PartitionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureRange(modelBuilder)
                .HasRangePartition(
                    "events_2025",
                    BlueTuskPartitionValue.Literal(new DateOnly(2025, 1, 1)),
                    BlueTuskPartitionValue.Literal(new DateOnly(2026, 1, 1)))
                .HasRangePartition(
                    "events_2026",
                    BlueTuskPartitionValue.Literal(new DateOnly(2026, 1, 1)),
                    BlueTuskPartitionValue.Literal(new DateOnly(2027, 1, 1)))
                .HasDefaultPartition("events_default")
                .HasSubpartitioning(
                    "events_2026",
                    BlueTuskPartitionStrategy.Hash,
                    [BlueTuskPartitionKeyDefinition.Column(nameof(PartitionEvent.TenantId))],
                    child => child
                        .HasHashPartition("events_2026_0", 2, 0)
                        .HasHashPartition("events_2026_1", 2, 1));
        }
    }

    private sealed class SinglePartitionContext(DbContextOptions<SinglePartitionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureRange(modelBuilder).HasRangePartition(
                "events_2025",
                BlueTuskPartitionValue.Literal(new DateOnly(2025, 1, 1)),
                BlueTuskPartitionValue.Literal(new DateOnly(2026, 1, 1)));
    }

    private sealed class RenamedPartitionContext(DbContextOptions<RenamedPartitionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureRange(modelBuilder).HasRangePartition(
                "events_archive",
                BlueTuskPartitionValue.Literal(new DateOnly(2025, 1, 1)),
                BlueTuskPartitionValue.Literal(new DateOnly(2026, 1, 1)));
    }

    private sealed class RenamedRootPartitionContext(DbContextOptions<RenamedRootPartitionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureRange(modelBuilder, "renamed_events").HasRangePartition(
                "events_2025",
                BlueTuskPartitionValue.Literal(new DateOnly(2025, 1, 1)),
                BlueTuskPartitionValue.Literal(new DateOnly(2026, 1, 1)));
    }

    private sealed class ChangedBoundPartitionContext(DbContextOptions<ChangedBoundPartitionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureRange(modelBuilder).HasRangePartition(
                "events_2025",
                BlueTuskPartitionValue.Literal(new DateOnly(2024, 1, 1)),
                BlueTuskPartitionValue.Literal(new DateOnly(2026, 1, 1)));
    }

    private sealed class ListPartitionContext(DbContextOptions<ListPartitionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntity(modelBuilder);
            modelBuilder.Entity<PartitionEvent>()
                .HasListPartitioning(item => item.OccurredOn)
                .HasListPartition(
                    "events_2025",
                    [BlueTuskPartitionValue.Literal(new DateOnly(2025, 1, 1))]);
        }
    }

    private sealed class HashPartitionContext(DbContextOptions<HashPartitionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntity(modelBuilder);
            modelBuilder.Entity<PartitionEvent>()
                .HasHashPartitioning(item => new { item.TenantId, item.Id })
                .HasHashPartition("events_hash_0", 2, 0)
                .HasHashPartition("events_hash_1", 2, 1);
        }
    }

    private sealed class ExpressionPartitionContext(DbContextOptions<ExpressionPartitionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntity(modelBuilder);
            modelBuilder.Entity<PartitionEvent>().HasPartitioning(
                BlueTuskPartitionStrategy.Range,
                BlueTuskPartitionKeyDefinition.SqlExpression(
                    "lower(payload)",
                    "pg_catalog.C",
                    "pg_catalog.text_ops"));
        }
    }

    private sealed class CrossSchemaPartitionContext(DbContextOptions<CrossSchemaPartitionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureRange(modelBuilder).HasRangePartition(
                "archived_events",
                BlueTuskPartitionValue.Literal(new DateOnly(2020, 1, 1)),
                BlueTuskPartitionValue.Literal(new DateOnly(2021, 1, 1)),
                "partition_archive");
    }

    private sealed class PartitionEvent
    {
        public int Id { get; set; }

        public int TenantId { get; set; }

        public DateOnly OccurredOn { get; set; }

        public string? Payload { get; set; }
    }
}

#pragma warning restore EF1001
