using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Graphs;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PropertyGraphQueryIntegrationTests
{
    [Fact]
    public async Task Typed_graph_query_materializes_parameters_and_composes_with_relational_queries()
    {
        await using var context = CreateContext(GetConnectionString());
        await context.Database.OpenConnectionAsync();
        var connection = Assert.IsType<BlueTuskConnection>(context.Database.GetDbConnection());

        if (connection.SupportsSqlPgq is not true)
        {
            var exception = Assert.Throws<BlueTuskGraphTranslationException>(
                () => CreateGraphQuery(context, sourceId: 1));
            Assert.Contains("require PostgreSQL 19", exception.Message, StringComparison.Ordinal);
            return;
        }

        await ExecuteAsync(
            connection,
            """
            CREATE TEMP TABLE bluetusk_ef_graph_people (
                id int4 PRIMARY KEY,
                name text NOT NULL);
            CREATE TEMP TABLE bluetusk_ef_graph_friendships (
                id int4 PRIMARY KEY,
                from_id int4 NOT NULL REFERENCES bluetusk_ef_graph_people (id),
                to_id int4 NOT NULL REFERENCES bluetusk_ef_graph_people (id));
            INSERT INTO bluetusk_ef_graph_people VALUES
                (1, 'Ada'), (2, 'Grace'), (3, 'Linus');
            INSERT INTO bluetusk_ef_graph_friendships VALUES
                (10, 1, 2), (11, 1, 3);
            CREATE TEMP PROPERTY GRAPH bluetusk_ef_graph
                VERTEX TABLES (
                    bluetusk_ef_graph_people AS people
                    KEY (id)
                    LABEL person PROPERTIES (id AS "Id", name AS "Name"))
                EDGE TABLES (
                    bluetusk_ef_graph_friendships AS friendships
                    KEY (id)
                    SOURCE KEY (from_id) REFERENCES people (id)
                    DESTINATION KEY (to_id) REFERENCES people (id)
                    LABEL knows PROPERTIES (
                        id AS "Id",
                        from_id AS "FromPersonId",
                        to_id AS "ToPersonId"));
            """);

        try
        {
            var query = CreateGraphQuery(context, sourceId: 1)
                .Where(result => result.TargetName == "Grace")
                .OrderBy(result => result.TargetName)
                .Take(1);

            var result = Assert.Single(await query.ToListAsync());
            Assert.Equal(1, result.SourceId);
            Assert.Equal("Ada", result.SourceName);
            Assert.Equal(10, result.RelationshipId);
            Assert.Equal(2, result.TargetId);
            Assert.Equal("Grace", result.TargetName);

            var joined = await CreateGraphQuery(context, sourceId: 1)
                .Join(
                    context.People.AsNoTracking(),
                    result => result.TargetId,
                    person => person.Id,
                    (result, person) => new { result.RelationshipId, person.Name })
                .OrderBy(item => item.RelationshipId)
                .ToListAsync();

            Assert.Equal(2, joined.Count);
            Assert.Equal(["Grace", "Linus"], joined.Select(item => item.Name));

            var trackedPerson = Assert.Single(
                await context.PropertyGraph("bluetusk_ef_graph")
                    .Match(pattern => pattern.Vertex<Person>("person"))
                    .Select<Person>(projection => projection
                        .Property<Person, int>("person", person => person.Id, result => result.Id)
                        .Property<Person, string>("person", person => person.Name, result => result.Name))
                    .Where(person => person.Id == 2)
                    .ToListAsync());
            Assert.Equal("Grace", trackedPerson.Name);
            Assert.Contains(
                context.ChangeTracker.Entries<Person>(),
                entry => entry.Entity == trackedPerson && entry.State == EntityState.Unchanged);
        }
        finally
        {
            await ExecuteAsync(connection, "DROP PROPERTY GRAPH IF EXISTS bluetusk_ef_graph");
        }
    }

    private static IQueryable<FriendResult> CreateGraphQuery(GraphContext context, int sourceId) =>
        context.PropertyGraph("bluetusk_ef_graph")
            .Match(pattern => pattern
                .Vertex<Person>("source", person => person.Id == sourceId)
                .Outgoing<Friendship>("relationship")
                .Vertex<Person>("target"))
            .Select<FriendResult>(projection => projection
                .Property<Person, int>("source", person => person.Id, result => result.SourceId)
                .Property<Person, string>("source", person => person.Name, result => result.SourceName)
                .Property<Friendship, int>("relationship", edge => edge.Id, result => result.RelationshipId)
                .Property<Person, int>("target", person => person.Id, result => result.TargetId)
                .Property<Person, string>("target", person => person.Name, result => result.TargetName));

    private static GraphContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<GraphContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new GraphContext(options);
    }

    private static async Task ExecuteAsync(BlueTuskConnection connection, string sql)
    {
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
            Pooling = false,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }

    private sealed class GraphContext(DbContextOptions<GraphContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Person>(entity =>
            {
                entity.ToTable("bluetusk_ef_graph_people");
                entity.HasKey(person => person.Id);
                entity.Property(person => person.Id).HasColumnName("id");
                entity.Property(person => person.Name).HasColumnName("name");
            });
            modelBuilder.Entity<Friendship>(entity =>
            {
                entity.ToTable("bluetusk_ef_graph_friendships");
                entity.HasKey(friendship => friendship.Id);
                entity.Property(friendship => friendship.Id).HasColumnName("id");
                entity.Property(friendship => friendship.FromPersonId).HasColumnName("from_id");
                entity.Property(friendship => friendship.ToPersonId).HasColumnName("to_id");
            });
            modelBuilder.HasBlueTuskPropertyGraph(
                "bluetusk_ef_graph",
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
                });
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
