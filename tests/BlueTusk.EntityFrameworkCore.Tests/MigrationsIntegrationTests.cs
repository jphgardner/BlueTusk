using BlueTusk.Client;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class MigrationsIntegrationTests
{
    [Fact]
    public async Task Database_migrate_applies_and_reverts_a_migration_on_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_migration_lifecycle\"");
        await ExecuteNonQueryAsync(connectionString, "DROP SEQUENCE IF EXISTS \"ef_migration_sequence\"");
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"__EFMigrationsHistory\"");

        try
        {
            var options = new DbContextOptionsBuilder<MigrationLifecycleContext>()
                .UseBlueTusk(connectionString)
                .Options;
            await using var context = new MigrationLifecycleContext(options);
            var migrator = context.GetService<IMigrator>();

            var idempotentScript = migrator.GenerateScript(
                fromMigration: null,
                toMigration: null,
                MigrationsSqlGenerationOptions.Idempotent);
            Assert.Contains("IF NOT EXISTS", idempotentScript, StringComparison.Ordinal);
            await ExecuteNonQueryAsync(connectionString, idempotentScript);
            await ExecuteNonQueryAsync(connectionString, idempotentScript);

            await context.Database.MigrateAsync();
            await context.Database.MigrateAsync();

            var repository = context.GetService<IHistoryRepository>();
            var applied = await repository.GetAppliedMigrationsAsync(CancellationToken.None);
            Assert.Equal(2, applied.Count);
            Assert.Contains(applied, row => row.MigrationId == MigrationLifecycleContext.InitialMigrationId);
            Assert.Contains(applied, row => row.MigrationId == MigrationLifecycleContext.ExpandedMigrationId);
            Assert.True(await RelationExistsAsync(connectionString, "ef_migration_lifecycle"));
            Assert.True(await RelationExistsAsync(connectionString, "ef_migration_sequence"));
            Assert.True(await ColumnExistsAsync(connectionString, "ef_migration_lifecycle", "DisplayName"));
            Assert.True(await ColumnExistsAsync(connectionString, "ef_migration_lifecycle", "Score"));

            await migrator.MigrateAsync(MigrationLifecycleContext.InitialMigrationId);

            applied = await repository.GetAppliedMigrationsAsync(CancellationToken.None);
            Assert.Equal(MigrationLifecycleContext.InitialMigrationId, Assert.Single(applied).MigrationId);
            Assert.False(await RelationExistsAsync(connectionString, "ef_migration_sequence"));
            Assert.True(await ColumnExistsAsync(connectionString, "ef_migration_lifecycle", "Name"));
            Assert.False(await ColumnExistsAsync(connectionString, "ef_migration_lifecycle", "DisplayName"));
            Assert.False(await ColumnExistsAsync(connectionString, "ef_migration_lifecycle", "Score"));

            await migrator.MigrateAsync(Migration.InitialDatabase);

            Assert.Empty(await repository.GetAppliedMigrationsAsync(CancellationToken.None));
            Assert.False(await RelationExistsAsync(connectionString, "ef_migration_lifecycle"));
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_migration_lifecycle\"");
            await ExecuteNonQueryAsync(connectionString, "DROP SEQUENCE IF EXISTS \"ef_migration_sequence\"");
            await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"__EFMigrationsHistory\"");
        }
    }

    [Fact]
    public async Task History_repository_round_trips_migrations_on_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"__EFMigrationsHistory\"");

        try
        {
            await using var context = CreateContext(connectionString);
            var repository = context.GetService<IHistoryRepository>();

            Assert.False(await repository.ExistsAsync(CancellationToken.None));

            var createScript = repository.GetCreateIfNotExistsScript();
            await ExecuteNonQueryAsync(connectionString, createScript);
            await ExecuteNonQueryAsync(connectionString, createScript);
            Assert.True(await repository.ExistsAsync(CancellationToken.None));

            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await using var migrationLock = await repository.AcquireDatabaseLockAsync(CancellationToken.None);
                await transaction.CommitAsync();
            }

            var row = new HistoryRow("202607310001_Initial", "10.0.10");
            await ExecuteNonQueryAsync(connectionString, repository.GetInsertScript(row));

            var applied = Assert.Single(await repository.GetAppliedMigrationsAsync(CancellationToken.None));
            Assert.Equal(row.MigrationId, applied.MigrationId);
            Assert.Equal(row.ProductVersion, applied.ProductVersion);

            await ExecuteNonQueryAsync(connectionString, repository.GetDeleteScript(row.MigrationId));
            Assert.Empty(await repository.GetAppliedMigrationsAsync(CancellationToken.None));
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"__EFMigrationsHistory\"");
        }
    }

    [Fact]
    public async Task Generated_create_table_commands_execute_on_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await DropTablesAsync(connectionString);

        try
        {
            await using (var context = CreateContext(connectionString))
            {
                await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();

                var parent = new Parent { Name = "migration-parent" };
                parent.Children.Add(new Child { Amount = 12.34m });
                context.Add(parent);
                Assert.Equal(2, await context.SaveChangesAsync());
                Assert.True(parent.Id > 0);
                Assert.True(parent.Children[0].Id > 0);
            }

            await using (var context = CreateContext(connectionString))
            {
                var parent = await context.Parents.Include(entity => entity.Children).SingleAsync();
                Assert.Equal("migration-parent", parent.Name);
                Assert.Equal(12.34m, Assert.Single(parent.Children).Amount);
            }
        }
        finally
        {
            await DropTablesAsync(connectionString);
        }
    }

    private static MigrationContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MigrationContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new MigrationContext(options);
    }

    private static async Task DropTablesAsync(string connectionString)
    {
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_migration_children\"");
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_migration_parents\"");
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<bool> RelationExistsAsync(string connectionString, string relationName)
        => await ExecuteBooleanScalarAsync(
            connectionString,
            $"SELECT to_regclass('{relationName}') IS NOT NULL");

    private static async Task<bool> ColumnExistsAsync(
        string connectionString,
        string tableName,
        string columnName)
        => await ExecuteBooleanScalarAsync(
            connectionString,
            $"SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = '{tableName}' AND column_name = '{columnName}')");

    private static async Task<bool> ExecuteBooleanScalarAsync(string connectionString, string sql)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None) is true;
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

    private sealed class MigrationContext(DbContextOptions<MigrationContext> options) : DbContext(options)
    {
        public DbSet<Parent> Parents => Set<Parent>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var parent = modelBuilder.Entity<Parent>();
            parent.ToTable("ef_migration_parents");
            parent.Property(entity => entity.Name).HasMaxLength(80);

            var child = modelBuilder.Entity<Child>();
            child.ToTable("ef_migration_children");
            child.Property(entity => entity.Amount).HasPrecision(12, 2);
        }
    }

    private sealed class Parent
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<Child> Children { get; } = [];
    }

    private sealed class Child
    {
        public int Id { get; set; }

        public int ParentId { get; set; }

        public Parent Parent { get; set; } = null!;

        public decimal Amount { get; set; }
    }
}

