using Microsoft.EntityFrameworkCore;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class QueryTranslationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Core_model_uses_PostgreSQL_store_types()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(Blog))!;

        Assert.Equal("integer", entityType.FindProperty(nameof(Blog.Id))!.GetColumnType());
        Assert.Equal("text", entityType.FindProperty(nameof(Blog.Name))!.GetColumnType());
        Assert.Equal("boolean", entityType.FindProperty(nameof(Blog.IsActive))!.GetColumnType());
    }

    [Fact]
    public void Core_query_translates_to_PostgreSQL_SQL()
    {
        using var context = CreateContext();

        var sql = context.Blogs
            .Where(blog => blog.IsActive && blog.Name.StartsWith("Blue"))
            .OrderBy(blog => blog.Id)
            .Select(blog => new { blog.Id, blog.Name })
            .Take(5)
            .ToQueryString();

        Assert.Contains("SELECT", sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"Blogs\" AS \"b\"", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"b\".\"Id\"", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
    }

    private static BlogContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BlogContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new BlogContext(options);
    }

    private sealed class BlogContext(DbContextOptions<BlogContext> options) : DbContext(options)
    {
        public DbSet<Blog> Blogs => Set<Blog>();
    }

    private sealed class Blog
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; }
    }
}
