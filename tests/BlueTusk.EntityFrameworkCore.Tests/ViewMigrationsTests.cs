using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Views;
using BlueTusk.EntityFrameworkCore.Views.Internal;
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

public sealed class ViewMigrationsTests
{
    private const string OfflineConnectionString =
        "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Model_metadata_generates_views_after_relations_in_dependency_order()
    {
        using var context = CreateContext<InitialViewsContext>(OfflineConnectionString);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel())
            .ToArray();

        var definitions = model.GetViews();
        Assert.Equal(3, definitions.Views.Count);
        var materialized = Assert.Single(definitions.MaterializedViews);
        Assert.Equal("heap", materialized.AccessMethod);
        Assert.Equal("80", Assert.Single(materialized.StorageParameters).ValueSql);
        Assert.True(materialized.IsPopulated);

        var createTable = Array.FindIndex(operations, operation => operation is CreateTableOperation);
        var active = Array.FindIndex(
            operations,
            operation => operation is CreateViewOperation create &&
                         create.Definition.Name == "active_sales");
        var highValue = Array.FindIndex(
            operations,
            operation => operation is CreateViewOperation create &&
                         create.Definition.Name == "high_value_sales");
        var summary = Array.FindIndex(
            operations,
            operation => operation is CreateMaterializedViewOperation);
        var summaryView = Array.FindIndex(
            operations,
            operation => operation is CreateViewOperation create &&
                         create.Definition.Name == "sales_summary_view");
        Assert.True(createTable >= 0 && createTable < active);
        Assert.True(active < highValue);
        Assert.True(summary < summaryView);

