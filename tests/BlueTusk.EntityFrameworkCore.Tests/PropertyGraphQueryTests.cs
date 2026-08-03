using BlueTusk.EntityFrameworkCore.Graphs;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PropertyGraphQueryTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Typed_match_generates_parameterized_composable_graph_table_SQL()
    {
        using var context = CreateContext();
        var sourceId = 7;

        var sql = context.PropertyGraph("social", "graphs")
            .Match(pattern => pattern
                .Vertex<Person>("source", person => person.Id == sourceId)
                .Outgoing<Friendship>("relationship")
                .Vertex<Person>("target"))
            .Select<FriendResult>(projection => projection
                .Property<Person, int>("source", person => person.Id, result => result.SourceId)
                .Property<Person, string>("source", person => person.Name, result => result.SourceName)
                .Property<Friendship, int>("relationship", edge => edge.Id, result => result.RelationshipId)
                .Property<Person, int>("target", person => person.Id, result => result.TargetId)
                .Property<Person, string>("target", person => person.Name, result => result.TargetName))
            .Where(result => result.TargetName != "blocked")
            .OrderBy(result => result.TargetName)
            .Take(5)
            .ToQueryString();

        Assert.Contains("GRAPH_TABLE (\"graphs\".\"social\" MATCH", sql, StringComparison.Ordinal);
        Assert.Contains("(\"source\" IS \"person\")", sql, StringComparison.Ordinal);
        Assert.Contains("-[\"relationship\" IS \"knows\"]->", sql, StringComparison.Ordinal);
        Assert.Contains("(\"target\" IS \"person\")", sql, StringComparison.Ordinal);
        Assert.Contains("\"source\".\"Id\" AS \"SourceId\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"relationship\".\"Id\" AS \"RelationshipId\"", sql, StringComparison.Ordinal);
        Assert.Contains("__bluetusk_filter_0", sql, StringComparison.Ordinal);
        Assert.Contains("@p0", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"__bluetusk_filter_0\" = 7", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Incoming_match_reverses_the_edge_pattern()
    {
        using var context = CreateContext();

        var sql = context.PropertyGraph("social", "graphs")
            .Match(pattern => pattern
                .Vertex<Person>("target")
                .Incoming<Friendship>("relationship")
                .Vertex<Person>("source"))
            .Select<FriendResult>(projection => projection
                .Property<Person, string>("source", person => person.Name, result => result.SourceName)
                .Property<Person, string>("target", person => person.Name, result => result.TargetName))
            .ToQueryString();

        Assert.Contains(
            "(\"target\" IS \"person\")<-[\"relationship\" IS \"knows\"]-(\"source\" IS \"person\")",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Graph_results_support_grouping_and_mapped_entity_materialization_SQL()
    {
        using var context = CreateContext();

        var match = context.PropertyGraph("social", "graphs")
            .Match(pattern => pattern.Vertex<Person>("person"));
        var graphResults = match.Select<FriendResult>(projection => projection
            .Property<Person, int>("person", person => person.Id, result => result.TargetId)
            .Property<Person, string>("person", person => person.Name, result => result.TargetName));
        var groupedSql = graphResults
            .GroupBy(result => result.TargetName)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToQueryString();

        var entitySql = match.Select<Person>(projection => projection
                .Property<Person, int>("person", person => person.Id, result => result.Id)
                .Property<Person, string>("person", person => person.Name, result => result.Name))
            .ToQueryString();

        Assert.Contains("GROUP BY", groupedSql, StringComparison.Ordinal);
        Assert.Contains("count(*)", groupedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS \"id\"", entitySql, StringComparison.Ordinal);
        Assert.Contains("AS \"name\"", entitySql, StringComparison.Ordinal);
    }

    [Fact]
    public void Match_rejects_invalid_pattern_structure_and_duplicate_variables()
    {
        using var context = CreateContext();
        var root = context.PropertyGraph("social", "graphs");

        Assert.Throws<BlueTuskGraphTranslationException>(
            () => root.Match(pattern => pattern.Outgoing<Friendship>("relationship")));
        Assert.Throws<BlueTuskGraphTranslationException>(
            () => root.Match(pattern => pattern.Vertex<Person>("person").Outgoing<Friendship>("edge")));
        Assert.Throws<BlueTuskGraphTranslationException>(
            () => root.Match(pattern => pattern
                .Vertex<Person>("person")
                .Outgoing<Friendship>("edge")
                .Vertex<Person>("person")));
    }

    [Fact]
    public void Translation_reports_unsupported_predicates_and_projection_variables()
    {
        using var context = CreateContext();

        var unsupported = context.PropertyGraph("social", "graphs")
            .Match(pattern => pattern.Vertex<Person>("person", person => person.Name.StartsWith("Al")));
        var predicateException = Assert.Throws<BlueTuskGraphTranslationException>(
            () => unsupported.Select<FriendResult>(projection => projection
                .Property<Person, string>("person", person => person.Name, result => result.SourceName)));
        Assert.Contains("direct property comparisons", predicateException.Message, StringComparison.Ordinal);

        var match = context.PropertyGraph("social", "graphs")
            .Match(pattern => pattern.Vertex<Person>("person"));
        var projectionException = Assert.Throws<BlueTuskGraphTranslationException>(
            () => match.Select<FriendResult>(projection => projection
                .Property<Person, string>("missing", person => person.Name, result => result.SourceName)));
        Assert.Contains("unknown graph variable 'missing'", projectionException.Message, StringComparison.Ordinal);

        var entityProjectionException = Assert.Throws<BlueTuskGraphTranslationException>(
            () => match.Select<Person>(projection => projection
                .Property<Person, int>("person", person => person.Id, result => result.Id)));
        Assert.Contains("must project every mapped property", entityProjectionException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Query_root_requires_an_unambiguous_configured_graph()
    {
        using var context = CreateContext();

        var missing = Assert.Throws<BlueTuskGraphTranslationException>(
            () => context.PropertyGraph("missing"));
        Assert.Contains("not configured", missing.Message, StringComparison.Ordinal);
    }

    private static GraphContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GraphContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new GraphContext(options);
    }

    private sealed class GraphContext(DbContextOptions<GraphContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Person>(entity =>
            {
                entity.ToTable("people", "graphs");
                entity.HasKey(person => person.Id);
                entity.Property(person => person.Id).HasColumnName("id");
                entity.Property(person => person.Name).HasColumnName("name");
            });
            modelBuilder.Entity<Friendship>(entity =>
            {
                entity.ToTable("friendships", "graphs");
                entity.HasKey(friendship => friendship.Id);
                entity.Property(friendship => friendship.Id).HasColumnName("id");
                entity.Property(friendship => friendship.FromPersonId).HasColumnName("from_id");
                entity.Property(friendship => friendship.ToPersonId).HasColumnName("to_id");
            });
            modelBuilder.HasBlueTuskPropertyGraph(
                "social",
                graph =>
                {
                    graph.Vertex<Person>("people", vertex => vertex
                        .HasLabel("person")
                        .HasKey(person => person.Id)
                        .Properties(person => new { person.Id, person.Name }));
                    graph.Edge<Friendship>("friendships", edge => edge
                        .HasLabel("knows")
                        .HasKey(friendship => friendship.Id)
                        .Properties(friendship => new
                        {
                            friendship.Id,
                            friendship.FromPersonId,
                            friendship.ToPersonId,
                        })
                        .HasSource<Person>(friendship => friendship.FromPersonId, person => person.Id)
                        .HasDestination<Person>(friendship => friendship.ToPersonId, person => person.Id));
                },
                schema: "graphs");
        }
    }

    private sealed class Person
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class Friendship
    {
        public int Id { get; set; }

        public int FromPersonId { get; set; }

        public int ToPersonId { get; set; }
    }

    private sealed class FriendResult
    {
        public int SourceId { get; set; }

        public string SourceName { get; set; } = string.Empty;

        public int RelationshipId { get; set; }

        public int TargetId { get; set; }

        public string TargetName { get; set; } = string.Empty;
    }
}
