using BlueTusk.Data;
using BlueTusk.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class CrudIntegrationTests
{
    [Fact]
    public async Task EF_Core_executes_CRUD_against_PostgreSQL()
    {
        var connectionString = GetConnectionString();
        await RecreateTableAsync(connectionString);

        try
        {
            var generatedId = 0;
            await using (var context = CreateContext(connectionString))
            {
                var blog = new Blog { Name = "BlueTusk", IsActive = true };
                context.Blogs.Add(blog);
                Assert.Equal(1, await context.SaveChangesAsync());
                Assert.True(blog.Id > 0);
                generatedId = blog.Id;
            }

            await using (var context = CreateContext(connectionString))
            {
                var prefix = "Blue";
                var blog = await context.Blogs.SingleAsync(candidate =>
                    candidate.Id == generatedId
                    && candidate.Name.StartsWith(prefix)
                    && candidate.Name.Contains("Tusk")
                    && candidate.Name.Length > 3);
                Assert.Equal("BlueTusk", blog.Name);
                Assert.True(blog.IsActive);
                Assert.Equal("server-default", blog.ServerDefault);
                Assert.Equal(8, blog.NameLength);
                Assert.Equal(1, blog.Version);

                blog.Name = "BlueTusk EF";
                Assert.Equal(1, await context.SaveChangesAsync());
                Assert.Equal(11, blog.NameLength);
            }

            await using (var context = CreateContext(connectionString))
            {
                var staleBlog = await context.Blogs.SingleAsync();
                await ExecuteNonQueryAsync(
                    connectionString,
                    $"UPDATE \"ef_crud_blogs\" SET \"Version\" = 2 WHERE \"Id\" = {staleBlog.Id}");
                staleBlog.Name = "Conflicting update";

                _ = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                    () => context.SaveChangesAsync());
            }

            await using (var context = CreateContext(connectionString))
            {
                var blog = await context.Blogs.SingleAsync();
                Assert.Equal("BlueTusk EF", blog.Name);
                Assert.Equal(2, blog.Version);

                context.Blogs.Remove(blog);
                Assert.Equal(1, await context.SaveChangesAsync());
                Assert.Equal(0, await context.Blogs.CountAsync());
            }

            await using (var context = CreateContext(connectionString))
            {
                await using var transaction = await context.Database.BeginTransactionAsync();
                context.Blogs.Add(new Blog { Name = "Rolled back", IsActive = false });
                Assert.Equal(1, await context.SaveChangesAsync());
                await transaction.RollbackAsync();
            }

            await using (var context = CreateContext(connectionString))
            {
                Assert.Equal(0, await context.Blogs.CountAsync());
            }
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_crud_blogs\"");
        }
    }

    [Fact]
    public async Task EF_Core_commits_transactions_and_rolls_back_to_savepoints()
    {
        var connectionString = GetConnectionString();
        await RecreateTableAsync(connectionString);

        try
        {
            await using (var context = CreateContext(connectionString))
            {
                await using var transaction = await context.Database.BeginTransactionAsync();
                context.Blogs.Add(new Blog { Name = "Committed", IsActive = true });
                _ = await context.SaveChangesAsync();

                await transaction.CreateSavepointAsync("before_second_blog");
                context.Blogs.Add(new Blog { Name = "Rolled back to savepoint", IsActive = false });
                _ = await context.SaveChangesAsync();
                await transaction.RollbackToSavepointAsync("before_second_blog");
                await transaction.CommitAsync();
            }

            await using var verificationContext = CreateContext(connectionString);
            var blog = await verificationContext.Blogs.SingleAsync();
            Assert.Equal("Committed", blog.Name);
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_crud_blogs\"");
        }
    }

    private static BlogContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<BlogContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new BlogContext(options);
    }

    private static async Task RecreateTableAsync(string connectionString)
    {
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_crud_blogs\"");
        await ExecuteNonQueryAsync(
            connectionString,
            """
            CREATE TABLE "ef_crud_blogs" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "Name" text NOT NULL,
                "IsActive" boolean NOT NULL,
                "ServerDefault" text NOT NULL DEFAULT 'server-default',
                "NameLength" integer GENERATED ALWAYS AS (char_length("Name")) STORED,
                "Version" integer NOT NULL DEFAULT 1)
            """);
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

    private sealed class BlogContext(DbContextOptions<BlogContext> options) : DbContext(options)
    {
        public DbSet<Blog> Blogs => Set<Blog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => ConfigureBlog(modelBuilder.Entity<Blog>());

        private static void ConfigureBlog(EntityTypeBuilder<Blog> blog)
        {
            blog.ToTable("ef_crud_blogs");
            blog.HasKey(entity => entity.Id);
            blog.Property(entity => entity.Id).ValueGeneratedOnAdd();
            blog.Property(entity => entity.Name).IsRequired();
            blog.Property(entity => entity.ServerDefault)
                .HasDefaultValueSql("'server-default'")
                .ValueGeneratedOnAdd();
            blog.Property(entity => entity.NameLength)
                .HasComputedColumnSql("char_length(\"Name\")", stored: true);
            blog.Property(entity => entity.Version)
                .HasDefaultValue(1)
                .IsConcurrencyToken();
        }
    }

    private sealed class Blog
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; }

        public string? ServerDefault { get; set; }

        public int NameLength { get; set; }

        public int Version { get; set; }
    }
}
