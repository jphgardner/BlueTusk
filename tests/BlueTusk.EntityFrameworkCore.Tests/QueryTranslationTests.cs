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
        Assert.Equal("character varying(64)", entityType.FindProperty(nameof(Blog.Name))!.GetColumnType());
        Assert.Equal("boolean", entityType.FindProperty(nameof(Blog.IsActive))!.GetColumnType());
        Assert.Equal("smallint", entityType.FindProperty(nameof(Blog.SmallNumber))!.GetColumnType());
        Assert.Equal("bigint", entityType.FindProperty(nameof(Blog.LargeNumber))!.GetColumnType());
        Assert.Equal("real", entityType.FindProperty(nameof(Blog.Ratio))!.GetColumnType());
        Assert.Equal("double precision", entityType.FindProperty(nameof(Blog.Measurement))!.GetColumnType());
        Assert.Equal("numeric(18,4)", entityType.FindProperty(nameof(Blog.Amount))!.GetColumnType());
        Assert.Equal("bytea", entityType.FindProperty(nameof(Blog.Payload))!.GetColumnType());
        Assert.Equal("uuid", entityType.FindProperty(nameof(Blog.Token))!.GetColumnType());
        Assert.Equal("timestamp without time zone", entityType.FindProperty(nameof(Blog.CreatedAt))!.GetColumnType());
        Assert.Equal("timestamp with time zone", entityType.FindProperty(nameof(Blog.PublishedAt))!.GetColumnType());
        Assert.Equal("date", entityType.FindProperty(nameof(Blog.PublishDate))!.GetColumnType());
        Assert.Equal("time without time zone", entityType.FindProperty(nameof(Blog.PublishTime))!.GetColumnType());
        Assert.Equal("interval", entityType.FindProperty(nameof(Blog.Duration))!.GetColumnType());
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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Blog>().Property(blog => blog.Name).HasMaxLength(64);
            modelBuilder.Entity<Blog>().Property(blog => blog.Amount).HasPrecision(18, 4);
        }
    }

    private sealed class Blog
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; }

        public short SmallNumber { get; set; }

        public long LargeNumber { get; set; }

        public float Ratio { get; set; }

        public double Measurement { get; set; }

        public decimal Amount { get; set; }

        public byte[] Payload { get; set; } = [];

        public Guid Token { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTimeOffset PublishedAt { get; set; }

        public DateOnly PublishDate { get; set; }

        public TimeOnly PublishTime { get; set; }

        public TimeSpan Duration { get; set; }
    }
}