        var sql = string.Concat(context.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, model)
            .Select(command => command.CommandText));
        Assert.Contains(
            "CREATE VIEW \"view_tests\".\"active_sales\" (\"id\", \"tenant_id\", \"amount\")",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("security_barrier=true", sql, StringComparison.Ordinal);
        Assert.Contains("security_invoker=true", sql, StringComparison.Ordinal);
        Assert.Contains("check_option=cascaded", sql, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE MATERIALIZED VIEW \"view_tests\".\"sales_summary\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("USING \"heap\" WITH (\"fillfactor\"=80)", sql, StringComparison.Ordinal);
        Assert.Contains("WITH DATA", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_differ_replaces_ordinary_views_and_alters_materialized_storage_in_place()
    {
        using var initialContext = CreateContext<InitialViewsContext>(OfflineConnectionString);
        using var alteredContext = CreateContext<AlteredViewsContext>(OfflineConnectionString);
        var alteredModel = alteredContext.GetService<IDesignTimeModel>().Model;
        var operations = initialContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            alteredModel.GetRelationalModel()).ToArray();

        Assert.Single(operations.OfType<ReplaceViewOperation>());
        var alter = Assert.Single(operations.OfType<AlterMaterializedViewOperation>());
        Assert.Equal("70", Assert.Single(alter.Definition.StorageParameters).ValueSql);
        Assert.Empty(operations.OfType<DropViewOperation>());

        var sql = string.Concat(alteredContext.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, alteredModel)
            .Select(command => command.CommandText));
        Assert.Contains("CREATE OR REPLACE VIEW", sql, StringComparison.Ordinal);
        Assert.Contains("amount >= 10", sql, StringComparison.Ordinal);
        Assert.Contains("SET (security_barrier=false)", sql, StringComparison.Ordinal);
        Assert.Contains("SET (security_invoker=false)", sql, StringComparison.Ordinal);
        Assert.Contains("RESET (check_option)", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER MATERIALIZED VIEW", sql, StringComparison.Ordinal);
        Assert.Contains("SET (\"fillfactor\"=70)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialized_auxiliary_changes_use_alter_and_refresh_without_replacing_the_query()
    {
        using var initialContext = CreateContext<InitialViewsContext>(OfflineConnectionString);
        using var auxiliaryContext = CreateContext<AuxiliaryMaterializedViewContext>(OfflineConnectionString);
        var model = auxiliaryContext.GetService<IDesignTimeModel>().Model;
        var operations = initialContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            model.GetRelationalModel()).ToArray();

        var alter = Assert.Single(operations.OfType<AlterMaterializedViewOperation>());
        Assert.True(alter.IsDestructiveChange);
        Assert.Empty(operations.OfType<DropViewOperation>());
        Assert.Empty(operations.OfType<CreateMaterializedViewOperation>());
        var sql = string.Concat(auxiliaryContext.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, model)
            .Select(command => command.CommandText));
        Assert.Contains("SET ACCESS METHOD \"archive_heap\"", sql, StringComparison.Ordinal);
        Assert.Contains("SET TABLESPACE \"archive_space\"", sql, StringComparison.Ordinal);
        Assert.Contains("SET (\"autovacuum_enabled\"=false)", sql, StringComparison.Ordinal);
        Assert.Contains("RESET (\"fillfactor\")", sql, StringComparison.Ordinal);
        Assert.Contains("REFRESH MATERIALIZED VIEW", sql, StringComparison.Ordinal);
        Assert.Contains("WITH NO DATA", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialized_query_changes_recreate_the_transitive_dependent_view_closure()
    {
        using var initialContext = CreateContext<InitialViewsContext>(OfflineConnectionString);
        using var changedContext = CreateContext<ChangedMaterializedQueryContext>(OfflineConnectionString);
        var differ = initialContext.GetService<IMigrationsModelDiffer>();
        var operations = differ.GetDifferences(
            initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            changedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();

        var drops = operations.OfType<DropViewOperation>().ToArray();
        Assert.Equal(2, drops.Length);
        Assert.Equal("sales_summary_view", drops[0].Name);
        Assert.Equal("sales_summary", drops[1].Name);
        Assert.All(drops, operation => Assert.True(operation.IsDestructiveChange));

        var materializedCreate = Assert.Single(operations.OfType<CreateMaterializedViewOperation>());
        var dependentCreate = Assert.Single(
            operations.OfType<CreateViewOperation>(),
            operation => operation.Definition.Name == "sales_summary_view");
        Assert.True(Array.IndexOf(operations, materializedCreate) < Array.IndexOf(operations, dependentCreate));
    }

    [Fact]
    public void View_and_routine_operations_preserve_cross_object_dependency_order()
    {
        using var context = CreateContext<ViewWithRoutineContext>(OfflineConnectionString);
        using var emptyContext = CreateContext<EmptyContext>(OfflineConnectionString);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var creates = differ.GetDifferences(null, model).ToArray();
        var drops = differ.GetDifferences(
            model,
            emptyContext.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();

        Assert.Single(creates.OfType<EnsureSchemaOperation>());
        Assert.True(
            Array.FindIndex(creates, operation => operation is CreateRoutineOperation) <
            Array.FindIndex(creates, operation => operation is CreateViewOperation));
        Assert.True(
            Array.FindIndex(drops, operation => operation is DropViewOperation) <
            Array.FindIndex(drops, operation => operation is DropRoutineOperation));
    }

    [Fact]
    public void Differ_infers_rename_and_rejects_unsupported_relation_or_column_changes()
    {
        using var initialContext = CreateContext<InitialViewsContext>(OfflineConnectionString);
        using var renamedContext = CreateContext<RenamedViewContext>(OfflineConnectionString);
        using var badColumnsContext = CreateContext<BadViewColumnsContext>(OfflineConnectionString);
        using var kindContext = CreateContext<MaterializedKindContext>(OfflineConnectionString);
        var differ = initialContext.GetService<IMigrationsModelDiffer>();
        var source = initialContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var rename = Assert.Single(differ.GetDifferences(
                source,
                renamedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .OfType<RenameViewOperation>());
        Assert.Equal("high_value_sales", rename.Name);
        Assert.Equal("premium_sales", rename.NewName);

        Assert.Contains(
            "cannot rename, remove, or reorder",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                source,
                badColumnsContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "between VIEW and MATERIALIZED VIEW",
            Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
                source,
                kindContext.GetService<IDesignTimeModel>().Model.GetRelationalModel())).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Recursive_and_refresh_validation_matches_PostgreSQL_rules()
    {
        var modelBuilder = new ModelBuilder();
        Assert.Contains(
            "cannot use CHECK OPTION",
            Assert.Throws<ArgumentException>(() => modelBuilder.HasView(
                "numbers",
                "VALUES (1) UNION ALL SELECT number + 1 FROM numbers WHERE number < 10",
                view => view
                    .HasColumn("number")
                    .IsRecursive()
                    .HasCheckOption(BlueTuskViewCheckOption.Local))).Message,
            StringComparison.Ordinal);

        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        Assert.Throws<ArgumentException>(() => migration.RefreshMaterializedView(
            "sales_summary",
            "view_tests",
            concurrently: true,
            withData: false));
        migration.RefreshMaterializedView(
            "sales_summary",
            "view_tests",
            concurrently: true);

        using var context = CreateContext<InitialViewsContext>(OfflineConnectionString);
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.Operations, context.GetService<IDesignTimeModel>().Model));
        Assert.Contains(
            "REFRESH MATERIALIZED VIEW CONCURRENTLY \"view_tests\".\"sales_summary\" WITH DATA",
            command.CommandText,
            StringComparison.Ordinal);

        var recursive = modelBuilder.Model.GetViews().Views.SingleOrDefault();
        Assert.Null(recursive);
        var recursiveMigration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        recursiveMigration.CreateView(new BlueTuskViewDefinition(
            "numbers",
            "view_tests",
            "VALUES (1) UNION ALL SELECT number + 1 FROM numbers WHERE number < 10",
            ["number"],
            [],
            IsRecursive: true));
        var recursiveCommand = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate(recursiveMigration.Operations, context.GetService<IDesignTimeModel>().Model));
        Assert.Contains("CREATE RECURSIVE VIEW", recursiveCommand.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Design_time_generator_scaffolds_all_view_operation_families()
    {
        var initial = CreateDefinitions<InitialViewsContext>();
        var altered = CreateDefinitions<AlteredViewsContext>();
        var oldView = initial.Views.Single(definition => definition.Name == "active_sales");
        var view = altered.Views.Single(definition => definition.Name == "active_sales");
        var oldMaterialized = Assert.Single(initial.MaterializedViews);
        var materialized = Assert.Single(altered.MaterializedViews);
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
                new CreateViewOperation { Definition = oldView },
                new ReplaceViewOperation { OldDefinition = oldView, Definition = view },
                new CreateMaterializedViewOperation { Definition = oldMaterialized },
                new AlterMaterializedViewOperation
                {
                    OldDefinition = oldMaterialized,
                    Definition = materialized,
                },
                new DropViewOperation
                {
                    Kind = BlueTuskViewKind.View,
                    Name = oldView.Name,
                    Schema = oldView.Schema,
                },
                new RenameViewOperation
                {
                    Kind = BlueTuskViewKind.MaterializedView,
                    Name = oldMaterialized.Name,
                    Schema = oldMaterialized.Schema,
                    NewName = "summary_v2",
                    NewSchema = oldMaterialized.Schema,
                },
                new RefreshMaterializedViewOperation
                {
                    Name = oldMaterialized.Name,
                    Schema = oldMaterialized.Schema,
                    Concurrently = true,
                },
            ],
            builder);

        var code = builder.ToString();
        Assert.Contains("CreateView", code, StringComparison.Ordinal);
        Assert.Contains("ReplaceView", code, StringComparison.Ordinal);
        Assert.Contains("CreateMaterializedView", code, StringComparison.Ordinal);
        Assert.Contains("AlterMaterializedView", code, StringComparison.Ordinal);
        Assert.Contains("DropView", code, StringComparison.Ordinal);
        Assert.Contains("RenameView", code, StringComparison.Ordinal);
        Assert.Contains("RefreshMaterializedView", code, StringComparison.Ordinal);
        Assert.Contains("BlueTuskViewKind.MaterializedView", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Views_execute_round_trip_scaffold_replace_refresh_and_rename_across_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS view_tests CASCADE");

        try
        {
            using var initialContext = CreateContext<InitialViewsContext>(connectionString);
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
                INSERT INTO view_tests.sales (tenant_id, amount)
                VALUES (1, 5), (1, 75), (2, -1);
                CREATE UNIQUE INDEX sales_summary_tenant_key
                    ON view_tests.sales_summary (tenant_id);
                """);
            Assert.Equal(2, await ExecuteScalarAsync(
                connectionString,
                "SELECT count(*)::integer FROM view_tests.active_sales"));
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteNonQueryAsync(
                connectionString,
                "INSERT INTO view_tests.active_sales (tenant_id, amount) VALUES (3, -5)"));

            var refresh = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            refresh.RefreshMaterializedView(
                "sales_summary",
                "view_tests",
                concurrently: true);
            var refreshCommand = Assert.Single(initialContext.GetService<IMigrationsSqlGenerator>()
                .Generate(refresh.Operations, initialModel));
            await ExecuteNonQueryAsync(connectionString, refreshCommand.CommandText);
            Assert.Equal(2, await ExecuteScalarAsync(
                connectionString,
                "SELECT count(*)::integer FROM view_tests.sales_summary"));

            var databaseModel = new BlueTuskDatabaseModelFactory().Create(
                connectionString,
                new DatabaseModelFactoryOptions([], ["view_tests"]));
            var discovered = BlueTuskViewMetadata.Deserialize(
                Assert.IsType<string>(databaseModel[BlueTuskViewMetadata.AnnotationName]));
            Assert.Equal(3, discovered.Views.Count);
            var active = discovered.Views.Single(definition => definition.Name == "active_sales");
            Assert.True(active.SecurityBarrier);
            Assert.True(active.SecurityInvoker);
            Assert.Equal(BlueTuskViewCheckOption.Cascaded, active.CheckOption);
            Assert.Contains("sales", active.QuerySql, StringComparison.Ordinal);
            var summary = Assert.Single(discovered.MaterializedViews);
            Assert.Equal("heap", summary.AccessMethod);
            Assert.True(summary.IsPopulated);
            Assert.Equal("80", Assert.Single(summary.StorageParameters).ValueSql);
            var dependent = discovered.Views.Single(definition => definition.Name == "sales_summary_view");
            Assert.Contains(
                dependent.Dependencies,
                dependency => dependency.Name == "sales_summary" && dependency.Schema == "view_tests");

            var services = new ServiceCollection();
            services.AddEntityFrameworkDesignTimeServices();
            services.AddEntityFrameworkBlueTusk();
            new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
            await using (var provider = services.BuildServiceProvider())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    connectionString,
                    new DatabaseModelFactoryOptions([], ["view_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "ViewContext",
                        ConnectionString = connectionString,
                        ContextNamespace = "ViewModels",
                        ModelNamespace = "ViewModels",
                        RootNamespace = "ViewModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasViews(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
            }

            using var alteredContext = CreateContext<AlteredViewsContext>(connectionString);
            var alteredModel = alteredContext.GetService<IDesignTimeModel>().Model;
            var alteredOperations = alteredContext.GetService<IMigrationsModelDiffer>().GetDifferences(
                initialModel.GetRelationalModel(),
                alteredModel.GetRelationalModel());
            foreach (var command in alteredContext.GetService<IMigrationsSqlGenerator>()
                         .Generate(alteredOperations, alteredModel))
            {
                await ExecuteNonQueryAsync(connectionString, command.CommandText);
            }

            Assert.Equal(1, await ExecuteScalarAsync(
                connectionString,
                "SELECT count(*)::integer FROM view_tests.active_sales"));
            Assert.Equal(false, await ExecuteScalarAsync(
                connectionString,
                "SELECT reloptions @> ARRAY['security_barrier=true'] FROM pg_class WHERE oid = 'view_tests.active_sales'::regclass"));
            Assert.Equal("70", await ExecuteScalarAsync(
                connectionString,
                "SELECT option_value FROM pg_options_to_table((SELECT reloptions FROM pg_class WHERE oid = 'view_tests.sales_summary'::regclass)) WHERE option_name = 'fillfactor'"));

            var rename = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
            rename.RenameView(
                BlueTuskViewKind.View,
                "high_value_sales",
                "premium_sales",
                "view_tests");
            var renameCommand = Assert.Single(alteredContext.GetService<IMigrationsSqlGenerator>()
                .Generate(rename.Operations, alteredModel));
            await ExecuteNonQueryAsync(connectionString, renameCommand.CommandText);
            Assert.Equal(true, await ExecuteScalarAsync(
                connectionString,
                "SELECT to_regclass('view_tests.premium_sales') IS NOT NULL"));
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP SCHEMA IF EXISTS view_tests CASCADE");
        }
    }

    private static BlueTuskViewDefinitionSet CreateDefinitions<TContext>()
        where TContext : DbContext
    {
        using var context = CreateContext<TContext>(OfflineConnectionString);
        return context.GetService<IDesignTimeModel>().Model.GetViews();
    }

    private static void ConfigureViews(
        ModelBuilder modelBuilder,
        bool altered,
        bool changedMaterializedQuery = false)
    {
        modelBuilder.Entity<Sale>(entity =>
        {
            entity.ToTable("sales", "view_tests");
            entity.HasKey(sale => sale.Id);
            entity.Property(sale => sale.Id).HasColumnName("id");
            entity.Property(sale => sale.TenantId).HasColumnName("tenant_id");
            entity.Property(sale => sale.Amount).HasColumnName("amount").HasColumnType("numeric");
        });
        modelBuilder.HasView(
            "active_sales",
            altered
                ? "SELECT id, tenant_id, amount FROM view_tests.sales WHERE amount >= 10"
                : "SELECT id, tenant_id, amount FROM view_tests.sales WHERE amount >= 0",
            view =>
            {
                view.HasColumns("id", "tenant_id", "amount");
                if (!altered)
                {
                    view.IsSecurityBarrier()
                        .IsSecurityInvoker()
                        .HasCheckOption(BlueTuskViewCheckOption.Cascaded);
                }
            },
            "view_tests");
        modelBuilder.HasView(
            "high_value_sales",
            "SELECT id, tenant_id, amount FROM view_tests.active_sales WHERE amount >= 50",
            view => view
                .HasColumns("id", "tenant_id", "amount")
                .DependsOnView("active_sales", "view_tests"),
            "view_tests");
        modelBuilder.HasMaterializedView(
            "sales_summary",
            changedMaterializedQuery
                ? "SELECT tenant_id, count(*)::bigint AS sale_count, (sum(amount) + 1)::numeric AS total FROM view_tests.sales GROUP BY tenant_id"
                : "SELECT tenant_id, count(*)::bigint AS sale_count, sum(amount)::numeric AS total FROM view_tests.sales GROUP BY tenant_id",
            view => view
                .HasColumns("tenant_id", "sale_count", "total")
                .UseAccessMethod("heap")
                .HasStorageParameter("fillfactor", altered ? "70" : "80")
                .IsPopulated(),
            "view_tests");
        modelBuilder.HasView(
            "sales_summary_view",
            "SELECT tenant_id, sale_count, total FROM view_tests.sales_summary",
            view => view
                .HasColumns("tenant_id", "sale_count", "total")
                .DependsOnView("sales_summary", "view_tests"),
            "view_tests");
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

    private sealed class InitialViewsContext(DbContextOptions<InitialViewsContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureViews(modelBuilder, altered: false);
    }

    private sealed class AlteredViewsContext(DbContextOptions<AlteredViewsContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureViews(modelBuilder, altered: true);
    }

    private sealed class ChangedMaterializedQueryContext(
        DbContextOptions<ChangedMaterializedQueryContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureViews(modelBuilder, altered: false, changedMaterializedQuery: true);
    }

    private sealed class AuxiliaryMaterializedViewContext(
        DbContextOptions<AuxiliaryMaterializedViewContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureViews(modelBuilder, altered: false);
            modelBuilder.HasMaterializedView(
                "sales_summary",
                "SELECT tenant_id, count(*)::bigint AS sale_count, sum(amount)::numeric AS total FROM view_tests.sales GROUP BY tenant_id",
                view => view
                    .HasColumns("tenant_id", "sale_count", "total")
                    .UseAccessMethod("archive_heap")
                    .HasStorageParameter("autovacuum_enabled", "false")
                    .UseTablespace("archive_space")
                    .IsPopulated(false),
                "view_tests");
        }
    }

    private sealed class RenamedViewContext(DbContextOptions<RenamedViewContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureViews(modelBuilder, altered: false);
            modelBuilder.HasNoView("high_value_sales", "view_tests")
                .HasView(
                    "premium_sales",
                    "SELECT id, tenant_id, amount FROM view_tests.active_sales WHERE amount >= 50",
                    view => view
                        .HasColumns("id", "tenant_id", "amount")
                        .DependsOnView("active_sales", "view_tests"),
                    "view_tests");
        }
    }

    private sealed class ViewWithRoutineContext(DbContextOptions<ViewWithRoutineContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasFunction(
                "increment_value",
                "integer",
                "SELECT value + 1",
                function => function.HasParameter("integer", "value"),
                "view_tests");
            modelBuilder.HasView(
                "incremented_value",
                "SELECT view_tests.increment_value(1) AS value",
                view => view.HasColumn("value"),
                "view_tests");
        }
    }

    private sealed class BadViewColumnsContext(DbContextOptions<BadViewColumnsContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureViews(modelBuilder, altered: false);
            modelBuilder.HasView(
                "active_sales",
                "SELECT id, amount FROM view_tests.sales WHERE amount >= 0",
                view => view.HasColumns("id", "amount"),
                "view_tests");
        }
    }

    private sealed class MaterializedKindContext(DbContextOptions<MaterializedKindContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureViews(modelBuilder, altered: false);
            modelBuilder.HasMaterializedView(
                "active_sales",
                "SELECT id, tenant_id, amount FROM view_tests.sales WHERE amount >= 0",
                view => view.HasColumns("id", "tenant_id", "amount"),
                "view_tests");
        }
    }

    private sealed class Sale
    {
        public int Id { get; set; }

        public int TenantId { get; set; }

        public decimal Amount { get; set; }
    }

    private sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options);
}

#pragma warning restore EF1001