[DbContext(typeof(MigrationLifecycleContext))]
[Migration(MigrationLifecycleContext.InitialMigrationId)]
internal sealed class MigrationLifecycleInitial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ef_migration_lifecycle",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_ef_migration_lifecycle", entity => entity.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "ef_migration_lifecycle");
}

[DbContext(typeof(MigrationLifecycleContext))]
[Migration(MigrationLifecycleContext.ExpandedMigrationId)]
internal sealed class MigrationLifecycleExpanded : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Score",
            table: "ef_migration_lifecycle",
            type: "integer",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.RenameColumn(
            name: "Name",
            table: "ef_migration_lifecycle",
            newName: "DisplayName");
        migrationBuilder.AlterColumn<string>(
            name: "DisplayName",
            table: "ef_migration_lifecycle",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text");
        migrationBuilder.CreateSequence<long>(
            name: "ef_migration_sequence",
            startValue: 100L);
        migrationBuilder.CreateIndex(
            name: "IX_ef_migration_lifecycle_DisplayName",
            table: "ef_migration_lifecycle",
            column: "DisplayName",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ef_migration_lifecycle_DisplayName",
            table: "ef_migration_lifecycle");
        migrationBuilder.DropSequence(name: "ef_migration_sequence");
        migrationBuilder.AlterColumn<string>(
            name: "DisplayName",
            table: "ef_migration_lifecycle",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(100)",
            oldMaxLength: 100);
        migrationBuilder.RenameColumn(
            name: "DisplayName",
            table: "ef_migration_lifecycle",
            newName: "Name");
        migrationBuilder.DropColumn(
            name: "Score",
            table: "ef_migration_lifecycle");
    }
}

internal sealed class MigrationLifecycleContext(DbContextOptions<MigrationLifecycleContext> options)
    : DbContext(options)
{
    public const string InitialMigrationId = "20260731000100_Initial";
    public const string ExpandedMigrationId = "20260731000200_Expanded";
}
