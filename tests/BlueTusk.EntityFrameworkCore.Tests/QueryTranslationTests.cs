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

    [Fact]
    public void Parameterised_string_operations_translate_to_PostgreSQL_functions()
    {
        using var context = CreateContext();
        var prefix = "Blue%";
        var fragment = "Tusk_";

        var sql = context.Blogs
            .Where(blog =>
                blog.Name.StartsWith(prefix)
                && blog.Name.Contains(fragment)
                && blog.Name.Length > 3
                && blog.Name.ToLowerInvariant().Replace("tusk", "db").Substring(0, 2) == "bl")
            .ToQueryString();

        Assert.Contains("left(", sql, StringComparison.Ordinal);
        Assert.Contains("strpos(", sql, StringComparison.Ordinal);
        Assert.Contains("char_length(", sql, StringComparison.Ordinal);
        Assert.Contains("lower(", sql, StringComparison.Ordinal);
        Assert.Contains("replace(", sql, StringComparison.Ordinal);
        Assert.Contains("substring(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Joins_grouping_aggregates_and_paging_translate()
    {
        using var context = CreateContext();

        var groupedSql = context.Blogs
            .GroupBy(blog => blog.IsActive)
            .Select(group => new
            {
                group.Key,
                Count = group.Count(),
                MaximumId = group.Max(blog => blog.Id),
            })
            .ToQueryString();

        var joinSql = context.Blogs
            .Join(
                context.Blogs,
                left => left.Id,
                right => right.Id,
                (left, right) => new { left.Id, RightName = right.Name })
            .Skip(2)
            .Take(3)
            .ToQueryString();

        Assert.Contains("GROUP BY", groupedSql, StringComparison.Ordinal);
        Assert.Contains("count(*)", groupedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max(", groupedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INNER JOIN", joinSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", joinSql, StringComparison.Ordinal);
        Assert.Contains("OFFSET", joinSql, StringComparison.Ordinal);
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
