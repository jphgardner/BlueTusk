using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.Live;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.ServiceTopology.Application;
using BlueTusk.ServiceTopology.Infrastructure;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.ContinuousGraph.Tests;

public sealed class ServiceTopologyGraphCanaryTests
{
    private const string Graph = "service_topology_graph";
    private const string AlphaRole = "bluetusk_topology_canary_alpha";
    private const string BetaRole = "bluetusk_topology_canary_beta";
    private const string ReplaySchema = "topology_graph_canary_replay";
    private static readonly LiveSecurityScope AlphaScope = new("tenant:alpha", "topology-canary-v1");
    private static readonly LiveSecurityScope BetaScope = new("tenant:beta", "topology-canary-v1");
    private static readonly ChangeSourceIdentity Source = new("topology-canary", "topology-canary", "topology-canary", "topology-canary");
    private static readonly ChangeTable Dependencies = new(1, "topology", "dependencies", 'f',
        [new ChangeColumn(0, "Id", 2950, -1, IsKey: true)]);

    [Fact]
    public async Task Service_topology_shadow_matches_application_and_excludes_other_tenants()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(token);
        await using var alpha = await fixture.OpenAuthorizedAsync(AlphaScope, IsolationLevel.ReadCommitted, token);
        var graph = alpha.PropertyGraph(Graph);
        var incoming = new BlueTuskGraphTraversalOptions
        {
            EdgeAliases = ["dependencies"],
            Direction = BlueTuskGraphEdgeDirection.Incoming,
        };
        var blastRadius = await graph.ReachableAsync(new BlueTuskGraphIdentity("services", fixture.Identity),
            incoming, token);
        var expected = await fixture.Service.BlastRadiusAsync("alpha", fixture.Identity, token);
        Assert.True(blastRadius.UsedServerEngine);
        Assert.False(blastRadius.BudgetExhausted);
        Assert.Equal(expected.Order(), blastRadius.Rows.Select(static row => (Guid) row.Vertex.Key[0]).Order());

        var path = await graph.ShortestPathAsync(new BlueTuskGraphIdentity("services", fixture.Billing),
            new BlueTuskGraphIdentity("services", fixture.Identity),
            cancellationToken: token);
        Assert.True(path.UsedServerEngine);
        Assert.Equal(await fixture.Service.FindPathAsync("alpha", fixture.Billing, fixture.Identity, token),
            path.Path!.Vertices.Select(static node => (Guid) node.Key[0]));

        // Simulate a legacy cross-tenant edge. The intermediate service remains invisible.
        await fixture.SetEdgeAsync(Guid.NewGuid(), fixture.Billing, fixture.Hidden, add: true, token);
        await fixture.SetEdgeAsync(Guid.NewGuid(), fixture.Hidden, fixture.Isolated, add: true, token);
        var blocked = await graph.ShortestPathAsync(new BlueTuskGraphIdentity("services", fixture.Billing),
            new BlueTuskGraphIdentity("services", fixture.Isolated),
            cancellationToken: token);
        Assert.True(blocked.UsedServerEngine);
        Assert.Null(blocked.Path);

        await using var beta = await fixture.OpenAuthorizedAsync(BetaScope, IsolationLevel.ReadCommitted, token);
        var otherTenant = await beta.PropertyGraph(Graph).ReachableAsync(
            new BlueTuskGraphIdentity("services", fixture.Identity), incoming, token);
        Assert.True(otherTenant.UsedServerEngine);
        Assert.Empty(otherTenant.Rows);

