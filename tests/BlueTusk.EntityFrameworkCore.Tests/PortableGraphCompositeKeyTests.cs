using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PortableGraphCompositeKeyTests
{
    [Fact]
    public async Task Quoted_composite_keys_preserve_direction_cycles_parallel_edges_and_self_loops()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING")
            ?? throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var command = new BlueTuskCommand(
            """
            CREATE TEMP TABLE "Vertex {graph}" (
                "tenant" int, "vertex""id" int,
                PRIMARY KEY ("tenant", "vertex""id"));
            CREATE TEMP TABLE "Edge {graph}" (
                "id" int PRIMARY KEY, "tenant" int NOT NULL, "source" int NOT NULL, "destination" int NOT NULL,
                FOREIGN KEY ("tenant", "source") REFERENCES "Vertex {graph}",
                FOREIGN KEY ("tenant", "destination") REFERENCES "Vertex {graph}");
            INSERT INTO "Vertex {graph}" VALUES (1, 1), (1, 2), (2, 1), (2, 2);
            INSERT INTO "Edge {graph}" VALUES (100, 1, 1, 1), (101, 1, 1, 2), (102, 1, 1, 2),
                (103, 1, 2, 1), (200, 2, 1, 2);
            """, connection))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var options = new DbContextOptionsBuilder<CompositeContext>().UseBlueTusk(connection).Options;
        await using var context = new CompositeContext(options);
        var tenant = 1;
        var start = 1;
        var root = context.PropertyGraph("quoted composite");

        var outgoing = root.Match(pattern => pattern
            .Vertex<Vertex>("source", vertex => vertex.Tenant == tenant && vertex.Id == start)
            .Outgoing<Edge>("edge").Vertex<Vertex>("target"));
        var incoming = root.Match(pattern => pattern
            .Vertex<Vertex>("source", vertex => vertex.Tenant == tenant && vertex.Id == start)
            .Incoming<Edge>("edge").Vertex<Vertex>("target"));
        var undirected = root.Match(pattern => pattern
            .Vertex<Vertex>("source", vertex => vertex.Tenant == tenant && vertex.Id == start)
            .Undirected<Edge>("edge").Vertex<Vertex>("target"));

        var outgoingIds = await EdgesAsync(outgoing);
        var incomingIds = await EdgesAsync(incoming);
        var undirectedIds = await EdgesAsync(undirected);
        Assert.Equal([100, 101, 102], outgoingIds);
        Assert.Equal([100, 103], incomingIds);
        Assert.Equal([100, 101, 102, 103], undirectedIds);

        var bounded = root.Match(pattern => pattern
            .Vertex<Vertex>("source", vertex => vertex.Tenant == tenant && vertex.Id == start)
            .OutgoingPath<Edge>("path", 1, 2).Vertex<Vertex>("target"))
            .Select<IdRow>(projection => projection.Property<Vertex, int>("target", vertex => vertex.Id, row => row.Id))
            .OrderBy(row => row.Id);
        var expected = await context.Database.SqlQueryRaw<int>(
            """
            SELECT destination AS "Value" FROM "Edge {{graph}}" WHERE tenant = 1 AND source = 1
            UNION ALL
            SELECT second.destination FROM "Edge {{graph}}" AS first JOIN "Edge {{graph}}" AS second
                ON first.tenant = second.tenant AND first.destination = second.source
            WHERE first.tenant = 1 AND first.source = 1
            """).OrderBy(id => id).ToListAsync(cancellationToken);
        Assert.Equal(expected, (await bounded.ToListAsync(cancellationToken)).Select(row => row.Id));

        async Task<int[]> EdgesAsync(BlueTusk.EntityFrameworkCore.Graphs.BlueTuskGraphMatch match) =>
            await match.Select<IdRow>(projection => projection.Property<Edge, int>("edge", edge => edge.Id, row => row.Id))
                .OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(cancellationToken);
    }

    private sealed class CompositeContext(DbContextOptions<CompositeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Vertex>(entity =>
            {
                entity.ToTable("Vertex {graph}", "pg_temp");
                entity.HasKey(vertex => new { vertex.Tenant, vertex.Id });
                entity.Property(vertex => vertex.Tenant).HasColumnName("tenant");
                entity.Property(vertex => vertex.Id).HasColumnName("vertex\"id");
            });
            modelBuilder.Entity<Edge>(entity =>
            {
                entity.ToTable("Edge {graph}", "pg_temp");
                entity.Property(edge => edge.Id).HasColumnName("id");
                entity.Property(edge => edge.Tenant).HasColumnName("tenant");
                entity.Property(edge => edge.Source).HasColumnName("source");
                entity.Property(edge => edge.Destination).HasColumnName("destination");
            });
            modelBuilder.HasPropertyGraph("quoted composite", graph => graph
                .Vertex<Vertex>("vertices", vertex => vertex.HasLabel("vertex")
                    .HasKey(row => new { row.Tenant, row.Id })
                    .Properties(row => new { row.Tenant, row.Id }))
                .Edge<Edge>("edges", edge => edge.HasLabel("edge")
                    .HasKey(row => row.Id)
                    .Properties(row => new { row.Id })
                    .HasSource<Vertex>(row => new { row.Tenant, row.Source }, row => new { row.Tenant, row.Id })
                    .HasDestination<Vertex>(row => new { row.Tenant, row.Destination }, row => new { row.Tenant, row.Id })));
        }
    }

    private sealed class Vertex
    {
        public int Tenant { get; set; }
        public int Id { get; set; }
    }

    private sealed class Edge
    {
        public int Id { get; set; }
        public int Tenant { get; set; }
        public int Source { get; set; }
        public int Destination { get; set; }
    }

    private sealed class IdRow
    {
        public int Id { get; set; }
    }
}
