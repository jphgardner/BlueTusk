using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using BlueTusk.EntityFrameworkCore.Tablespaces.Internal;
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

public sealed class TablespaceMigrationsTests
{
    private const string Offline = "Host=localhost;Database=unused;Username=unused;Password=unused";
    private const string Name = "bluetusk_archive_space";
    private const string RenamedName = "bluetusk_archive_space_v2";
    private const string Owner = "bluetusk_tablespace_owner";

    [Fact]
    public void Tablespace_SQL_diffs_ordering_safety_and_generated_CSharp_are_preserved()
    {
        using var initial = Create<InitialContext>(Offline);
        using var altered = Create<AlteredContext>(Offline);
        using var relocated = Create<RelocatedContext>(Offline);
        using var empty = Create<EmptyContext>(Offline);
        var initialModel = initial.GetService<IDesignTimeModel>().Model;
        var alteredModel = altered.GetService<IDesignTimeModel>().Model;
        var differ = initial.GetService<IMigrationsModelDiffer>();
        var creates = differ.GetDifferences(null, initialModel.GetRelationalModel()).ToArray();
        var create = Assert.Single(creates.OfType<CreateTablespaceOperation>());
        Assert.True(Array.IndexOf(creates, create) <
                    Array.FindIndex(creates, operation => operation is CreateTableOperation));

        var generator = initial.GetService<IMigrationsSqlGenerator>();
        var createCommands = generator.Generate(creates, initialModel);
        var createCommand = Assert.Single(createCommands, command =>
            command.CommandText.Contains("CREATE TABLESPACE", StringComparison.Ordinal));
        Assert.True(createCommand.TransactionSuppressed);
        Assert.Contains(
            "CREATE TABLESPACE \"bluetusk_archive_space\" OWNER \"postgres\" " +
            "LOCATION '/srv/postgresql/bluetusk_archive_space' WITH " +
            "(random_page_cost = '1.75', seq_page_cost = '1.25')",
            createCommand.CommandText,
            StringComparison.Ordinal);
        Assert.Contains(createCommands, command =>
            command.CommandText.Contains(
                "COMMENT ON TABLESPACE \"bluetusk_archive_space\" IS 'BlueTusk archive data'",
                StringComparison.Ordinal) &&
            !command.TransactionSuppressed);

        var changes = differ.GetDifferences(
            initialModel.GetRelationalModel(), alteredModel.GetRelationalModel()).ToArray();
        var rename = Assert.Single(changes.OfType<RenameTablespaceOperation>());
        var alter = Assert.Single(changes.OfType<AlterTablespaceOperation>());
        Assert.True(Array.IndexOf(changes, rename) < Array.IndexOf(changes, alter));
        var alterSql = string.Concat(generator.Generate(changes, alteredModel)
            .Select(command => command.CommandText));
        Assert.Contains("ALTER TABLESPACE \"bluetusk_archive_space\" RENAME TO " +
                        "\"bluetusk_archive_space_v2\"", alterSql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLESPACE \"bluetusk_archive_space_v2\" OWNER TO " +
                        "\"bluetusk_tablespace_owner\"", alterSql, StringComparison.Ordinal);
        Assert.Contains("SET (effective_io_concurrency = '3', seq_page_cost = '1.5')", alterSql,
            StringComparison.Ordinal);
        Assert.Contains("RESET (random_page_cost)", alterSql, StringComparison.Ordinal);
        Assert.Contains("COMMENT ON TABLESPACE \"bluetusk_archive_space_v2\" IS 'Updated archive data'",
            alterSql, StringComparison.Ordinal);

        var drops = differ.GetDifferences(
            alteredModel.GetRelationalModel(), empty.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .ToArray();
        var drop = Assert.Single(drops.OfType<DropTablespaceOperation>());
        Assert.True(Array.FindIndex(drops, operation => operation is DropTableOperation) <
                    Array.IndexOf(drops, drop));
        Assert.True(drop.IsDestructiveChange);
        var dropCommand = Assert.Single(generator.Generate(drops, null), command =>
            command.CommandText.Contains("DROP TABLESPACE", StringComparison.Ordinal));
        Assert.True(dropCommand.TransactionSuppressed);

        var exception = Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(
            initialModel.GetRelationalModel(),
            relocated.GetService<IDesignTimeModel>().Model.GetRelationalModel()));
        Assert.Contains("cannot change its filesystem location in place", exception.Message,
            StringComparison.Ordinal);

        var migration = new MigrationBuilder("BlueTusk.EntityFrameworkCore");
        migration.CreateTablespace(create.Definition);
        migration.AlterTablespace(alter.Definition, alter.OldDefinition);
        migration.RenameTablespace(Name, RenamedName);
        migration.DropTablespace(RenamedName, ifExists: true);
        using var provider = DesignServices();
        var codeBuilder = new IndentedStringBuilder();
        provider.GetRequiredService<ICSharpMigrationOperationGenerator>()
            .Generate("migrationBuilder", migration.Operations, codeBuilder);
        var code = codeBuilder.ToString();
        Assert.Contains("CreateTablespace(", code, StringComparison.Ordinal);
        Assert.Contains("AlterTablespace(", code, StringComparison.Ordinal);
        Assert.Contains("RenameTablespace(", code, StringComparison.Ordinal);
        Assert.Contains("DropTablespace(", code, StringComparison.Ordinal);
        Assert.Contains(", true);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_tablespace_names_options_and_locations_are_rejected()
    {
        var modelBuilder = new ModelBuilder();
        Assert.Throws<ArgumentException>(() => modelBuilder.HasTablespace(
            "pg_reserved", "/srv/postgresql/reserved"));
        Assert.Throws<ArgumentException>(() => modelBuilder.HasTablespace(
            "invalid_option",
            "/srv/postgresql/invalid_option",
            tablespace => tablespace.HasOption("future_option", "1")));
        Assert.Throws<ArgumentException>(() => modelBuilder.HasTablespace(
            "missing_location", " "));
    }

    [Fact]
    public async Task Tablespaces_execute_round_trip_alter_rename_scaffold_and_drop_across_PostgreSQL()
    {
        var cs = ConnectionString();
        _ = TablespaceLocation();
        await Cleanup(cs);
        try
        {
            await Execute(cs, $"CREATE ROLE {Owner}");
            using var initial = Create<InitialContext>(cs);
            var initialModel = initial.GetService<IDesignTimeModel>().Model;
            await Apply(initial, null, initialModel, cs);
            Assert.Equal(true, await Scalar(cs,
                $"SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_tablespace WHERE spcname = '{Name}')"));

            var database = new BlueTuskDatabaseModelFactory().Create(
                cs,
                new DatabaseModelFactoryOptions([], []));
            var definitions = BlueTuskTablespaceMetadata.Deserialize(Assert.IsType<string>(
                database[BlueTuskTablespaceMetadata.AnnotationName]));
            var discovered = Assert.Single(definitions.Tablespaces);
            Assert.Equal(Name, discovered.Name);
            Assert.Equal(TablespaceLocation(), discovered.Location);
            Assert.Equal("postgres", discovered.Owner);
            Assert.Equal("BlueTusk archive data", discovered.Comment);
            Assert.Equal(2, discovered.Options.Count);

            using (var provider = DesignServices())
            {
                var scaffolded = provider.GetRequiredService<IReverseEngineerScaffolder>().ScaffoldModel(
                    cs,
                    new DatabaseModelFactoryOptions([], []),
                    new ModelReverseEngineerOptions(),
                    new ModelCodeGenerationOptions
                    {
                        ContextName = "TablespaceScaffoldContext",
                        ConnectionString = cs,
                        ContextNamespace = "TablespaceModels",
                        ModelNamespace = "TablespaceModels",
                        RootNamespace = "TablespaceModels",
                        Language = "C#",
                        ProjectDir = AppContext.BaseDirectory,
                        UseNullableReferenceTypes = true,
                    });
                Assert.Contains("HasTablespaces(", scaffolded.ContextFile.Code,
                    StringComparison.Ordinal);
            }

            using var altered = Create<AlteredContext>(cs);
            var alteredModel = altered.GetService<IDesignTimeModel>().Model;
            await Apply(altered, initialModel, alteredModel, cs);
            Assert.Equal(Owner, await Scalar(cs,
                $"SELECT pg_catalog.pg_get_userbyid(spcowner) FROM pg_catalog.pg_tablespace " +
                $"WHERE spcname = '{RenamedName}'"));
            Assert.Equal("Updated archive data", await Scalar(cs,
                $"SELECT pg_catalog.shobj_description(oid, 'pg_tablespace') " +
                $"FROM pg_catalog.pg_tablespace WHERE spcname = '{RenamedName}'"));
            Assert.Equal(true, await Scalar(cs,
                $"SELECT spcoptions @> ARRAY['seq_page_cost=1.5', 'effective_io_concurrency=3'] " +
                $"FROM pg_catalog.pg_tablespace WHERE spcname = '{RenamedName}'"));

            await Execute(cs, $"CREATE TABLE bluetusk_tablespace_probe (id integer) TABLESPACE {RenamedName}");
            Assert.Equal(RenamedName, await Scalar(cs,
                "SELECT tablespace.spcname FROM pg_catalog.pg_class AS relation " +
                "JOIN pg_catalog.pg_tablespace AS tablespace ON tablespace.oid = relation.reltablespace " +
                "WHERE relation.relname = 'bluetusk_tablespace_probe'"));
            await Execute(cs, "DROP TABLE bluetusk_tablespace_probe");

            using var empty = Create<EmptyContext>(cs);
            await Apply(empty, alteredModel, empty.GetService<IDesignTimeModel>().Model, cs);
            Assert.Equal(false, await Scalar(cs,
                $"SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_tablespace WHERE spcname = '{RenamedName}')"));
        }
        finally
        {
            await Cleanup(cs);
        }
    }

    private static void Configure(ModelBuilder modelBuilder, bool altered, bool relocated = false)
    {
        modelBuilder.Entity<Probe>(entity =>
        {
            entity.ToTable("tablespace_model_probe");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
        });
        modelBuilder.HasTablespace(
            altered ? RenamedName : Name,
            relocated ? $"{TablespaceLocation()}_moved" : TablespaceLocation(),
            tablespace =>
            {
                tablespace.OwnedBy(altered ? Owner : "postgres")
                    .HasSequentialPageCost(altered ? 1.5 : 1.25)
                    .HasComment(altered ? "Updated archive data" : "BlueTusk archive data");
                if (altered)
                {
                    tablespace.HasEffectiveIoConcurrency(3);
                }
                else
                {
                    tablespace.HasRandomPageCost(1.75);
                }
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

    private static async Task Cleanup(string cs)
    {
        await Execute(cs, "DROP TABLE IF EXISTS bluetusk_tablespace_probe");
        await Execute(cs, $"DROP TABLESPACE IF EXISTS {RenamedName}");
        await Execute(cs, $"DROP TABLESPACE IF EXISTS {Name}");
        await Execute(cs, $"DROP ROLE IF EXISTS {Owner}");
    }

    private static string TablespaceLocation()
    {
        var value = Environment.GetEnvironmentVariable("BLUETUSK_TEST_TABLESPACE_LOCATION");
        return string.IsNullOrWhiteSpace(value)
            ? "/srv/postgresql/bluetusk_archive_space"
            : value;
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLUETUSK_TEST_TABLESPACE_LOCATION")))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_TABLESPACE_LOCATION is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(value)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }

    private sealed class InitialContext(DbContextOptions<InitialContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Configure(modelBuilder, altered: false);
    }

    private sealed class AlteredContext(DbContextOptions<AlteredContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Configure(modelBuilder, altered: true);
    }

    private sealed class RelocatedContext(DbContextOptions<RelocatedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            Configure(modelBuilder, altered: false, relocated: true);
    }

    private sealed class EmptyContext(DbContextOptions<EmptyContext> options) : DbContext(options);

    private sealed class Probe
    {
        public int Id { get; set; }
    }
}

#pragma warning restore EF1001
