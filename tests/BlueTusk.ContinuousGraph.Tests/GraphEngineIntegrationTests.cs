using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.Live;
using BlueTusk.Live.Testing;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.ContinuousGraph.Tests;

public sealed class GraphEngineIntegrationTests
{
    [Fact]
    public async Task Typed_traversals_and_live_repairs_use_the_same_authorized_topology()
    {
        var token = TestContext.Current.CancellationToken;
        if (Environment.GetEnvironmentVariable("BLUETUSK_GRAPH_ENGINE_TEST_RUNTIME") is { } expectedRuntime)
        {
            Assert.Equal(expectedRuntime, Environment.Version.ToString());
        }

        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_GRAPH_ENGINE_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("The native Graph test requires BLUETUSK_GRAPH_ENGINE_TEST_CONNECTION_STRING.");
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(token);
        await ExecuteAsync("""
            CREATE EXTENSION IF NOT EXISTS bluetusk_graph;
            CREATE SCHEMA engine_fixture;
            CREATE TABLE engine_fixture.service (id bigint PRIMARY KEY, tenant text NOT NULL DEFAULT 'a');
            CREATE TABLE engine_fixture.dependency (
                id bigint PRIMARY KEY,
                source bigint NOT NULL REFERENCES engine_fixture.service(id),
                target bigint NOT NULL REFERENCES engine_fixture.service(id),
                tenant text NOT NULL DEFAULT 'a');
            CREATE INDEX ON engine_fixture.dependency(source);
            CREATE INDEX ON engine_fixture.dependency(target);
            INSERT INTO engine_fixture.service VALUES (1), (2), (3), (4);
            INSERT INTO engine_fixture.service VALUES (5, 'b'), (6, 'a');
            INSERT INTO engine_fixture.dependency VALUES (10, 1, 2), (20, 2, 3), (30, 1, 4);
            INSERT INTO engine_fixture.dependency VALUES (50, 2, 5), (60, 5, 6);
            CREATE ROLE bluetusk_engine_fixture_reader;
            GRANT USAGE ON SCHEMA engine_fixture TO bluetusk_engine_fixture_reader;
            GRANT SELECT ON ALL TABLES IN SCHEMA engine_fixture TO bluetusk_engine_fixture_reader;
            ALTER TABLE engine_fixture.service ENABLE ROW LEVEL SECURITY;
            ALTER TABLE engine_fixture.dependency ENABLE ROW LEVEL SECURITY;
            CREATE POLICY service_tenant ON engine_fixture.service TO bluetusk_engine_fixture_reader USING (tenant = 'a');
            CREATE POLICY dependency_tenant ON engine_fixture.dependency TO bluetusk_engine_fixture_reader USING (tenant = 'a');
            """);

        try
        {
            var deploymentOptions = new DbContextOptionsBuilder<GraphEngineContext>().UseBlueTusk(connection).Options;
            await using (var deployment = new GraphEngineContext(deploymentOptions))
                await deployment.PropertyGraph("topology").RegisterEngineAsync(token);

            await using var readerConnection = new BlueTuskConnection(connectionString);
            await readerConnection.OpenAsync(token);
            await using (var setRole = new BlueTuskCommand("SET ROLE bluetusk_engine_fixture_reader", readerConnection))
                await setRole.ExecuteNonQueryAsync(token);

            var efOptions = new DbContextOptionsBuilder<GraphEngineContext>().UseBlueTusk(readerConnection).Options;
            await using var context = new GraphEngineContext(efOptions);
            var graph = context.PropertyGraph("topology");
            var engine = new BlueTuskGraphTraversalOptions { UseServerEngine = true };
            var start = new BlueTuskGraphIdentity("services", 1L);
            var target = new BlueTuskGraphIdentity("services", 3L);

            var native = await graph.ReachableAsync(start, engine, token);
            var portable = await graph.ReachableAsync(start, cancellationToken: token);
            Assert.True(native.UsedServerEngine);
            Assert.False(portable.UsedServerEngine);
            Assert.Equal([2L, 4L, 3L], native.Rows.Select(static row => (long) row.Vertex.Key[0]));
            Assert.Equal(native.Rows.Select(Row), portable.Rows.Select(Row));
            var path = await graph.ShortestPathAsync(start, target, engine, token);
            Assert.Equal([10L, 20L], path.Path!.Edges.Select(static edge => (long) edge.Key[0]));
            var portablePath = await graph.ShortestPathAsync(start, target, cancellationToken: token);
            Assert.Equal(path.Path.Edges.Select(static edge => edge.Key[0]),
                portablePath.Path!.Edges.Select(static edge => edge.Key[0]));

            var first = await graph.NeighborsAsync(start,
                new BlueTuskGraphTraversalOptions { UseServerEngine = true, MaximumResults = 1 },
                cancellationToken: token);
            Assert.NotNull(first.NextEdge);
            var second = await graph.NeighborsAsync(start, engine, first.NextEdge, token);
            Assert.Equal(30L, Assert.Single(second.Rows).Edge!.Key[0]);

            var budget = await graph.ReachableAsync(start,
                new BlueTuskGraphTraversalOptions { UseServerEngine = true, MaximumEdges = 1 }, token);
            Assert.True(budget.BudgetExhausted);
            Assert.Empty(budget.Rows);

            var plan = ContinuousGraphQueryCompiler.CompileReachability(
                context, "impact", "engine-test", "v1", "topology", null,
                [new LiveQueryParameter("source", typeof(long))],
                static arguments => new BlueTuskGraphIdentity("services", arguments.Get<long>("source")),
                (_, _) => ValueTask.FromResult(new GraphEngineContext(efOptions)), engine);
            var invalidations = new InMemoryLiveInvalidationLog();
            var scope = new LiveSecurityScope("tenant:a", "test-policy");
            var arguments = plan.Bind(new Dictionary<string, object?> { ["source"] = 1L });
            var pathPlan = ContinuousGraphQueryCompiler.CompileShortestPath(
                context, "dependency-path", "engine-test", "v1", "topology", null,
                [new LiveQueryParameter("source", typeof(long)), new LiveQueryParameter("target", typeof(long))],
                static bound => new BlueTuskGraphIdentity("services", bound.Get<long>("source")),
                static bound => new BlueTuskGraphIdentity("services", bound.Get<long>("target")),
                (_, _) => ValueTask.FromResult(new GraphEngineContext(efOptions)), engine);
            var pathArguments = pathPlan.Bind(new Dictionary<string, object?> { ["source"] = 1L, ["target"] = 3L });
            Assert.Equal([10L, 20L], (await ReadPathAsync()).Edges.Select(static edge => (long) edge.Key[0]));
            var neighborPlan = ContinuousGraphQueryCompiler.CompileNeighbors(
                context, "neighbors", "engine-test", "v1", "topology", null,
                [new LiveQueryParameter("source", typeof(long))],
                static bound => new BlueTuskGraphIdentity("services", bound.Get<long>("source")),
                (_, _) => ValueTask.FromResult(new GraphEngineContext(efOptions)), engine);
            var neighbors = await neighborPlan.LivePlan.ExecuteAsync(
                new LiveQueryExecutionContext(arguments, scope), token);
            Assert.Equal([10L, 30L], neighbors.Select(static row => (long) row.Edge!.Key[0]));

            await using var session = plan.CreateSession(arguments, scope, invalidations);
            var snapshot = await session.StartAsync(token);
            Assert.Equal(3, snapshot.Snapshot.Rows.Count);

            var replay = new InMemoryLiveReplayStore();
            await using var incremental = plan.CreateIncrementalSession(arguments, scope,
                new ContinuousGraphIncrementalOptions<BlueTuskGraphTraversalRow, string>
                {
                    ResultOrdering = Comparer<BlueTuskGraphTraversalRow>.Create(
                        static (left, right) => left.Hops.CompareTo(right.Hops)),
                    KeyOrdering = StringComparer.Ordinal,
                });
            await using var consumer = new ContinuousGraphIncrementalConsumer<BlueTuskGraphTraversalRow, string>(
                incremental, replay);
            await consumer.InitializeAsync(token);
            var source = new ChangeSourceIdentity("graph-system", plan.LivePlan.DatabaseIdentity, "graph-slot", "graph-publication");
            var table = new ChangeTable(1, "engine_fixture", "dependency", 'f',
                [new ChangeColumn(0, "id", 20, -1, IsKey: true)]);

            await ExecuteAsync("DELETE FROM engine_fixture.dependency WHERE id = 20");
            var deletedAt = new BlueTuskLogSequenceNumber(10);
            var deleted = new DeleteChange(new ChangeId(source, deletedAt, 1, 0),
                new ChangeRow(table, [ChangeColumnValue.FromValue("20"u8.ToArray(), ChangeValueEncoding.Text)]));
            await using (var delivery = ChangeDeliveryTestFactory.CreateCommitted(source, 1, deletedAt, [deleted]))
            {
                await consumer.ConsumeTransactionAsync(delivery, token);
                Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
            }
            invalidations.Append(plan.LivePlan.DatabaseIdentity, plan.Dependencies);
            var removal = await session.RefreshToCurrentAsync(token);
            Assert.NotNull(removal);
            Assert.Contains(removal.Events, static item => item.Kind == LiveEventKind.RowRemoved);

            await ExecuteAsync("INSERT INTO engine_fixture.dependency VALUES (40, 4, 3)");
            var insertedAt = new BlueTuskLogSequenceNumber(20);
            var inserted = new InsertChange(new ChangeId(source, insertedAt, 2, 0),
                new ChangeRow(table, [ChangeColumnValue.FromValue("40"u8.ToArray(), ChangeValueEncoding.Text)]));
            await using (var delivery = ChangeDeliveryTestFactory.CreateCommitted(source, 2, insertedAt, [inserted]))
            {
                await consumer.ConsumeTransactionAsync(delivery, token);
                Assert.Equal(ChangeDeliveryState.Acknowledged, delivery.State);
            }
            Assert.Equal(2, incremental.Status.AuthoritativeRepairs);
            Assert.Equal(3, (await replay.ReadAsync(consumer.Identity, 0, 10, token)).LastSequence);
            Assert.Equal([30L, 40L], (await ReadPathAsync()).Edges.Select(static edge => (long) edge.Key[0]));

            invalidations.Append(plan.LivePlan.DatabaseIdentity, plan.Dependencies);
            var addition = await session.RefreshToCurrentAsync(token);
            Assert.NotNull(addition);
            Assert.Contains(addition.Events, static item => item.Kind == LiveEventKind.RowAdded);

            await using var blocker = new BlueTuskConnection(connectionString);
            await blocker.OpenAsync(token);
            await using (var transaction = await blocker.BeginTransactionAsync(token))
            {
                await using var command = new BlueTuskCommand(
                    "LOCK TABLE engine_fixture.dependency IN ACCESS EXCLUSIVE MODE", blocker);
                command.Transaction = transaction;
                await command.ExecuteNonQueryAsync(token);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => graph.ReachableAsync(start, engine, cancellation.Token));
                await transaction.RollbackAsync(token);
            }
            Assert.Equal(3, (await graph.ReachableAsync(start, engine, token)).Rows.Count);
            await ExecuteAsync("""
                DELETE FROM bluetusk_graph._graphs WHERE graph_schema = 'engine_fixture' AND graph_name = 'topology'
                """);
            var fallback = await graph.ReachableAsync(start, engine, token);
            Assert.False(fallback.UsedServerEngine);
            Assert.Equal(native.Rows.Select(Row), fallback.Rows.Select(Row));

            async Task<BlueTuskGraphPath> ReadPathAsync() => Assert.Single(
                await pathPlan.LivePlan.ExecuteAsync(new LiveQueryExecutionContext(pathArguments, scope), token));
        }
        finally
        {
            await ExecuteAsync("""
                DELETE FROM bluetusk_graph._graphs WHERE graph_schema = 'engine_fixture' AND graph_name = 'topology';
                DROP SCHEMA engine_fixture CASCADE;
                DROP ROLE bluetusk_engine_fixture_reader;
                """);
        }

