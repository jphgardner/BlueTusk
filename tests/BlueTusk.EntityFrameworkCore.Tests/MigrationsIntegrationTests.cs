using BlueTusk.Client;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class MigrationsIntegrationTests
{
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