        await using var portable = await fixture.OpenAuthorizedAsync(AlphaScope, IsolationLevel.RepeatableRead, token);
        var rollback = await portable.PropertyGraph(Graph).ReachableAsync(
            new BlueTuskGraphIdentity("services", fixture.Identity),
            new BlueTuskGraphTraversalOptions { Direction = BlueTuskGraphEdgeDirection.Incoming, UseServerEngine = false }, token);
        Assert.False(rollback.UsedServerEngine);
        Assert.Equal(blastRadius.Rows.Select(static row => row.Vertex.Key[0]),
            rollback.Rows.Select(static row => row.Vertex.Key[0]));
    }

    [Fact]
    public async Task Native_topology_endurance_reconciles_authorized_live_replay()
    {
        var token = TestContext.Current.CancellationToken;
        var duration = TimeSpan.Parse(GetSetting("BLUETUSK_NATIVE_GRAPH_DURATION"), CultureInfo.InvariantCulture);
        var minimumEvaluations = int.Parse(GetSetting("BLUETUSK_NATIVE_GRAPH_MIN_EVALUATIONS"), CultureInfo.InvariantCulture);
        var reportPath = GetSetting("BLUETUSK_NATIVE_GRAPH_REPORT_PATH");
        Assert.True(duration > TimeSpan.Zero && minimumEvaluations >= 10);
        await using var fixture = await Fixture.CreateAsync(token);
        await using var dataSource = BlueTuskDataSource.Create(fixture.ConnectionString);
        var replay = new PostgreSqlLiveInvalidationStore(new PostgreSqlLiveStoreOptions
        {
            ControlDataSource = dataSource,
            ControlSchema = ReplaySchema,
            ReplayRetentionWindow = TimeSpan.FromDays(2),
        });
        await replay.InitializeAsync(token);

        var plan = ContinuousGraphQueryCompiler.CompileReachability(
            fixture.Database, "topology-impact", Source.DatabaseName, "v1", Graph, null,
            [new LiveQueryParameter("anchor", typeof(Guid))],
            static arguments => new BlueTuskGraphIdentity("services", arguments.Get<Guid>("anchor")),
            (execution, cancellation) => fixture.OpenAuthorizedAsync(
                execution.SecurityScope, IsolationLevel.ReadCommitted, cancellation),
            new BlueTuskGraphTraversalOptions
            {
                EdgeAliases = ["dependencies"],
                Direction = BlueTuskGraphEdgeDirection.Incoming,
            });
        var arguments = plan.Bind(new Dictionary<string, object?> { ["anchor"] = fixture.Identity });
        var options = new ContinuousGraphIncrementalOptions<BlueTuskGraphTraversalRow, string>
        {
            ResultOrdering = Comparer<BlueTuskGraphTraversalRow>.Create(
                static (left, right) => left.Hops.CompareTo(right.Hops)),
            KeyOrdering = StringComparer.Ordinal,
        };
        var sessionRestarts = 0;
        var revokedDeliveries = 0;
        var canceledDeliveries = 0;
        var latencies = new List<double>();
        var sequence = 0L;
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var completed = false;
        const int restartInterval = 1000;
        var edgeId = Guid.NewGuid();
        var expectedKey = JsonSerializer.Serialize(new object[] { "services", new[] { fixture.Isolated } });

        try
        {
            for (var epoch = 0; timer.Elapsed < duration || latencies.Count < minimumEvaluations; epoch++)
            {
                var session = plan.CreateIncrementalSession(arguments, AlphaScope, options);
                await using var consumer = new ContinuousGraphIncrementalConsumer<BlueTuskGraphTraversalRow, string>(session, replay);
                await consumer.InitializeAsync(token);
                var snapshot = await ReadNextAsync(consumer.Identity, LiveEventKind.InitialResult);
                Assert.True(LiveReplayJsonSerializer.VerifyIntegrity(snapshot));
                if (epoch > 0)
                    sessionRestarts++;

                do
                {
                    token.ThrowIfCancellationRequested();
                    var iteration = checked((uint)latencies.Count + 1);
                    var add = (iteration & 1) == 1;
                    var position = new BlueTuskLogSequenceNumber(iteration);
                    var identity = new ChangeId(Source, position, iteration, 0);
                    var row = new ChangeRow(Dependencies,
                        [ChangeColumnValue.FromValue(
                            System.Text.Encoding.UTF8.GetBytes(edgeId.ToString("D")), ChangeValueEncoding.Text)]);
                    Change change = add ? new InsertChange(identity, row) : new DeleteChange(identity, row);
                    var startedEvaluation = Stopwatch.GetTimestamp();
                    await fixture.SetEdgeAsync(edgeId, fixture.Isolated, fixture.Identity, add, token);

                    if (iteration == 3)
                    {
                        await fixture.Database.Database.ExecuteSqlRawAsync(
                            "REVOKE SELECT ON topology.dependencies FROM " + AlphaRole, token);
                        try
                        {
                            await using var denied = NewDelivery();
                            var error = await Assert.ThrowsAsync<BlueTuskServerException>(
                                () => consumer.ConsumeTransactionAsync(denied, token).AsTask());
                            Assert.Equal("42501", error.SqlState);
                            Assert.Equal(ChangeDeliveryState.Nacked, denied.State);
                            ++revokedDeliveries;
                        }
                        finally
                        {
                            await fixture.Database.Database.ExecuteSqlRawAsync(
                                "GRANT SELECT ON topology.dependencies TO " + AlphaRole, token);
                        }
                    }

                    if (iteration == 5)
                    {
                        await using var lockConnection = new BlueTuskConnection(fixture.ConnectionString);
                        await lockConnection.OpenAsync(token);
                        await using var transaction = await lockConnection.BeginTransactionAsync(token);
                        await using var command = new BlueTuskCommand("LOCK TABLE topology.dependencies IN ACCESS EXCLUSIVE MODE", lockConnection);
                        command.Transaction = transaction;
                        await command.ExecuteNonQueryAsync(token);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
                        await using var canceled = NewDelivery();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(
                            () => consumer.ConsumeTransactionAsync(canceled, timeout.Token).AsTask());
                        await transaction.RollbackAsync(token);
                        ++canceledDeliveries;
                    }

                    await using var delivery = NewDelivery();
                    await consumer.ConsumeTransactionAsync(delivery, token);
                    Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
                    var stored = await ReadNextAsync(consumer.Identity, add ? LiveEventKind.RowAdded : LiveEventKind.RowRemoved);
                    using var payload = JsonDocument.Parse(stored.Payload);
                    Assert.Equal(expectedKey, payload.RootElement.GetProperty("key").GetString());
                    latencies.Add(Stopwatch.GetElapsedTime(startedEvaluation).TotalMilliseconds);

                    ChangeTransactionDelivery NewDelivery() =>
                        ChangeDeliveryTestFactory.CreateCommitted(Source, iteration, position, [change]);
                }
                while (latencies.Count % restartInterval != 0 &&
                       (timer.Elapsed < duration || latencies.Count < minimumEvaluations));
            }

            Assert.True(revokedDeliveries > 0 && canceledDeliveries > 0);
            Assert.True(Percentile95() <= 1000);
            completed = true;
        }
        finally
        {
            var report = JsonSerializer.Serialize(new
            {
                scope = "native-topology-synthetic-delivery",
                started,
                finished = DateTimeOffset.UtcNow,
                requestedSeconds = duration.TotalSeconds,
                minimumEvaluations,
                elapsedSeconds = timer.Elapsed.TotalSeconds,
                evaluations = latencies.Count,
                replaySequence = sequence,
                sessionRestarts,
                revokedDeliveries,
                canceledDeliveries,
                lifecycleP95Milliseconds = Percentile95(),
                completed,
            });
            await File.WriteAllTextAsync(reportPath, report, CancellationToken.None);
        }

        double? Percentile95()
        {
            if (latencies.Count == 0)
                return null;
            var sorted = latencies.Order().ToArray();
            return sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        }

        async Task<LiveReplayEvent> ReadNextAsync(LiveSubscriptionIdentity identity, LiveEventKind kind)
        {
            var batch = await replay.ReadAsync(identity, sequence, 2, token);
            Assert.Equal(LiveReplayReadStatus.Available, batch.Status);
            var item = Assert.Single(batch.Events);
            Assert.Equal(++sequence, item.Sequence);
            Assert.Equal(sequence, batch.LastSequence);
            Assert.Equal(kind, item.Kind);
            Assert.True(LiveReplayJsonSerializer.VerifyIntegrity(item));
            return item;
        }
    }

    private static string GetSetting(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} for the native Topology fixture.");

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string connectionString)
        {
            ConnectionString = connectionString;
            Database = NewContext(connectionString);
            Service = new TopologyService(new EfTopologyRepository(Database));
        }

        public string ConnectionString { get; }
        public TopologyDbContext Database { get; }
        public TopologyService Service { get; }
        public Guid Billing { get; private set; }
        public Guid Identity { get; private set; }
        public Guid Isolated { get; private set; }
        public Guid Hidden { get; private set; }

        public static async Task<Fixture> CreateAsync(CancellationToken token)
        {
            var fixture = new Fixture(GetSetting("BLUETUSK_GRAPH_ENGINE_TEST_CONNECTION_STRING"));
            try
            {
                // The runner provisions a dedicated database. A nonempty schema fails setup.
                await fixture.Database.Database.MigrateAsync(token);
                await fixture.Database.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS bluetusk_graph", token);
                fixture.Billing = (await fixture.Service.RegisterAsync("alpha", "billing", token)).Id;
                var gateway = await fixture.Service.RegisterAsync("alpha", "gateway", token);
                fixture.Identity = (await fixture.Service.RegisterAsync("alpha", "identity", token)).Id;
                fixture.Isolated = (await fixture.Service.RegisterAsync("alpha", "isolated", token)).Id;
                fixture.Hidden = (await fixture.Service.RegisterAsync("beta", "identity", token)).Id;
                await fixture.Service.ConnectAsync("alpha", fixture.Billing, gateway.Id, token);
                await fixture.Service.ConnectAsync("alpha", gateway.Id, fixture.Identity, token);
                await fixture.Database.PropertyGraph(Graph).RegisterEngineAsync(token);
                await fixture.Database.Database.ExecuteSqlRawAsync("""
                    CREATE ROLE bluetusk_topology_canary_alpha NOLOGIN;
                    CREATE ROLE bluetusk_topology_canary_beta NOLOGIN;
                    GRANT USAGE ON SCHEMA topology TO bluetusk_topology_canary_alpha, bluetusk_topology_canary_beta;
                    GRANT SELECT ON topology.services, topology.dependencies TO bluetusk_topology_canary_alpha, bluetusk_topology_canary_beta;
                    ALTER TABLE topology.services ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE topology.dependencies ENABLE ROW LEVEL SECURITY;
                    CREATE POLICY alpha_services ON topology.services TO bluetusk_topology_canary_alpha USING ("TenantId" = 'alpha');
                    CREATE POLICY alpha_edges ON topology.dependencies TO bluetusk_topology_canary_alpha USING ("TenantId" = 'alpha');
                    CREATE POLICY beta_services ON topology.services TO bluetusk_topology_canary_beta USING ("TenantId" = 'beta');
                    CREATE POLICY beta_edges ON topology.dependencies TO bluetusk_topology_canary_beta USING ("TenantId" = 'beta');
                    """, token);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask<TopologyDbContext> OpenAuthorizedAsync(
            LiveSecurityScope scope, IsolationLevel isolation, CancellationToken token)
        {
            var context = NewContext(ConnectionString);
            try
            {
                await context.Database.BeginTransactionAsync(isolation, token);
                // The native scope uses ReadCommitted so a portable fallback fails the fixture.
                if (scope == AlphaScope)
                    await context.Database.ExecuteSqlRawAsync("SET LOCAL ROLE " + AlphaRole, token);
                else if (scope == BetaScope)
                    await context.Database.ExecuteSqlRawAsync("SET LOCAL ROLE " + BetaRole, token);
                else
                    throw new UnauthorizedAccessException("Unknown Topology canary scope.");
                return context;
            }
            catch
            {
                await context.DisposeAsync();
                throw;
            }
        }

        public Task<int> SetEdgeAsync(Guid id, Guid source, Guid destination, bool add, CancellationToken token) =>
            add
                ? Database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO topology.dependencies ("Id", "TenantId", "SourceId", "DestinationId", "CreatedAt")
                    VALUES ({id}, 'alpha', {source}, {destination}, CURRENT_TIMESTAMP)
                    """, token)
                : Database.Database.ExecuteSqlInterpolatedAsync(
                    $"""DELETE FROM topology.dependencies WHERE "Id" = {id}""", token);

        public ValueTask DisposeAsync() => Database.DisposeAsync();

        private static TopologyDbContext NewContext(string connectionString) =>
            new(new DbContextOptionsBuilder<TopologyDbContext>().UseBlueTusk(connectionString).Options);
    }
}
