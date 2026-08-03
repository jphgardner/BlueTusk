using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints;
using BlueTusk.EntityFrameworkCore.ExclusionConstraints.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Partitioning;
using BlueTusk.TypeSystem;
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

public sealed class ExclusionConstraintTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Metadata_generates_complete_exclusion_SQL_after_its_table()
    {
        using var context = CreateContext<CompleteExclusionContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .ToArray();

        var createTableIndex = Array.FindIndex(
            operations,
            operation => operation is CreateTableOperation { Name: "reservations" });
        var addIndex = Array.FindIndex(
            operations,
            operation => operation is AddExclusionConstraintOperation);
        Assert.True(createTableIndex >= 0 && createTableIndex < addIndex);

        var add = Assert.Single(operations.OfType<AddExclusionConstraintOperation>());
        Assert.Equal("exclusion_tests", add.Schema);
        Assert.Equal("reservations_no_overlap", add.Definition.Name);
        Assert.Collection(
            add.Definition.Elements,
            element =>
            {
                Assert.Equal("during", element.Expression);
                Assert.True(element.IsColumn);
                Assert.Equal("&&", element.Operator);
                Assert.Equal("pg_catalog", element.OperatorSchema);
            },
            element =>
            {
                Assert.Equal("lower(note)", element.Expression);
                Assert.False(element.IsColumn);
                Assert.Equal("=", element.Operator);
            });
        Assert.Equal(["note"], add.Definition.IncludedColumns);

        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate([add], model)).CommandText;
        Assert.Equal(
            "ALTER TABLE \"exclusion_tests\".\"reservations\" ADD CONSTRAINT \"reservations_no_overlap\" " +
            "EXCLUDE USING \"gist\" (\"during\" COLLATE \"pg_catalog\".\"C\" \"pg_catalog\".\"range_ops\" " +
            "(buffering = auto) DESC NULLS LAST WITH OPERATOR(\"pg_catalog\".&&), " +
            "(lower(note)) WITH =) INCLUDE (\"note\") WITH (fillfactor = 80) " +
            "USING INDEX TABLESPACE \"fast_space\" WHERE (active) DEFERRABLE INITIALLY DEFERRED;" +
            Environment.NewLine,
            sql);
    }

    [Fact]
    public void Differ_drops_before_relational_changes_and_renames_equal_definitions()
    {
        using var sourceContext = CreateContext<LiveExclusionContext>(OfflineConnectionString);
        using var removedContext = CreateContext<ReducedReservationContext>(OfflineConnectionString);
        using var renamedContext = CreateContext<RenamedExclusionContext>(OfflineConnectionString);
        using var movedContext = CreateContext<MovedAndRenamedExclusionContext>(OfflineConnectionString);
        var differ = sourceContext.GetService<IMigrationsModelDiffer>();
        var source = sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var removals = differ.GetDifferences(
                source,
                removedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .ToArray();
        var dropIndex = Array.FindIndex(
            removals,
            operation => operation is DropExclusionConstraintOperation);
        var dropColumnIndex = Array.FindIndex(
            removals,
            operation => operation is DropColumnOperation { Name: "note" });
        Assert.True(dropIndex >= 0 && dropIndex < dropColumnIndex);
        Assert.True(Assert.Single(removals.OfType<DropExclusionConstraintOperation>())
            .IsDestructiveChange);

        var rename = Assert.Single(differ.GetDifferences(
                source,
                renamedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .OfType<RenameExclusionConstraintOperation>());
        Assert.Equal("reservations_no_overlap", rename.Name);
        Assert.Equal("reservations_no_collision", rename.NewName);

        var movedOperations = differ.GetDifferences(
                source,
                movedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .ToArray();
        Assert.Equal("bookings", Assert.Single(movedOperations.OfType<RenameTableOperation>()).NewName);
        var movedRename = Assert.Single(movedOperations.OfType<RenameExclusionConstraintOperation>());
        Assert.Equal("bookings", movedRename.Table);
        Assert.Equal("reservations_no_collision", movedRename.NewName);
        Assert.Empty(movedOperations.OfType<AddExclusionConstraintOperation>());
        Assert.Empty(movedOperations.OfType<DropExclusionConstraintOperation>());
    }

    [Fact]
    public void Definition_changes_drop_and_recreate_the_constraint()
    {
        using var sourceContext = CreateContext<LiveExclusionContext>(OfflineConnectionString);
        using var targetContext = CreateContext<ChangedExclusionContext>(OfflineConnectionString);
        var operations = sourceContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            sourceContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            targetContext.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.Single(operations.OfType<DropExclusionConstraintOperation>());
        var add = Assert.Single(operations.OfType<AddExclusionConstraintOperation>());
        Assert.Equal("NOT active", add.Definition.PredicateSql);
    }

    [Fact]
    public void Invalid_metadata_and_partitioned_roots_are_rejected()
    {
        var noElements = new ModelBuilder();
        ConfigureEntity(noElements, includeNote: true);
        Assert.Throws<ArgumentException>(() => noElements.Entity<Reservation>()
            .HasExclusionConstraint("empty", _ => { }));

        var unsafeOperator = new ModelBuilder();
        ConfigureEntity(unsafeOperator, includeNote: true);
        Assert.Throws<ArgumentException>(() => unsafeOperator.Entity<Reservation>()
            .HasExclusionConstraint(
                "unsafe",
                constraint => constraint.HasProperty(item => item.During, "&&; DROP TABLE reservations")));

        using var partitionedContext = CreateContext<PartitionedReservationContext>(OfflineConnectionString);
        var model = partitionedContext.GetService<IDesignTimeModel>().Model;
        var exception = Assert.Throws<InvalidOperationException>(() =>
            partitionedContext.GetService<IMigrationsModelDiffer>()
                .GetDifferences(null, model.GetRelationalModel()));
        Assert.Contains("does not support exclusion constraints on partitioned table", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_operations_generate_safe_SQL_and_CSharp()
    {
        using var context = CreateContext<LiveExclusionContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var definition = Assert.Single(
            model.FindEntityType(typeof(Reservation))!.GetExclusionConstraints());
        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.AddExclusionConstraint("reservations", definition, "exclusion_tests");
        migration.RenameExclusionConstraint(
            "reservations",
            "reservations_no_overlap",
            "reservations_no_collision",
            "exclusion_tests");
        migration.DropExclusionConstraint(
            "reservations",
            "reservations_no_collision",
            "exclusion_tests");

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.Operations, model);
        Assert.Equal(3, commands.Count);
        Assert.Equal(
            "ALTER TABLE \"exclusion_tests\".\"reservations\" RENAME CONSTRAINT " +
            "\"reservations_no_overlap\" TO \"reservations_no_collision\";" + Environment.NewLine,
            commands[1].CommandText);
        Assert.Equal(
            "ALTER TABLE \"exclusion_tests\".\"reservations\" DROP CONSTRAINT " +
            "\"reservations_no_collision\" RESTRICT;" + Environment.NewLine,
            commands[2].CommandText);

        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddEntityFrameworkBlueTusk();
        new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = services.BuildServiceProvider();
        var builder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, builder);
        var code = builder.ToString();
        Assert.Contains("AddExclusionConstraint", code, StringComparison.Ordinal);
        Assert.Contains("RenameExclusionConstraint", code, StringComparison.Ordinal);
        Assert.Contains("DropExclusionConstraint", code, StringComparison.Ordinal);
        Assert.Contains("reservations_no_collision", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exclusion_constraint_enforces_round_trips_scaffolds_renames_and_drops_across_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await CleanupAsync(connectionString);

        try
        {
            using var initialContext = CreateContext<LiveExclusionContext>(connectionString);
            var initialModel = initialContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(initialContext, null, initialModel, connectionString);

            await ExecuteNonQueryAsync(
                connectionString,
                "INSERT INTO exclusion_tests.reservations (id, during, active, note) " +
                "VALUES (1, int4range(1, 5, '[)'), true, 'first')");
            var conflict = await Assert.ThrowsAsync<BlueTuskException>(() => ExecuteNonQueryAsync(
                connectionString,
                "INSERT INTO exclusion_tests.reservations (id, during, active, note) " +
                "VALUES (2, int4range(4, 8, '[)'), true, 'conflict')"));
            Assert.Contains("reservations_no_overlap", conflict.Message, StringComparison.Ordinal);
            await ExecuteNonQueryAsync(
                connectionString,
                "INSERT INTO exclusion_tests.reservations (id, during, active, note) " +
                "VALUES (3, int4range(4, 8, '[)'), false, 'inactive')");

            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["exclusion_tests"]));
            var table = Assert.Single(
                databaseModel.Tables,
                item => item.Schema == "exclusion_tests" && item.Name == "reservations");
            var discovered = Assert.Single(BlueTuskExclusionConstraintMetadata.Deserialize(
                Assert.IsType<string>(table[BlueTuskExclusionConstraintMetadata.AnnotationName])));
            Assert.Equal("gist", discovered.IndexMethod);
            Assert.Equal("reservations_no_overlap", discovered.Name);
            var element = Assert.Single(discovered.Elements);
            Assert.True(element.IsPreformatted);
            Assert.Contains("during", element.Expression, StringComparison.Ordinal);
            Assert.Equal("&&", element.Operator);
            Assert.Equal("pg_catalog", element.OperatorSchema);
            Assert.Equal(["note"], discovered.IncludedColumns);
            Assert.Contains(
                discovered.StorageParameters,
                parameter => parameter.Name == "fillfactor" && parameter.Value == "80");
            Assert.Equal("active", discovered.PredicateSql);
            Assert.True(discovered.IsDeferrable);
            Assert.True(discovered.IsInitiallyDeferred);

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var provider = services.BuildServiceProvider())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], ["exclusion_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "ExclusionContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "ExclusionModels",
                        ModelNamespace = "ExclusionModels",
                        RootNamespace = "ExclusionModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains(
                    "HasExclusionConstraints(",
                    scaffolded.ContextFile.Code,
                    StringComparison.Ordinal);
            }

            using var renamedContext = CreateContext<RenamedExclusionContext>(connectionString);
            var renamedModel = renamedContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(renamedContext, initialModel, renamedModel, connectionString);
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT EXISTS (SELECT 1 FROM pg_constraint " +
                "WHERE conname = 'reservations_no_collision' AND contype = 'x')"));

            using var noConstraintContext = CreateContext<NoExclusionContext>(connectionString);
            var noConstraintModel = noConstraintContext.GetService<IDesignTimeModel>().Model;
            await ApplyAsync(noConstraintContext, renamedModel, noConstraintModel, connectionString);
            Assert.Equal(false, await ExecuteScalarAsync(
                connectionString,
                "SELECT EXISTS (SELECT 1 FROM pg_constraint " +
                "WHERE conname = 'reservations_no_collision' AND contype = 'x')"));
            await ExecuteNonQueryAsync(
                connectionString,
                "INSERT INTO exclusion_tests.reservations (id, during, active, note) " +
                "VALUES (4, int4range(4, 8, '[)'), true, 'allowed after drop')");
        }
        finally
        {
            await CleanupAsync(connectionString);
        }
    }

    private static void ConfigureEntity(
        ModelBuilder modelBuilder,
        bool includeNote,
        string tableName = "reservations")
    {
        var entity = modelBuilder.Entity<Reservation>();
        entity.ToTable(tableName, "exclusion_tests");
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.During).HasColumnName("during").HasColumnType("int4range");
        entity.Property(item => item.Active).HasColumnName("active");
        if (includeNote)
        {
            entity.Property(item => item.Note).HasColumnName("note");
        }
        else
        {
            entity.Ignore(item => item.Note);
        }
    }

    private static void ConfigureLiveConstraint(
        ModelBuilder modelBuilder,
        string name,
        string predicate = "active",
        string tableName = "reservations")
    {
        ConfigureEntity(modelBuilder, includeNote: true, tableName);
        modelBuilder.Entity<Reservation>().HasExclusionConstraint(
            name,
            constraint => constraint
                .HasProperty(item => item.During, "&&", operatorSchema: "pg_catalog")
                .IncludeProperties(nameof(Reservation.Note))
                .HasStorageParameter("fillfactor", "80")
                .HasFilter(predicate)
                .IsDeferrable(initiallyDeferred: true));
    }

    private static TContext CreateContext<TContext>(string connectionString)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
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

    private static async Task CleanupAsync(string connectionString) => await ExecuteNonQueryAsync(
        connectionString,
        "DROP SCHEMA IF EXISTS exclusion_tests CASCADE");

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

    private sealed class CompleteExclusionContext(DbContextOptions<CompleteExclusionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntity(modelBuilder, includeNote: true);
            modelBuilder.Entity<Reservation>().HasExclusionConstraint(
                "reservations_no_overlap",
                constraint => constraint
                    .UseIndexMethod("gist")
                    .HasProperty(
                        item => item.During,
                        "&&",
                        element => element
                            .UseCollation("C", "pg_catalog")
                            .UseOperatorClass("range_ops", "pg_catalog")
                            .HasOperatorClassParameter("buffering", "auto")
                            .IsDescending()
                            .HasNullSortOrder(BlueTuskExclusionNullSortOrder.NullsLast),
                        "pg_catalog")
                    .HasExpression("lower(note)", "=")
                    .IncludeProperties(nameof(Reservation.Note))
                    .HasStorageParameter("fillfactor", "80")
                    .UseTablespace("fast_space")
                    .HasFilter("active")
                    .IsDeferrable(initiallyDeferred: true));
        }
    }

    private sealed class LiveExclusionContext(DbContextOptions<LiveExclusionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureLiveConstraint(modelBuilder, "reservations_no_overlap");
    }

    private sealed class RenamedExclusionContext(DbContextOptions<RenamedExclusionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureLiveConstraint(modelBuilder, "reservations_no_collision");
    }

    private sealed class ChangedExclusionContext(DbContextOptions<ChangedExclusionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureLiveConstraint(modelBuilder, "reservations_no_overlap", "NOT active");
    }

    private sealed class MovedAndRenamedExclusionContext(
        DbContextOptions<MovedAndRenamedExclusionContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureLiveConstraint(
                modelBuilder,
                "reservations_no_collision",
                tableName: "bookings");
    }

    private sealed class NoExclusionContext(DbContextOptions<NoExclusionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntity(modelBuilder, includeNote: true);
    }

    private sealed class ReducedReservationContext(DbContextOptions<ReducedReservationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntity(modelBuilder, includeNote: false);
    }

    private sealed class PartitionedReservationContext(DbContextOptions<PartitionedReservationContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureLiveConstraint(modelBuilder, "reservations_no_overlap");
            modelBuilder.Entity<Reservation>().HasPartitioning(
                BlueTuskPartitionStrategy.Range,
                BlueTuskPartitionKeyDefinition.Column(nameof(Reservation.Id)));
        }
    }

    private sealed class Reservation
    {
        public int Id { get; set; }

        public BlueTuskRange<int> During { get; set; }

        public bool Active { get; set; }

        public string? Note { get; set; }
    }
}

#pragma warning restore EF1001
