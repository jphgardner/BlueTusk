using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Graphs;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PropertyGraphQueryIntegrationTests
{
    [Theory]
    [InlineData(BlueTuskPropertyGraphBackend.Relational)]
    [InlineData(BlueTuskPropertyGraphBackend.NativeSqlPgq)]
    public async Task Typed_graph_query_materializes_parameters_and_composes_with_relational_queries(
        BlueTuskPropertyGraphBackend backend)
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection, backend);
        var native = backend == BlueTuskPropertyGraphBackend.NativeSqlPgq;

        if (native && connection.SupportsSqlPgq is not true)
        {
            var exception = await Assert.ThrowsAsync<BlueTuskGraphTranslationException>(
                () => CreateGraphQuery(context, sourceId: 1).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Contains("does not support", exception.Message, StringComparison.Ordinal);
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
                (10, 1, 2), (11, 1, 3), (12, 2, 3);
            """);
        if (native)
        {
            await ExecuteAsync(connection,
            """
            CREATE TEMP PROPERTY GRAPH bluetusk_ef_graph
                VERTEX TABLES (
                    bluetusk_ef_graph_people AS people
                    KEY (id)
                    LABEL person PROPERTIES (id AS "Id", name AS "Name")
                    LABEL contact PROPERTIES (id AS "Id", name AS "Name"))
                EDGE TABLES (
                    bluetusk_ef_graph_friendships AS friendships
                    KEY (id)
                    SOURCE KEY (from_id) REFERENCES people (id)
                    DESTINATION KEY (to_id) REFERENCES people (id)
                    LABEL knows PROPERTIES (
                        id AS "Id",
                        from_id AS "FromPersonId",
                        to_id AS "ToPersonId")
                    LABEL connected PROPERTIES (
                        id AS "Id",
                        from_id AS "FromPersonId",
                        to_id AS "ToPersonId"));
            """);
        }

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

            // Exercise a warm graph command with different values, including
            // two independently bound roots in the same EF command.
            var rebound = await CreateGraphQuery(context, sourceId: 2)
                .Where(result => result.TargetName == "Linus")
                .OrderBy(result => result.TargetName)
                .Take(1)
                .ToListAsync();
            Assert.Equal(12, Assert.Single(rebound).RelationshipId);

            var composed = await CreateGraphQuery(context, sourceId: 1)
                .Concat(CreateGraphQuery(context, sourceId: 2))
                .Where(result => result.TargetName == "Linus")
                .OrderBy(result => result.RelationshipId)
                .Select(result => result.RelationshipId)
                .ToListAsync();
            Assert.Equal([11, 12], composed);

            var sharedRoot = CreateGraphQuery(context, sourceId: 2);
            Assert.Equal([12, 12], await sharedRoot.Concat(sharedRoot)
                .Select(result => result.RelationshipId).ToListAsync());

            long wideSource = 2;
            await using var regularCommand = sharedRoot.CreateDbCommand();
            await using var wideCommand = CreateGraphQuery(context, person => person.Id == wideSource).CreateDbCommand();
            Assert.Equal(regularCommand.CommandText, wideCommand.CommandText);
            Assert.Equal(DbType.Int32, Assert.Single(regularCommand.Parameters.Cast<DbParameter>()).DbType);
            Assert.Equal(DbType.Int64, Assert.Single(wideCommand.Parameters.Cast<DbParameter>()).DbType);

            var trackedPerson = Assert.Single(
                await context.PropertyGraph("bluetusk_ef_graph")
                    .Match(pattern => pattern
                        .Vertex<Person>("person")
                        .LabelsAnyOf("person"))
                    .Select<Person>(projection => projection
                        .Property<Person, int>("person", person => person.Id, result => result.Id)
                        .Property<Person, string>("person", person => person.Name, result => result.Name))
                    .Where(person => person.Id == 2)
                    .ToListAsync());
            Assert.Equal("Grace", trackedPerson.Name);
            Assert.Contains(
                context.ChangeTracker.Entries<Person>(),
                entry => entry.Entity == trackedPerson && entry.State == EntityState.Unchanged);

            var undirected = await context.PropertyGraph("bluetusk_ef_graph")
                .Match(pattern => pattern
                    .Vertex<Person>("left", person => person.Id == 2)
                    .LabelsAnyOf("person", "contact")
                    .Undirected<Friendship>("relationship")
                    .LabelsAnyOf("knows", "connected")
                    .Vertex<Person>("right")
                    .LabelsAnyOf("person", "contact"))
                .Select<PathEndpointResult>(projection => projection
                    .Property<Person, int>("left", person => person.Id, result => result.SourceId)
                    .Property<Person, int>("right", person => person.Id, result => result.TargetId)
                    .Property<Person, string>("right", person => person.Name, result => result.TargetName))
                .OrderBy(result => result.TargetId)
                .ToListAsync();
            Assert.Equal([1, 3], undirected.Select(static result => result.TargetId));

            var bounded = await context.PropertyGraph("bluetusk_ef_graph")
                .Match(pattern => pattern
                    .Vertex<Person>("source", person => person.Id == 1)
                    .LabelsAnyOf("person")
                    .OutgoingPath<Friendship>("path", 1, 2)
                    .LabelsAnyOf("knows")
                    .Vertex<Person>("target", person => person.Id == 3)
                    .LabelsAnyOf("person"))
                .Select<PathEndpointResult>(projection => projection
                    .Property<Person, int>("source", person => person.Id, result => result.SourceId)
                    .Property<Person, int>("target", person => person.Id, result => result.TargetId)
                    .Property<Person, string>("target", person => person.Name, result => result.TargetName))
                .OrderBy(result => result.TargetId)
                .ToListAsync();
            Assert.Equal([3, 3], bounded.Select(static result => result.TargetId));

            if (!native)
            {
                await AssertOrderedBranchLimitsAsync(context, connection);
            }
        }
        finally
        {
            if (native)
            {
                await ExecuteAsync(connection, "DROP PROPERTY GRAPH IF EXISTS bluetusk_ef_graph");
            }
        }
    }

    private static async Task AssertOrderedBranchLimitsAsync(GraphContext context, BlueTuskConnection connection)
    {
        // Parallel edges, a self-loop, and a cycle make multiple walks sort
        // to the same terminal value across the one- and two-hop branches.
        await ExecuteAsync(connection,
            "INSERT INTO bluetusk_ef_graph_friendships VALUES (13, 1, 2), (14, 2, 1), (15, 2, 2)");
        const string referenceSql = """
            SELECT to_id AS "TargetId" FROM pg_temp.bluetusk_ef_graph_friendships WHERE from_id = 1
            UNION ALL
            SELECT second.to_id AS "TargetId"
            FROM pg_temp.bluetusk_ef_graph_friendships AS first
            JOIN pg_temp.bluetusk_ef_graph_friendships AS second ON first.to_id = second.from_id
            WHERE first.from_id = 1
            """;
        var expected = context.Database.SqlQueryRaw<TerminalResult>(referenceSql);
        var actual = context.PropertyGraph("bluetusk_ef_graph")
            .Match(pattern => pattern
                .Vertex<Person>("source", person => person.Id == 1)
                .LabelsAnyOf("person")
                .OutgoingPath<Friendship>("path", 1, 2)
                .LabelsAnyOf("knows")
                .Vertex<Person>("target")
                .LabelsAnyOf("person"))
            .Select<TerminalResult>(projection => projection
                .Property<Person, int>("target", person => person.Id, result => result.TargetId));

        foreach (var count in new[] { 1, 3, 8 })
        {
            var first = actual.OrderBy(result => result.TargetId).Take(count);
            await using var command = first.CreateDbCommand();
            Assert.Equal(2, command.CommandText.Split("ORDER BY \"TargetId\" ASC LIMIT ", StringSplitOptions.None).Length - 1);
            Assert.Equal(await expected.OrderBy(result => result.TargetId).Take(count)
                .Select(result => result.TargetId).ToListAsync(),
                (await first.ToListAsync()).Select(result => result.TargetId));
            Assert.Equal(await expected.OrderByDescending(result => result.TargetId).Take(count)
                .Select(result => result.TargetId).ToListAsync(),
                (await actual.OrderByDescending(result => result.TargetId).Take(count).ToListAsync())
                .Select(result => result.TargetId));
        }

        var filtered = actual.Where(result => result.TargetId > 2).OrderBy(result => result.TargetId).Take(1);
        await using var filteredCommand = filtered.CreateDbCommand();
        Assert.DoesNotContain("ORDER BY \"TargetId\" ASC LIMIT ", filteredCommand.CommandText, StringComparison.Ordinal);
        Assert.Equal(3, Assert.Single(await filtered.ToListAsync()).TargetId);
    }

    private static IQueryable<FriendResult> CreateGraphQuery(GraphContext context, int sourceId) =>
        CreateGraphQuery(context, person => person.Id == sourceId);

    private static IQueryable<FriendResult> CreateGraphQuery(
        GraphContext context, Expression<Func<Person, bool>> sourcePredicate) =>
        context.PropertyGraph("bluetusk_ef_graph")
            .Match(pattern => pattern
                .Vertex<Person>("source", sourcePredicate)
                .LabelsAnyOf("person")
                .Outgoing<Friendship>("relationship")
                .LabelsAnyOf("knows")
                .Vertex<Person>("target")
                .LabelsAnyOf("person"))
            .Select<FriendResult>(projection => projection
                .Property<Person, int>("source", person => person.Id, result => result.SourceId)
                .Property<Person, string>("source", person => person.Name, result => result.SourceName)
                .Property<Friendship, int>("relationship", edge => edge.Id, result => result.RelationshipId)
                .Property<Person, int>("target", person => person.Id, result => result.TargetId)
                .Property<Person, string>("target", person => person.Name, result => result.TargetName));

    private static GraphContext CreateContext(BlueTuskConnection connection, BlueTuskPropertyGraphBackend backend)
    {
        var options = new DbContextOptionsBuilder<GraphContext>()
            .UseBlueTusk(connection, blueTuskOptionsAction: options => options.UseGraphCompatibility(backend))
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
                entity.ToTable("bluetusk_ef_graph_people", "pg_temp");
                entity.HasKey(person => person.Id);
                entity.Property(person => person.Id).HasColumnName("id");
                entity.Property(person => person.Name).HasColumnName("name");
            });
            modelBuilder.Entity<Friendship>(entity =>
            {
                entity.ToTable("bluetusk_ef_graph_friendships", "pg_temp");
                entity.HasKey(friendship => friendship.Id);
                entity.Property(friendship => friendship.Id).HasColumnName("id");
                entity.Property(friendship => friendship.FromPersonId).HasColumnName("from_id");
                entity.Property(friendship => friendship.ToPersonId).HasColumnName("to_id");
            });
            modelBuilder.HasPropertyGraph(
                "bluetusk_ef_graph",
                graph =>
                {
                    graph.Vertex<Person>("people", vertex => vertex
                        .HasLabel("person")
                        .HasLabel("contact")
                        .HasKey(person => person.Id)
                        .Properties(person => new { person.Id, person.Name }));
                    graph.Edge<Friendship>("friendships", edge => edge
                        .HasLabel("knows")
                        .HasLabel("connected")
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

    private sealed class PathEndpointResult
    {
        public int SourceId { get; set; }

        public int TargetId { get; set; }

        public string TargetName { get; set; } = string.Empty;
    }

    private sealed class TerminalResult
    {
        public int TargetId { get; set; }
    }
}
