using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using BlueTusk.ContinuousGraph;
using BlueTusk.Data;
using BlueTusk.Live;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.Benchmarks;

/// <summary>Registration, authoritative requery, and invalidation/diff over a live PostgreSQL 19 graph.</summary>
[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
[Orderer(SummaryOrderPolicy.Declared)]
public class ContinuousGraphBenchmarks : IAsyncDisposable
{
    private const string GraphName = "bluetusk_benchmark_continuous_graph";
    private const string PeopleTable = "bluetusk_benchmark_continuous_people";
    private const string FriendshipsTable = "bluetusk_benchmark_continuous_friendships";
    private BlueTuskDataSource _dataSource = null!;
    private BlueTuskConnection _administration = null!;
    private GraphContextFactory _contextFactory = null!;
    private ContinuousGraphQueryDefinition<GraphContext, GraphPath, int> _definition = null!;
    private ContinuousGraphQueryPlan<GraphPath, int> _plan = null!;
    private LiveQueryArguments _arguments = null!;
    private LiveSecurityScope _securityScope = null!;
    private AdvancingInvalidationLog _invalidations = null!;
    private LiveQuerySession<GraphPath, int> _session = null!;
    private int _disposed;

    [Params(1_000, 100_000, 1_000_000)]
    public int EdgeCount { get; set; }

    [Params(10, 100, 1_000)]
    public int TopN { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _dataSource = BlueTuskDataSource.Create(GetConnectionString());
        _administration = await _dataSource.OpenConnectionAsync();
        if (_administration.SupportsSqlPgq is not true)
        {
            throw new InvalidOperationException(
                $"Continuous Graph benchmarks require PostgreSQL 19 SQL/PGQ; " +
                $"connected to {_administration.ServerVersion}.");
        }

        await DropGraphObjectsAsync();
        await ExecuteAsync(
            $"""
            CREATE TABLE {PeopleTable} (
                id int4 PRIMARY KEY,
                name text NOT NULL);
            CREATE TABLE {FriendshipsTable} (
                id int4 PRIMARY KEY,
                from_id int4 NOT NULL REFERENCES {PeopleTable} (id),
                to_id int4 NOT NULL REFERENCES {PeopleTable} (id));
            INSERT INTO {PeopleTable}
            SELECT value, 'person-' || value::text
            FROM generate_series(1, {EdgeCount + 1}) AS value;
            INSERT INTO {FriendshipsTable}
            SELECT value - 1, 1, value
            FROM generate_series(2, {EdgeCount + 1}) AS value;
            CREATE PROPERTY GRAPH {GraphName}
                VERTEX TABLES (
                    {PeopleTable} AS people
                    KEY (id)
                    LABEL person PROPERTIES (id AS "Id", name AS "Name"))
                EDGE TABLES (
                    {FriendshipsTable} AS friendships
                    KEY (id)
                    SOURCE KEY (from_id) REFERENCES people (id)
                    DESTINATION KEY (to_id) REFERENCES people (id)
                    LABEL knows PROPERTIES (
                        id AS "Id",
                        from_id AS "FromPersonId",
                        to_id AS "ToPersonId"));
            """);
        _contextFactory = new GraphContextFactory(_dataSource);
        _definition = CreateDefinition();
        _plan = await ContinuousGraphQueryCompiler.CompileAsync(
            _contextFactory,
            _definition);
        _arguments = _plan.Bind(
            new Dictionary<string, object?> { ["sourceId"] = 1 });
        _securityScope = new LiveSecurityScope("benchmark", "v1");
        _invalidations = new AdvancingInvalidationLog();
        _session = _plan.CreateSession(
            _arguments,
            _securityScope,
            _invalidations);
        _ = await _session.StartAsync();
        _ = await AuthoritativeGraphRequeryAsync();
        _ = await AffectedGraphRefreshAndDiffAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    [Benchmark]
    public async Task<string> CompileGraphRegistrationAsync()
    {
        var plan = await ContinuousGraphQueryCompiler.CompileAsync(
            _contextFactory,
            _definition);
        return plan.Fingerprint;
    }

    [Benchmark]
    public async Task<int> AuthoritativeGraphRequeryAsync()
    {
        var rows = await _plan.LivePlan.ExecuteAsync(
            new LiveQueryExecutionContext(_arguments, _securityScope),
            CancellationToken.None);
        return rows.Count;
    }

    [Benchmark]
    public async Task<long> AffectedGraphRefreshAndDiffAsync()
    {
        _invalidations.Advance();
        var batch = await _session.RefreshToCurrentAsync();
        return (batch?.Snapshot.Rows.Count ?? 0) +
            _session.Status.AuthoritativeQueryCount;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        if (_administration is not null)
        {
            try
            {
                if (_administration.State is System.Data.ConnectionState.Open)
                {
                    await DropGraphObjectsAsync();
                }
            }
            finally
            {
                await _administration.DisposeAsync();
            }
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private ContinuousGraphQueryDefinition<GraphContext, GraphPath, int>
        CreateDefinition() =>
        new(
            "continuous-graph-benchmark",
            "benchmark",
            "1",
            GraphName,
            graphSchema: null,
            ["people", "friendships"],
            [new LiveQueryParameter("sourceId", typeof(int))],
            new Dictionary<string, object?> { ["sourceId"] = 1 },
            TopN,
            (context, arguments) =>
            {
                var sourceId = arguments.Get<int>("sourceId");
                return context.PropertyGraph(GraphName)
                    .Match(pattern => pattern
                        .Vertex<GraphPerson>("source", person => person.Id == sourceId)
                        .Outgoing<GraphFriendship>("relationship")
                        .Vertex<GraphPerson>("destination"))
                    .Select<GraphPath>(projection => projection
                        .Property<GraphFriendship, int>(
                            "relationship",
                            friendship => friendship.Id,
                            result => result.RelationshipId)
                        .Property<GraphPerson, int>(
                            "destination",
                            person => person.Id,
                            result => result.DestinationId)
                        .Property<GraphPerson, string>(
                            "destination",
                            person => person.Name,
                            result => result.DestinationName))
                    .OrderBy(result => result.DestinationId)
                    .Take(TopN);
            },
            result => result.DestinationId,
            GraphPathComparer.Instance);

    private async Task DropGraphObjectsAsync()
    {
        await ExecuteAsync($"DROP PROPERTY GRAPH IF EXISTS {GraphName}");
        await ExecuteAsync($"DROP TABLE IF EXISTS {FriendshipsTable}");
        await ExecuteAsync($"DROP TABLE IF EXISTS {PeopleTable}");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _administration.CreateCommand();
        command.CommandText = sql;
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

    private sealed class GraphContextFactory(BlueTuskDataSource dataSource) :
        IDbContextFactory<GraphContext>
    {
        public GraphContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<GraphContext>()
                .UseBlueTusk(dataSource)
                .Options;
            return new GraphContext(options);
        }
    }

    private sealed class GraphContext(DbContextOptions<GraphContext> options) :
        DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GraphPerson>(entity =>
            {
                entity.ToTable(PeopleTable);
                entity.HasKey(person => person.Id);
                entity.Property(person => person.Id).HasColumnName("id");
                entity.Property(person => person.Name).HasColumnName("name");
            });
            modelBuilder.Entity<GraphFriendship>(entity =>
            {
                entity.ToTable(FriendshipsTable);
                entity.HasKey(friendship => friendship.Id);
                entity.Property(friendship => friendship.Id).HasColumnName("id");
                entity.Property(friendship => friendship.FromPersonId).HasColumnName("from_id");
                entity.Property(friendship => friendship.ToPersonId).HasColumnName("to_id");
            });
            modelBuilder.HasPropertyGraph(
                GraphName,
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

    private sealed class GraphPath
    {
        public int RelationshipId { get; set; }

        public int DestinationId { get; set; }

        public string DestinationName { get; set; } = string.Empty;
    }

    private sealed class GraphPathComparer : IEqualityComparer<GraphPath>
    {
        public static GraphPathComparer Instance { get; } = new();

        public bool Equals(GraphPath? x, GraphPath? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             x.RelationshipId == y.RelationshipId &&
             x.DestinationId == y.DestinationId &&
             string.Equals(x.DestinationName, y.DestinationName, StringComparison.Ordinal));

        public int GetHashCode(GraphPath obj) =>
            HashCode.Combine(
                obj.RelationshipId,
                obj.DestinationId,
                obj.DestinationName);
    }

    private sealed class AdvancingInvalidationLog : ILiveInvalidationLog
    {
        private long _cursor;

        public void Advance() => Interlocked.Increment(ref _cursor);

        public ValueTask<LiveInvalidationCursor> GetCurrentCursorAsync(
            string databaseIdentity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new LiveInvalidationCursor(Interlocked.Read(ref _cursor)));
        }

        public ValueTask<bool> HasChangesAsync(
            string databaseIdentity,
            IReadOnlyCollection<LiveTableDependency> dependencies,
            LiveInvalidationCursor afterExclusive,
            LiveInvalidationCursor throughInclusive,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(throughInclusive > afterExclusive);
        }
    }
}
