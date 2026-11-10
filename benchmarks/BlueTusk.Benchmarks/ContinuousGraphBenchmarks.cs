using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using BlueTusk.ContinuousGraph;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.Live;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.Benchmarks;

/// <summary>Traversal and maintenance over a portable PostgreSQL graph.</summary>
[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
[JsonExporterAttribute.Full]
[Orderer(SummaryOrderPolicy.Declared)]
public partial class ContinuousGraphBenchmarks : IAsyncDisposable
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
    private ContinuousGraphIncrementalSession<GraphPath, int> _scoped = null!;
    private ContinuousGraphIncrementalSession<GraphPath, int> _trusted = null!;
    private ChangeTransactionDelivery _update = null!;
    private BlueTuskCommand _prepared = null!;
    private int _disposed;

    [Params(1_000, 100_000, 1_000_000)]
    public int EdgeCount { get; set; }

    [Params(10, 100, 1_000)]
    public int TopN { get; set; }

    [GlobalSetup]
    public Task Setup() => SetupAsync(null);

    private async Task SetupAsync(ScopedReadObserver? observer)
    {
        _dataSource = BlueTuskDataSource.Create(GetConnectionString());
        _administration = await _dataSource.OpenConnectionAsync();
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
            SELECT value - 1,
                CASE WHEN value <= 1001 OR value % 2 = 0 THEN 1 ELSE value - 1 END,
                value
            FROM generate_series(2, {EdgeCount + 1}) AS value;
            CREATE INDEX ON {FriendshipsTable} (from_id, to_id);
            CREATE INDEX ON {FriendshipsTable} (to_id, from_id);
            ANALYZE {PeopleTable};
            ANALYZE {FriendshipsTable};
            """);
        if (observer is not null)
        {
            await InstallCapturePolicyAsync();
        }

        _contextFactory = new GraphContextFactory(_dataSource, observer);
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
        var incrementalOptions = new ContinuousGraphIncrementalOptions<GraphPath, int>
        {
            ResultOrdering = Comparer<GraphPath>.Create(static (left, right) =>
                left.DestinationId.CompareTo(right.DestinationId)),
            KeyOrdering = Comparer<int>.Default,
            MaximumRepairInterval = TimeSpan.FromHours(1),
        };
        _scoped = _plan.CreateIncrementalSession(_arguments, _securityScope, incrementalOptions);
        var updatedEdgeId = EdgeCount + 2;
        _trusted = _plan.CreateAutomaticIncrementalSession(_arguments, _securityScope, incrementalOptions,
            resultLimit: null, trustedProjector: new BenchmarkProjector(_plan.Fingerprint, _securityScope));
        await InitializeAsync(_scoped);
        await InitializeAsync(_trusted);
        await ExecuteAsync($"UPDATE {FriendshipsTable} SET id = {updatedEdgeId} WHERE id = 1");
        _update = CreateUpdate(updatedEdgeId);
        if (await ScopedUpdateAsync() != ContinuousGraphMaintenanceTier.AuthoritativeDelta ||
            await TrustedUpdateAsync() != ContinuousGraphMaintenanceTier.TrustedCdcDelta)
        {
            throw new InvalidOperationException("Portable Graph benchmark did not exercise both delta tiers.");
        }

        _prepared = new BlueTuskCommand(
            $"""
            SELECT e.id, v.id, v.name
            FROM {PeopleTable} AS anchor
            JOIN {FriendshipsTable} AS e ON e.to_id = anchor.id
            JOIN {PeopleTable} AS v ON v.id = e.from_id
            WHERE anchor.id = @source
            ORDER BY v.id
            LIMIT @top
            """, _administration);
        _prepared.Parameters.Add(new BlueTuskParameter<int>(EdgeCount + 1) { ParameterName = "source" });
        _prepared.Parameters.Add(new BlueTuskParameter<int>(TopN) { ParameterName = "top" });
        await _prepared.PrepareAsync();

        if (Environment.GetEnvironmentVariable("BLUETUSK_GRAPH_PLAN_DIRECTORY") is { Length: > 0 } directory)
        {
            await CapturePlansAsync(directory);
        }
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

    [Benchmark]
    public async Task<ContinuousGraphMaintenanceTier> ScopedUpdateAsync() =>
        await EvaluateUpdateAsync(_scoped);

    [Benchmark]
    public async Task<ContinuousGraphMaintenanceTier> TrustedUpdateAsync() =>
        await EvaluateUpdateAsync(_trusted);

    [Benchmark]
    public async Task<ContinuousGraphMaintenanceTier> FullRepairUpdateAsync()
    {
        await using var proposal = await _scoped.PrepareRepairAsync("benchmark-forced-full-requery");
        return proposal.Evaluation.MaintenanceTier;
    }

    [Benchmark]
    public async Task<int> PreparedSelectiveTraversalAsync()
    {
        await using var reader = await _prepared.ExecuteReaderAsync();
        var rows = new List<GraphPath>();
        while (await reader.ReadAsync())
        {
            rows.Add(new GraphPath
            {
                RelationshipId = reader.GetInt32(0),
                DestinationId = reader.GetInt32(1),
                DestinationName = reader.GetString(2),
            });
        }

        return rows.Count;
    }

    [Benchmark]
    public async Task<int> TypedSelectiveTraversalAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        return (await Traversal(context, EdgeCount + 1, BlueTuskGraphEdgeDirection.Incoming)
            .ToListAsync()).Count;
    }

    [Benchmark]
    public async Task<int> TypedBoundedTraversalAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        return (await BoundedTraversal(context, 1).ToListAsync()).Count;
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

        if (_update is not null)
        {
            await _update.DisposeAsync();
        }

        if (_scoped is not null)
        {
            await _scoped.DisposeAsync();
        }

        if (_trusted is not null)
        {
            await _trusted.DisposeAsync();
        }

        if (_prepared is not null)
        {
            await _prepared.DisposeAsync();
        }

        if (_administration is not null)
        {
            try
            {
                if (_administration.State is System.Data.ConnectionState.Open)
                {
                    await DropGraphObjectsAsync();
                    if (_capturePolicyInstalled)
                    {
                        await ExecuteAsync($"DROP ROLE {CaptureRole}");
                    }
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
            (context, arguments) => Traversal(context, arguments.Get<int>("sourceId"),
                BlueTuskGraphEdgeDirection.Outgoing),
            result => result.DestinationId,
            GraphPathComparer.Instance);

    private async Task DropGraphObjectsAsync()
    {
        await ExecuteAsync($"DROP TABLE IF EXISTS {FriendshipsTable}");
        await ExecuteAsync($"DROP TABLE IF EXISTS {PeopleTable}");
    }

    private IQueryable<GraphPath> Traversal(
        GraphContext context, int sourceId, BlueTuskGraphEdgeDirection direction)
    {
        var match = context.PropertyGraph(GraphName).Match(pattern =>
        {
            pattern.Vertex<GraphPerson>("source", person => person.Id == sourceId);
            _ = direction switch
            {
                BlueTuskGraphEdgeDirection.Incoming => pattern.Incoming<GraphFriendship>("relationship"),
                BlueTuskGraphEdgeDirection.Undirected => pattern.Undirected<GraphFriendship>("relationship"),
                _ => pattern.Outgoing<GraphFriendship>("relationship"),
            };

            return pattern.Vertex<GraphPerson>("destination");
        });
        return match.Select<GraphPath>(projection => projection
                .Property<GraphFriendship, int>("relationship", edge => edge.Id, row => row.RelationshipId)
                .Property<GraphPerson, int>("destination", person => person.Id, row => row.DestinationId)
                .Property<GraphPerson, string>("destination", person => person.Name, row => row.DestinationName))
            .OrderBy(result => result.DestinationId)
            .Take(TopN);
    }

    private IQueryable<GraphEndpoint> BoundedTraversal(GraphContext context, int sourceId) =>
        context.PropertyGraph(GraphName)
            .Match(pattern => pattern
                .Vertex<GraphPerson>("source", person => person.Id == sourceId)
                .OutgoingPath<GraphFriendship>("relationship", 1, 2)
                .Vertex<GraphPerson>("destination"))
            .Select<GraphEndpoint>(projection => projection
                .Property<GraphPerson, int>("destination", person => person.Id, row => row.Id))
            .OrderBy(result => result.Id)
            .Take(TopN);

    private async Task CapturePlansAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await using var context = _contextFactory.CreateDbContext();
        await context.Database.OpenConnectionAsync();
        foreach (var (label, source, direction, bounded) in new[]
        {
            ("selective", EdgeCount + 1, BlueTuskGraphEdgeDirection.Incoming, false),
            ("selective-undirected", EdgeCount + 1, BlueTuskGraphEdgeDirection.Undirected, false),
            ("high-degree", 1, BlueTuskGraphEdgeDirection.Outgoing, false),
            ("bounded", 1, BlueTuskGraphEdgeDirection.Outgoing, true),
        })
        {
            IQueryable query = bounded ? BoundedTraversal(context, source) : Traversal(context, source, direction);
            await using var command = query.CreateDbCommand();
            command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + command.CommandText;
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException("PostgreSQL returned no traversal plan.");
            }

            var plan = reader.GetString(0);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{label}-{EdgeCount}-{TopN}.json"), plan);
        }
    }

    private static async Task InitializeAsync(ContinuousGraphIncrementalSession<GraphPath, int> session)
    {
        await using var initial = await session.PrepareInitialAsync();
        await initial.CommitAsync();
    }

    private async Task<ContinuousGraphMaintenanceTier> EvaluateUpdateAsync(
        ContinuousGraphIncrementalSession<GraphPath, int> session)
    {
        // Dispose the proposal without committing so each measurement starts from the same snapshot.
        await using var proposal = await session.PrepareTransactionAsync(_update.Transaction);
        return proposal.Evaluation.MaintenanceTier;
    }

    private static ChangeTransactionDelivery CreateUpdate(int updatedEdgeId)
    {
        var source = new ChangeSourceIdentity("benchmark", "graph", "slot", "publication");
        var position = new BlueTuskLogSequenceNumber(1);
        var table = new ChangeTable(1, "public", FriendshipsTable, 'f',
            [
                new ChangeColumn(0, "id", 23, -1, IsKey: true),
                new ChangeColumn(1, "from_id", 23, -1, IsKey: false),
                new ChangeColumn(2, "to_id", 23, -1, IsKey: false),
            ]);
        var sourceId = ChangeColumnValue.FromValue("1"u8.ToArray(), ChangeValueEncoding.Text);
        var targetId = ChangeColumnValue.FromValue("2"u8.ToArray(), ChangeValueEncoding.Text);
        var newId = ChangeColumnValue.FromValue(System.Text.Encoding.UTF8.GetBytes(
            updatedEdgeId.ToString(System.Globalization.CultureInfo.InvariantCulture)), ChangeValueEncoding.Text);
        var before = new ChangeRow(table, [sourceId, sourceId, targetId]);
        var after = new ChangeRow(table, [newId, sourceId, targetId]);
        return ChangeDeliveryTestFactory.CreateCommitted(source, 1, position,
            [new UpdateChange(new ChangeId(source, position, 1, 0), before, after, new ChangedColumnSet(true, [0]))]);
    }

    private sealed class BenchmarkProjector(string fingerprint, LiveSecurityScope scope)
        : IContinuousGraphCdcProjector<GraphPath, int>
    {
        public ContinuousGraphCdcTrustContract TrustContract { get; } = new(fingerprint,
            HasCompleteOldAndNewValues: true, HasExactChangedColumns: true,
            HasSufficientReplicaIdentity: true, EnforcesSecurityScope: true);

        public async ValueTask<ContinuousGraphIncrementalResult<GraphPath, int>> ProjectAsync(
            ChangeTransaction transaction, LiveQueryExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.SecurityScope != scope)
            {
                return ContinuousGraphIncrementalResult<GraphPath, int>.RequiresRepair("benchmark-scope-mismatch");
            }

            await foreach (var change in transaction.Changes.WithCancellation(cancellationToken))
            {
                if (change is not UpdateChange update || update.NewRow.Table.Name != FriendshipsTable)
                {
                    continue;
                }

                var row = update.NewRow;
                var columns = row.Table.Columns;
                if (ChangeValueDecoders.Decode<int>(columns[1], row[1]) != context.Arguments.Get<int>("sourceId"))
                {
                    break;
                }

                var target = ChangeValueDecoders.Decode<int>(columns[2], row[2]);
                return ContinuousGraphIncrementalResult<GraphPath, int>.Exact([target],
                    [new GraphPath
                    {
                        DestinationId = target,
                        RelationshipId = ChangeValueDecoders.Decode<int>(columns[0], row[0]),
                        DestinationName = FormattableString.Invariant($"person-{target}"),
                    }]);
            }

            return ContinuousGraphIncrementalResult<GraphPath, int>.RequiresRepair("benchmark-update-not-found");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _administration.CreateCommand();
        command.CommandText = sql;
        // GlobalSetup deliberately creates the largest graph outside the measured
        // operation. It can exceed the provider's production-safe command timeout
        // on slower runners without saying anything about benchmark performance.
        command.CommandTimeout = 0;
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

    private sealed class GraphContextFactory : IDbContextFactory<GraphContext>
    {
        private readonly DbContextOptions<GraphContext> _options;
        private readonly ScopedReadObserver? _observer;

        public GraphContextFactory(BlueTuskDataSource dataSource, ScopedReadObserver? observer)
        {
            _observer = observer;
            var options = new DbContextOptionsBuilder<GraphContext>().UseBlueTusk(dataSource);
            if (observer is not null)
            {
                options.AddInterceptors(observer);
            }

            _options = options.Options;
        }

        public GraphContext CreateDbContext() => new(_options);

        public Task<GraphContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            _observer is null
                ? Task.FromResult(CreateDbContext())
                : CreateAuthorizedContextAsync(_observer, cancellationToken);

        private async Task<GraphContext> CreateAuthorizedContextAsync(
            ScopedReadObserver observer, CancellationToken cancellationToken)
        {
            var context = CreateDbContext();
            try
            {
                observer.ContextCreated = Stopwatch.GetTimestamp();
                await context.Database.OpenConnectionAsync(cancellationToken);
                observer.ConnectionOpened = Stopwatch.GetTimestamp();
                await AuthorizeCaptureAsync((BlueTuskConnection)context.Database.GetDbConnection(), cancellationToken);
                observer.Authorized = Stopwatch.GetTimestamp();
                return context;
            }
            catch
            {
                await context.DisposeAsync();
                throw;
            }
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

    private sealed class GraphEndpoint
    {
        public int Id { get; set; }
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
