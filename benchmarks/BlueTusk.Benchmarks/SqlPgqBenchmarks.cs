using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.Benchmarks;

/// <summary>Prepared raw SQL/PGQ and typed EF graph traversal over the same live graph.</summary>
[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
[Orderer(SummaryOrderPolicy.Declared)]
public class SqlPgqBenchmarks : IAsyncDisposable
{
    private BlueTuskDataSource _dataSource = null!;
    private BlueTuskConnection _connection = null!;
    private BlueTuskCommand _preparedTraversal = null!;
    private DbContextOptions<GraphContext> _options = null!;
    private int _disposed;

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = GetConnectionString();
        _dataSource = BlueTuskDataSource.Create(connectionString);
        _connection = await _dataSource.OpenConnectionAsync();
        if (_connection.ServerCapabilities is not { SupportsSqlPgq: true })
        {
            throw new InvalidOperationException(
                $"SQL/PGQ benchmarks require PostgreSQL 19 or later; connected to {_connection.ServerVersion}.");
        }

        _options = new DbContextOptionsBuilder<GraphContext>()
            .UseBlueTusk(_dataSource)
            .Options;
        await DropGraphObjectsAsync();
        await ExecuteAsync(
            """
            CREATE TABLE bluetusk_benchmark_graph_people (
                id int4 PRIMARY KEY,
                name text NOT NULL)
            """);
        await ExecuteAsync(
            """
            CREATE TABLE bluetusk_benchmark_graph_friendships (
                id int4 PRIMARY KEY,
                from_id int4 NOT NULL REFERENCES bluetusk_benchmark_graph_people (id),
                to_id int4 NOT NULL REFERENCES bluetusk_benchmark_graph_people (id))
            """);
        await ExecuteAsync(
            """
            INSERT INTO bluetusk_benchmark_graph_people
            SELECT value, 'person-' || value::text
            FROM generate_series(1, 1000) AS value
            """);
        await ExecuteAsync(
            """
            INSERT INTO bluetusk_benchmark_graph_friendships
            SELECT value - 1, 1, value
            FROM generate_series(2, 1000) AS value
            """);
        await ExecuteAsync(
            """
            CREATE PROPERTY GRAPH bluetusk_benchmark_graph
                VERTEX TABLES (
                    bluetusk_benchmark_graph_people AS people
                    KEY (id)
                    LABEL person PROPERTIES (id AS "Id", name AS "Name"))
                EDGE TABLES (
                    bluetusk_benchmark_graph_friendships AS friendships
                    KEY (id)
                    SOURCE KEY (from_id) REFERENCES people (id)
                    DESTINATION KEY (to_id) REFERENCES people (id)
                    LABEL knows PROPERTIES (
                        id AS "Id",
                        from_id AS "FromPersonId",
                        to_id AS "ToPersonId"))
            """);

        _preparedTraversal = new BlueTuskCommand(
            """
            SELECT destination_id
            FROM GRAPH_TABLE (
                bluetusk_benchmark_graph
                MATCH (source IS person)-[IS knows]->(destination IS person)
                COLUMNS (
                    source."Id" AS source_id,
                    destination."Id" AS destination_id))
            WHERE source_id = $1::int4
            ORDER BY destination_id
            """,
            _connection);
        _preparedTraversal.Parameters.Add(new BlueTuskParameter<int>(1));
        await _preparedTraversal.PrepareAsync();

        _ = await RawPreparedGraphTraversalAsync();
        _ = await TypedEfGraphTraversalAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    [Benchmark]
    public async Task<long> RawPreparedGraphTraversalAsync()
    {
        await using var reader = await _preparedTraversal.ExecuteReaderAsync();
        long sum = 0;
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt32(0);
        }

        return sum;
    }

    [Benchmark]
    public async Task<int> TypedEfGraphTraversalAsync()
    {
        await using var context = new GraphContext(_options);
        var results = await CreateGraphQuery(context, sourceId: 1).ToListAsync();
        return results.Count;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_preparedTraversal is not null)
        {
            await _preparedTraversal.DisposeAsync();
        }

        if (_connection is not null)
        {
            try
            {
                if (_connection.State == System.Data.ConnectionState.Open)
                {
                    await DropGraphObjectsAsync();
                }
            }
            finally
            {
                await _connection.DisposeAsync();
            }
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private static IQueryable<GraphTraversal> CreateGraphQuery(GraphContext context, int sourceId) =>
        context.PropertyGraph("bluetusk_benchmark_graph")
            .Match(pattern => pattern
                .Vertex<GraphPerson>("source", person => person.Id == sourceId)
                .Outgoing<GraphFriendship>("relationship")
                .Vertex<GraphPerson>("destination"))
            .Select<GraphTraversal>(projection => projection
                .Property<GraphPerson, int>("source", person => person.Id, result => result.SourceId)
                .Property<GraphFriendship, int>(
                    "relationship",
                    friendship => friendship.Id,
                    result => result.RelationshipId)
                .Property<GraphPerson, int>(
                    "destination",
                    person => person.Id,
                    result => result.DestinationId));

    private async Task DropGraphObjectsAsync()
    {
        await ExecuteAsync("DROP PROPERTY GRAPH IF EXISTS bluetusk_benchmark_graph");
        await ExecuteAsync("DROP TABLE IF EXISTS bluetusk_benchmark_graph_friendships");
        await ExecuteAsync("DROP TABLE IF EXISTS bluetusk_benchmark_graph_people");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = new BlueTuskCommand(sql, _connection);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ProviderComparisonBenchmarks.ConnectionStringEnvironmentVariable);
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException(
                $"{ProviderComparisonBenchmarks.ConnectionStringEnvironmentVariable} must be configured.")
            : connectionString;
    }

    private sealed class GraphContext(DbContextOptions<GraphContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GraphPerson>(entity =>
            {
                entity.ToTable("bluetusk_benchmark_graph_people");
                entity.HasKey(person => person.Id);
                entity.Property(person => person.Id).HasColumnName("id");
                entity.Property(person => person.Name).HasColumnName("name");
            });
            modelBuilder.Entity<GraphFriendship>(entity =>
            {
                entity.ToTable("bluetusk_benchmark_graph_friendships");
                entity.HasKey(friendship => friendship.Id);
                entity.Property(friendship => friendship.Id).HasColumnName("id");
                entity.Property(friendship => friendship.FromPersonId).HasColumnName("from_id");
                entity.Property(friendship => friendship.ToPersonId).HasColumnName("to_id");
            });
            modelBuilder.HasPropertyGraph(
                "bluetusk_benchmark_graph",
                graph =>
                {
                    graph.Vertex<GraphPerson>("people", vertex => vertex
                        .HasLabel("person")
                        .HasKey(person => person.Id)
                        .Properties(person => new { person.Id, person.Name }));
                    graph.Edge<GraphFriendship>("friendships", edge => edge
                        .HasLabel("knows")
                        .HasKey(friendship => friendship.Id)
                        .Properties(friendship => new
                        {
                            friendship.Id,
                            friendship.FromPersonId,
                            friendship.ToPersonId,
                        })
                        .HasSource<GraphPerson>(
                            friendship => friendship.FromPersonId,
                            person => person.Id)
                        .HasDestination<GraphPerson>(
                            friendship => friendship.ToPersonId,
                            person => person.Id));
                });
        }
    }

    private sealed class GraphPerson
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class GraphFriendship
    {
        public int Id { get; set; }

        public int FromPersonId { get; set; }

        public int ToPersonId { get; set; }
    }

    private sealed class GraphTraversal
    {
        public int SourceId { get; set; }

        public int RelationshipId { get; set; }

        public int DestinationId { get; set; }
    }
}