        static (long Id, int Hops) Row(BlueTuskGraphTraversalRow row) => ((long) row.Vertex.Key[0], row.Hops);

        async Task ExecuteAsync(string sql)
        {
            await using var command = new BlueTuskCommand(sql, connection);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private sealed class GraphEngineContext(DbContextOptions<GraphEngineContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("engine_fixture");
            modelBuilder.Entity<Service>().ToTable("service").Property(static service => service.Id).HasColumnName("id");
            modelBuilder.Entity<Dependency>().ToTable("dependency");
            modelBuilder.Entity<Dependency>().Property(static edge => edge.Id).HasColumnName("id");
            modelBuilder.Entity<Dependency>().Property(static edge => edge.Source).HasColumnName("source");
            modelBuilder.Entity<Dependency>().Property(static edge => edge.Target).HasColumnName("target");
            modelBuilder.HasPropertyGraph("topology", graph => graph
                .Vertex<Service>("services", vertex => vertex.HasLabel("service").HasKey(static row => row.Id))
                .Edge<Dependency>("depends", edge => edge.HasLabel("dependency").HasKey(static row => row.Id)
                    .HasSource<Service>(static row => row.Source, static row => row.Id)
                    .HasDestination<Service>(static row => row.Target, static row => row.Id)));
        }
    }

    private sealed class Service
    {
        public long Id { get; set; }
    }

    private sealed class Dependency
    {
        public long Id { get; set; }
        public long Source { get; set; }
        public long Target { get; set; }
    }
}
