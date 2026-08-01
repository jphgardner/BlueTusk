using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.ForeignData;
using BlueTusk.EntityFrameworkCore.ForeignData.Internal;
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

#pragma warning disable EF1001

public sealed class ForeignDataMigrationsTests
{
    private const string Offline = "Host=localhost;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Foreign_data_SQL_diffs_dependency_order_and_generated_CSharp_are_preserved()
    {
        using var initial = Create<ForeignDataContext>(Offline);
        using var changed = Create<ChangedForeignDataContext>(Offline);
        using var renamed = Create<RenamedForeignDataContext>(Offline);
        using var removed = Create<NoForeignDataContext>(Offline);
        var model = initial.GetService<IDesignTimeModel>().Model;
        var differ = initial.GetService<IMigrationsModelDiffer>();
        var creates = differ.GetDifferences(null, model.GetRelationalModel()).ToArray();
        var wrapper = Assert.Single(creates.OfType<CreateBlueTuskForeignDataWrapperOperation>());
        var server = Assert.Single(creates.OfType<CreateBlueTuskForeignServerOperation>());
        var mappings = creates.OfType<CreateBlueTuskUserMappingOperation>().ToArray();
        Assert.Equal(2, mappings.Length);
        var mapping = Assert.Single(mappings, operation => operation.Definition.UserName is null);
        var table = Assert.Single(creates.OfType<CreateTableOperation>());
        Assert.True(Array.IndexOf(creates, wrapper) < Array.IndexOf(creates, server));
        Assert.True(Array.IndexOf(creates, server) < Array.IndexOf(creates, mapping));
        Assert.True(Array.IndexOf(creates, mapping) < Array.IndexOf(creates, table));

        var generator = initial.GetService<IMigrationsSqlGenerator>();
        var createSql = string.Concat(generator.Generate(creates, model).Select(command => command.CommandText));
        Assert.Contains(
            "CREATE FOREIGN DATA WRAPPER \"test_fdw\" NO HANDLER NO VALIDATOR " +
            "OPTIONS (\"wrapper_option\" 'one')",
            createSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE SERVER \"test_server\" TYPE 'remote' VERSION '1' " +
            "FOREIGN DATA WRAPPER \"test_fdw\" OPTIONS (\"endpoint\" 'one')",
            createSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE USER MAPPING FOR PUBLIC SERVER \"test_server\" OPTIONS (\"user\" 'remote_user')",
            createSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE USER MAPPING FOR \"postgres\" SERVER \"test_server\" " +
            "OPTIONS (\"user\" 'mapped_postgres')",
            createSql,
            StringComparison.Ordinal);
        Assert.Contains("CREATE FOREIGN TABLE \"foreign_tests\".\"foreign_records\"", createSql,
            StringComparison.Ordinal);
        Assert.Contains("\"id\" integer OPTIONS (\"column_name\" 'remote_id') NOT NULL", createSql,
            StringComparison.Ordinal);
        Assert.Contains("SERVER \"test_server\" OPTIONS (\"table_name\" 'remote_records')", createSql,
            StringComparison.Ordinal);

        var changedModel = changed.GetService<IDesignTimeModel>().Model;
        var alters = differ.GetDifferences(model.GetRelationalModel(), changedModel.GetRelationalModel()).ToArray();
        Assert.Single(alters.OfType<AlterBlueTuskForeignDataWrapperOperation>());
        Assert.Single(alters.OfType<AlterBlueTuskForeignServerOperation>());
        Assert.Equal(2, alters.OfType<AlterBlueTuskUserMappingOperation>().Count());
        Assert.Single(alters.OfType<AlterTableOperation>());
        var alterSql = string.Concat(generator.Generate(alters, changedModel)
            .Select(command => command.CommandText));
        Assert.Contains("ALTER FOREIGN DATA WRAPPER \"test_fdw\" OPTIONS (SET \"wrapper_option\" 'two')",
            alterSql, StringComparison.Ordinal);
        Assert.Contains("ALTER SERVER \"test_server\" VERSION '2' OPTIONS (SET \"endpoint\" 'two')",
            alterSql, StringComparison.Ordinal);
        Assert.Contains(
            "ALTER USER MAPPING FOR PUBLIC SERVER \"test_server\" OPTIONS (SET \"user\" 'remote_user_v2')",
            alterSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER FOREIGN TABLE \"foreign_tests\".\"foreign_records\" " +
            "OPTIONS (SET \"table_name\" 'remote_records_v2')",
            alterSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER COLUMN \"id\" OPTIONS (SET \"column_name\" 'remote_id_v2')",
            alterSql,
            StringComparison.Ordinal);

        var renamedModel = renamed.GetService<IDesignTimeModel>().Model;
        var renames = differ.GetDifferences(
            changedModel.GetRelationalModel(), renamedModel.GetRelationalModel()).ToArray();
        Assert.Single(renames.OfType<RenameBlueTuskForeignDataWrapperOperation>());
        Assert.Single(renames.OfType<RenameBlueTuskForeignServerOperation>());
        Assert.Single(renames.OfType<RenameTableOperation>());
        Assert.Empty(renames.OfType<CreateBlueTuskUserMappingOperation>());
        Assert.Empty(renames.OfType<DropBlueTuskUserMappingOperation>());
        _ = generator.Generate(renames, renamedModel);

        var removals = differ.GetDifferences(
            renamedModel.GetRelationalModel(),
            removed.GetService<IDesignTimeModel>().Model.GetRelationalModel()).ToArray();
        var dropTableIndex = Array.FindIndex(removals, operation => operation is DropTableOperation);
        var dropMappingIndex = Array.FindIndex(removals, operation => operation is DropBlueTuskUserMappingOperation);
        var dropServerIndex = Array.FindIndex(removals, operation => operation is DropBlueTuskForeignServerOperation);
        var dropWrapperIndex = Array.FindIndex(
            removals,
            operation => operation is DropBlueTuskForeignDataWrapperOperation);
        Assert.True(dropTableIndex < dropMappingIndex);
        Assert.True(dropMappingIndex < dropServerIndex);
        Assert.True(dropServerIndex < dropWrapperIndex);
        var dropSql = string.Concat(generator.Generate(removals).Select(command => command.CommandText));
        Assert.Contains("DROP FOREIGN TABLE \"foreign_tests\".\"foreign_records_v2\"", dropSql,
            StringComparison.Ordinal);

        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateBlueTuskForeignDataWrapper(wrapper.Definition);
        migration.AlterBlueTuskForeignDataWrapper(wrapper.Definition, wrapper.Definition with { Options = [] });
        migration.RenameBlueTuskForeignDataWrapper("test_fdw", "test_fdw_v2");
        migration.CreateBlueTuskForeignServer(server.Definition);
        migration.AlterBlueTuskForeignServer(server.Definition, server.Definition with { Version = "2" });
        migration.RenameBlueTuskForeignServer("test_server", "test_server_v2");
        migration.CreateBlueTuskUserMapping(mapping.Definition);
        migration.AlterBlueTuskUserMapping(
            mapping.Definition,
            mapping.Definition with { Options = [new BlueTuskForeignOptionDefinition("user", "next")] });
        migration.DropBlueTuskUserMapping("test_server");
        migration.DropBlueTuskForeignServer("test_server_v2");
        migration.DropBlueTuskForeignDataWrapper("test_fdw_v2");
        using var provider = DesignServices();
        var codeBuilder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, codeBuilder);
        var code = codeBuilder.ToString();
        Assert.Contains("CreateBlueTuskForeignDataWrapper", code, StringComparison.Ordinal);
        Assert.Contains("AlterBlueTuskForeignDataWrapper", code, StringComparison.Ordinal);
        Assert.Contains("RenameBlueTuskForeignDataWrapper", code, StringComparison.Ordinal);
        Assert.Contains("CreateBlueTuskForeignServer", code, StringComparison.Ordinal);
        Assert.Contains("AlterBlueTuskForeignServer", code, StringComparison.Ordinal);
        Assert.Contains("RenameBlueTuskForeignServer", code, StringComparison.Ordinal);
        Assert.Contains("CreateBlueTuskUserMapping", code, StringComparison.Ordinal);
        Assert.Contains("AlterBlueTuskUserMapping", code, StringComparison.Ordinal);
        Assert.Contains("DropBlueTuskUserMapping", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_functions_and_mapping_secrets_are_guarded()
    {
        using var context = Create<ForeignDataContext>(Offline);
        var definition = new BlueTuskForeignDataWrapperDefinition(
            "connected_fdw",
            null,
            null,
            "public.connect_source",
            []);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [new CreateBlueTuskForeignDataWrapperOperation { Definition = definition }]);
        Assert.Contains("server_version_num')::integer < 190000", commands[0].CommandText,
            StringComparison.Ordinal);
        Assert.Contains("CONNECTION \"public\".\"connect_source\"", commands[1].CommandText,
            StringComparison.Ordinal);

        var model = new ModelBuilder();
        Assert.Throws<ArgumentException>(() => model.HasBlueTuskPublicUserMapping(
            "test_server",
            mapping => mapping.HasOption("password", "do-not-store")));

        Assert.Throws<InvalidOperationException>(() => context.GetService<IMigrationsSqlGenerator>().Generate(
            [new CreateBlueTuskUserMappingOperation
            {
                Definition = new BlueTuskUserMappingDefinition("test_server", null, [], OptionsRedacted: true),
            }]));

        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateBlueTuskUserMapping(new BlueTuskUserMappingDefinition(
            "test_server",
            null,
            [new BlueTuskForeignOptionDefinition("password", "runtime-secret")]));
        using var provider = DesignServices();
        Assert.Throws<ArgumentException>(() => provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, new IndentedStringBuilder()));
    }

    [Fact]
    public async Task Foreign_data_round_trips_alters_renames_scaffolds_and_drops_across_PostgreSQL()
    {
        var cs = ConnectionString();
        await Cleanup(cs);
        try
        {
            using var initial = Create<ForeignDataContext>(cs);
            var initialModel = initial.GetService<IDesignTimeModel>().Model;
            await Apply(initial, null, initialModel, cs);

            var database = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], ["foreign_tests"]));
            var definitions = BlueTuskForeignDataMetadata.Deserialize(Assert.IsType<string>(
                database[BlueTuskForeignDataMetadata.AnnotationName]));
            var wrapper = Assert.Single(definitions.Wrappers, item => item.Name == "test_fdw");
            Assert.Equal("one", Assert.Single(wrapper.Options).Value);
            var server = Assert.Single(definitions.Servers, item => item.Name == "test_server");
            Assert.Equal("remote", server.Type);
            Assert.Equal("1", server.Version);
            Assert.Equal("one", Assert.Single(server.Options).Value);
            var mapping = Assert.Single(
                definitions.UserMappings,
                item => item.ServerName == "test_server" && item.UserName is null);
            Assert.True(mapping.OptionsRedacted);
            Assert.Empty(mapping.Options);
            Assert.True(Assert.Single(
                definitions.UserMappings,
                item => item.ServerName == "test_server" && item.UserName == "postgres").OptionsRedacted);
            await VerifyRestrictedDiscovery(cs);
            var table = Assert.Single(database.Tables, item => item.Name == "foreign_records");
            var foreignTable = BlueTuskForeignDataMetadata.DeserializeForeignTable(Assert.IsType<string>(
                table[BlueTuskForeignDataMetadata.ForeignTableAnnotationName]));
            Assert.Equal("test_server", foreignTable.ServerName);
            Assert.Equal("remote_records", Assert.Single(foreignTable.Options).Value);
            Assert.Equal("remote_id", Assert.Single(Assert.Single(foreignTable.Columns).Options).Value);
            var version = Convert.ToInt32(await Scalar(cs, "SHOW server_version_num"),
                System.Globalization.CultureInfo.InvariantCulture);
            if (version >= 190000)
            {
                await VerifyConnectionFunction(initial, cs);
            }

            using (var provider = DesignServices())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    cs,
                    new DatabaseModelFactoryOptions([], ["foreign_tests"]),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "ForeignDataScaffoldContext",
                        ConnectionString = cs,
                        ContextNamespace = "ForeignDataModels",
                        ModelNamespace = "ForeignDataModels",
                        RootNamespace = "ForeignDataModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasBlueTuskForeignData(", scaffolded.ContextFile.Code, StringComparison.Ordinal);
                Assert.Contains("HasBlueTuskForeignTableDefinition(", scaffolded.ContextFile.Code,
                    StringComparison.Ordinal);
                Assert.Contains("HasNoKey()", scaffolded.ContextFile.Code, StringComparison.Ordinal);
                Assert.DoesNotContain("remote_user", scaffolded.ContextFile.Code, StringComparison.Ordinal);
            }

            using var changed = Create<ChangedForeignDataContext>(cs);
            var changedModel = changed.GetService<IDesignTimeModel>().Model;
            await Apply(changed, initialModel, changedModel, cs);
            Assert.Equal("{wrapper_option=two}", await Scalar(
                cs,
                "SELECT fdwoptions::text FROM pg_catalog.pg_foreign_data_wrapper WHERE fdwname = 'test_fdw'"));
            Assert.Equal("{endpoint=two}", await Scalar(
                cs,
                "SELECT srvoptions::text FROM pg_catalog.pg_foreign_server WHERE srvname = 'test_server'"));
            Assert.Equal("{table_name=remote_records_v2}", await Scalar(
                cs,
                "SELECT ftoptions::text FROM pg_catalog.pg_foreign_table AS ft " +
                "JOIN pg_catalog.pg_class AS c ON c.oid = ft.ftrelid WHERE c.relname = 'foreign_records'"));

            using var renamed = Create<RenamedForeignDataContext>(cs);
            var renamedModel = renamed.GetService<IDesignTimeModel>().Model;
            await Apply(renamed, changedModel, renamedModel, cs);
            Assert.Equal(true, await Scalar(
                cs,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_foreign_data_wrapper WHERE fdwname = 'test_fdw_v2')"));
            Assert.Equal(true, await Scalar(
                cs,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_foreign_server WHERE srvname = 'test_server_v2')"));
            Assert.Equal(true, await Scalar(
                cs,
                "SELECT to_regclass('foreign_tests.foreign_records_v2') IS NOT NULL"));

            using var removed = Create<NoForeignDataContext>(cs);
            await Apply(removed, renamedModel, removed.GetService<IDesignTimeModel>().Model, cs);
            Assert.Equal(false, await Scalar(
                cs,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_foreign_data_wrapper WHERE fdwname = 'test_fdw_v2')"));
        }
        finally
        {
            await Cleanup(cs);
        }
    }

    private static void Configure(ModelBuilder modelBuilder, bool changed, bool renamed)
    {
        var wrapperName = renamed ? "test_fdw_v2" : "test_fdw";
        var serverName = renamed ? "test_server_v2" : "test_server";
        modelBuilder.HasBlueTuskForeignDataWrapper(
            wrapperName,
            wrapper => wrapper.HasOption("wrapper_option", changed ? "two" : "one"));
        modelBuilder.HasBlueTuskForeignServer(
            serverName,
            wrapperName,
            server => server
                .HasType("remote")
                .HasVersion(changed ? "2" : "1")
                .HasOption("endpoint", changed ? "two" : "one"));
        modelBuilder.HasBlueTuskPublicUserMapping(
            serverName,
            mapping => mapping.HasOption("user", changed ? "remote_user_v2" : "remote_user"));
        modelBuilder.HasBlueTuskUserMapping(
            serverName,
            "postgres",
            mapping => mapping.HasOption("user", changed ? "mapped_postgres_v2" : "mapped_postgres"));
        modelBuilder.Entity<ForeignRecord>(entity =>
        {
            entity.HasNoKey();
            entity.ToTable(renamed ? "foreign_records_v2" : "foreign_records", "foreign_tests");
            entity.Property(record => record.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(record => record.Note).HasColumnName("note").IsRequired();
            entity.HasBlueTuskForeignTable(serverName, table => table
                .HasOption("table_name", changed ? "remote_records_v2" : "remote_records")
                .HasColumnOption("id", "column_name", changed ? "remote_id_v2" : "remote_id"));
        });
    }

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

    private static async Task VerifyConnectionFunction(DbContext context, string cs)
    {
        var extensionWasInstalled = Convert.ToBoolean(await Scalar(
            cs,
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_extension WHERE extname = 'postgres_fdw')"),
            System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await Execute(cs, "CREATE EXTENSION IF NOT EXISTS postgres_fdw");
            var definition = new BlueTuskForeignDataWrapperDefinition(
                "test_connected_fdw",
                null,
                null,
                "public.postgres_fdw_connection",
                []);
            foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(
                         [new CreateBlueTuskForeignDataWrapperOperation { Definition = definition }]))
            {
                await Execute(cs, command.CommandText);
            }

            var database = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], ["foreign_tests"]));
            var definitions = BlueTuskForeignDataMetadata.Deserialize(Assert.IsType<string>(
                database[BlueTuskForeignDataMetadata.AnnotationName]));
            Assert.Equal(
                "public.postgres_fdw_connection",
                Assert.Single(definitions.Wrappers, item => item.Name == definition.Name).ConnectionFunction);
        }
        finally
        {
            await Execute(cs, "DROP FOREIGN DATA WRAPPER IF EXISTS test_connected_fdw CASCADE");
            if (!extensionWasInstalled)
            {
                await Execute(cs, "DROP EXTENSION IF EXISTS postgres_fdw CASCADE");
            }
        }
    }

    private static async Task VerifyRestrictedDiscovery(string cs)
    {
        const string role = "bluetusk_foreign_data_reader";
        await Execute(cs, $"DROP ROLE IF EXISTS {role}");
        try
        {
            await Execute(cs, $"CREATE ROLE {role} LOGIN PASSWORD 'catalog-reader-password'");
            var restrictedConnection = new BlueTuskConnectionStringBuilder(cs)
            {
                Username = role,
                Password = "catalog-reader-password",
            }.ConnectionString;
            var database = new BlueTuskDatabaseModelFactory().Create(
                restrictedConnection,
                new DatabaseModelFactoryOptions([], ["foreign_tests"]));
            var definitions = BlueTuskForeignDataMetadata.Deserialize(Assert.IsType<string>(
                database[BlueTuskForeignDataMetadata.AnnotationName]));
            var mapping = Assert.Single(
                definitions.UserMappings,
                item => item.ServerName == "test_server" && item.UserName is null);
            Assert.True(mapping.OptionsRedacted);
            Assert.Empty(mapping.Options);
        }
        finally
        {
            await Execute(cs, $"DROP ROLE IF EXISTS {role}");
        }
    }

    private static async Task Cleanup(string cs) => await Execute(
        cs,
        "DROP SCHEMA IF EXISTS foreign_tests CASCADE; " +
        "DROP SERVER IF EXISTS test_server CASCADE; " +
        "DROP SERVER IF EXISTS test_server_v2 CASCADE; " +
        "DROP FOREIGN DATA WRAPPER IF EXISTS test_fdw CASCADE; " +
        "DROP FOREIGN DATA WRAPPER IF EXISTS test_fdw_v2 CASCADE; " +
        "DROP FOREIGN DATA WRAPPER IF EXISTS test_connected_fdw CASCADE; " +
        "DROP ROLE IF EXISTS bluetusk_foreign_data_reader");

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

    private sealed class ForeignDataContext(DbContextOptions<ForeignDataContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Configure(modelBuilder, changed: false, renamed: false);
    }

    private sealed class ChangedForeignDataContext(DbContextOptions<ChangedForeignDataContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Configure(modelBuilder, changed: true, renamed: false);
    }

    private sealed class RenamedForeignDataContext(DbContextOptions<RenamedForeignDataContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Configure(modelBuilder, changed: true, renamed: true);
    }

    private sealed class NoForeignDataContext(DbContextOptions<NoForeignDataContext> options) : DbContext(options);

    private sealed class ForeignRecord
    {
        public int Id { get; set; }

        public required string Note { get; set; }
    }
}

#pragma warning restore EF1001
