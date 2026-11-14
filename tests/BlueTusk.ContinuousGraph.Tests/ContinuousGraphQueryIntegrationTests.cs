using System.Data.Common;
using System.Diagnostics;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.Live;
using BlueTusk.Live.Testing;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Sdk;

namespace BlueTusk.ContinuousGraph.Tests;

public sealed class ContinuousGraphQueryIntegrationTests
{
    private const string PeopleTable = "bluetusk_continuous_graph_people";
    private const string FriendshipsTable = "bluetusk_continuous_graph_friendships";
    private const string GraphName = "bluetusk_continuous_graph";
    private const string AlphaRole = "bluetusk_graph_alpha";
    private const string BetaRole = "bluetusk_graph_beta";

    [Theory]
    [InlineData(BlueTuskPropertyGraphBackend.Relational)]
    [InlineData(BlueTuskPropertyGraphBackend.NativeSqlPgq)]
    public async Task PostgreSQL_requeries_affected_graphs_and_cancels_blocked_execution(
        BlueTuskPropertyGraphBackend backend)
    {
        var connectionString = GetConnectionString();
        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(TestContext.Current.CancellationToken);
        var native = backend == BlueTuskPropertyGraphBackend.NativeSqlPgq;
        var factory = new GraphContextFactory(connectionString, backend);
        if (native && administration.SupportsSqlPgq is not true)
        {
            await Assert.ThrowsAsync<ContinuousGraphCapabilityException>(
                () => ContinuousGraphQueryCompiler.CompileAsync(
                    factory, CreateDefinition(), cancellationToken: TestContext.Current.CancellationToken).AsTask());
            return;
        }

        await ResetSchemaAsync(administration, native);
        try
        {
            await CreateSchemaAsync(administration, native);
            var definition = CreateDefinition();
            var plan = await ContinuousGraphQueryCompiler.CompileAsync(
                factory,
                definition,
                cancellationToken: TestContext.Current.CancellationToken);
            var arguments = plan.Bind(
                new Dictionary<string, object?> { ["sourceId"] = 1 });
            var securityScope = new LiveSecurityScope("tenant:integration", "policy-v1");
            await using (var scopeContext = factory.CreateDbContext())
            {
                var limited = definition.QueryFactory(scopeContext, arguments).Take(1).AsNoTracking();
                var scopedRows = await ContinuousGraphKeyScope.Apply(limited, definition.KeySelector, [3])
                    .ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(3, Assert.Single(scopedRows).TargetId);
            }

            var invalidations = new InMemoryLiveInvalidationLog();
            await using var session = plan.CreateSession(
                arguments,
                securityScope,
                invalidations);

            var initial = await session.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["Grace", "Linus"], initial.Snapshot.Rows.Select(static row => row.TargetName));

            await ExecuteAsync(
                administration,
                $"UPDATE {PeopleTable} SET name = 'Hopper' WHERE id = 2",
                TestContext.Current.CancellationToken);
            _ = invalidations.Append("continuous-graph-integration", plan.Dependencies);

            var refresh = Assert.IsType<LiveDiffBatch<FriendResult, int>>(
                await session.RefreshToCurrentAsync(TestContext.Current.CancellationToken));
            var updated = Assert.Single(
                refresh.Events,
                static graphEvent => graphEvent.Kind is LiveEventKind.RowUpdated);
            Assert.Equal(2, updated.Key);
            Assert.Equal("Hopper", updated.Row!.TargetName);

            await AssertBlockedExecutionCancelsAsync(
                connectionString,
                plan,
                arguments,
                securityScope);
        }
        finally
        {
            await ResetSchemaAsync(administration, native);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Portable_graph_preserves_RLS_during_scoped_delta_and_repair_on_a_reused_connection(
        bool useScopedConnection, bool useBorrowedLease)
    {
        var token = TestContext.Current.CancellationToken;
        var connectionString = GetConnectionString();
        await using var admin = new BlueTuskConnection(connectionString);
        await admin.OpenAsync(token);
        await ResetSchemaAsync(admin, native: false);
        try
        {
            await CreateSchemaAsync(admin, native: false);
            await ExecuteAsync(admin,
                $"""
                CREATE ROLE {AlphaRole} NOLOGIN;
                CREATE ROLE {BetaRole} NOLOGIN;
                ALTER TABLE {PeopleTable} ADD COLUMN reader text NOT NULL DEFAULT '{AlphaRole}';
                ALTER TABLE {FriendshipsTable} ADD COLUMN reader text NOT NULL DEFAULT '{AlphaRole}';
                UPDATE {PeopleTable} SET reader = '{BetaRole}' WHERE id = 3;
                INSERT INTO {PeopleTable} VALUES (4, 'Beta source', '{BetaRole}');
                UPDATE {FriendshipsTable} SET reader = '{BetaRole}', from_id = 4 WHERE id = 11;
                INSERT INTO {FriendshipsTable} VALUES (30, 1, 2, '{BetaRole}');
                ALTER TABLE {PeopleTable} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE {FriendshipsTable} ENABLE ROW LEVEL SECURITY;
                CREATE POLICY graph_reader ON {PeopleTable} USING (reader = current_user);
                CREATE POLICY graph_reader ON {FriendshipsTable} USING (reader = current_user);
                GRANT SELECT ON {PeopleTable}, {FriendshipsTable} TO {AlphaRole}, {BetaRole};
                """, token);

            var pooled = new BlueTuskConnectionStringBuilder(connectionString)
            {
                Pooling = true,
                MaximumPoolSize = 1,
            }.ConnectionString;
            await using var dataSource = BlueTuskDataSource.Create(pooled);
            var alpha = await ContinuousGraphQueryCompiler.CompileAsync(new RoleContextFactory(dataSource, AlphaRole),
                CreateDefinition(), cancellationToken: token);
            var beta = await ContinuousGraphQueryCompiler.CompileAsync(new RoleContextFactory(dataSource, BetaRole),
                CreateDefinition(), cancellationToken: token);
            var alphaArguments = alpha.Bind(new Dictionary<string, object?> { ["sourceId"] = 1 });
            var alphaScope = new LiveSecurityScope("alpha", "rls-v1");
            var betaScope = new LiveSecurityScope("beta", "rls-v1");
            var betaArguments = beta.Bind(new Dictionary<string, object?> { ["sourceId"] = 4 });
            var authorizations = 0;
            var revoked = false;
            CancellationTokenSource? cancelAfterAcquiring = null;
            var leaseConnectionString = new BlueTuskConnectionStringBuilder(pooled)
            {
                MaxAutoPrepare = 2,
                AutoPrepareMinUsages = 1,
            }.ConnectionString;
            await using var borrowed = useBorrowedLease ? new BlueTuskConnection(leaseConnectionString) : null;
            TestConnectionLease? lease = null;
            if (borrowed is not null)
            {
                await borrowed.OpenAsync(token);
                await ExecuteAsync(borrowed, $"SET ROLE {AlphaRole};", token);
                lease = new TestConnectionLease(borrowed);
            }

            void CheckAuthorization(LiveQueryExecutionContext execution, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(alphaScope, execution.SecurityScope);
                authorizations++;
            }

            var scopedConnection = lease is not null
                ? new ContinuousGraphScopedConnection((execution, cancellationToken) =>
                {
                    CheckAuthorization(execution, cancellationToken);
                    if (revoked)
                    {
                        throw new UnauthorizedAccessException("Subscriber revoked");
                    }

                    cancelAfterAcquiring?.Cancel();
                    return ValueTask.FromResult<IContinuousGraphScopedConnectionLease>(lease);
                })
                : useScopedConnection
                    ? new ContinuousGraphScopedConnection(
                        (execution, cancellationToken) =>
                        {
                            CheckAuthorization(execution, cancellationToken);
                            return ValueTask.FromResult(dataSource);
                        },
                        async (connection, _, cancellationToken) =>
                        {
                            await ExecuteAsync(connection, $"SET ROLE {AlphaRole};", cancellationToken);
                            if (revoked)
                            {
                                throw new UnauthorizedAccessException("Subscriber revoked");
                            }
                        })
                    : null;

            await using var session = alpha.CreateIncrementalSession(alphaArguments, alphaScope,
                new ContinuousGraphIncrementalOptions<FriendResult, int>
                {
                    ResultOrdering = Comparer<FriendResult>.Create(static (left, right) => left.TargetId.CompareTo(right.TargetId)),
                    KeyOrdering = Comparer<int>.Default,
                    ScopedConnection = scopedConnection,
                });
            await using (var initial = await session.PrepareInitialAsync(cancellationToken: token))
            {
                Assert.Equal("Grace", Assert.Single(initial.Evaluation.Batch!.Snapshot.Rows).TargetName);
                await initial.CommitAsync(token);
            }

            Assert.Empty(await beta.LivePlan.ExecuteAsync(new LiveQueryExecutionContext(alphaArguments, betaScope), token));
            Assert.Equal("Linus", Assert.Single(await beta.LivePlan.ExecuteAsync(
                new LiveQueryExecutionContext(betaArguments, betaScope), token)).TargetName);
            Assert.Empty(await alpha.LivePlan.ExecuteAsync(new LiveQueryExecutionContext(betaArguments, alphaScope), token));

            await ExecuteAsync(admin, $"UPDATE {FriendshipsTable} SET id = 12 WHERE id = 10;", token);
            var source = new ChangeSourceIdentity("rls", "graph", "slot", "publication");
            var position = new BlueTuskLogSequenceNumber(1);
            var table = new ChangeTable(1, "public", FriendshipsTable, 'f',
                [new ChangeColumn(0, "to_id", 23, -1, IsKey: false)]);
            var endpoint = new ChangeRow(table, [ChangeColumnValue.FromValue("2"u8.ToArray(), ChangeValueEncoding.Text)]);
            if (useScopedConnection && !useBorrowedLease)
            {
                await AssertRevokedAsync(position);
            }

            await using (var delivery = CreateDelivery(position))
            await using (var scoped = await session.PrepareTransactionAsync(delivery.Transaction, token))
            {
                Assert.Equal(ContinuousGraphMaintenanceTier.AuthoritativeDelta, scoped.Evaluation.MaintenanceTier);
                Assert.Equal(12, Assert.Single(scoped.Evaluation.Batch!.Snapshot.Rows).RelationshipId);
                await scoped.CommitAsync(token);
            }
            if (lease is not null)
            {
                var nextPosition = new BlueTuskLogSequenceNumber(2);
                await ExecuteAsync(admin, $"UPDATE {FriendshipsTable} SET id = 13 WHERE id = 12;", token);
                await AssertRevokedAsync(nextPosition);
                using var cancelledRead = CancellationTokenSource.CreateLinkedTokenSource(token);
                cancelAfterAcquiring = cancelledRead;
                await using (var cancelled = CreateDelivery(nextPosition))
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    {
                        await using var unexpected = await session.PrepareTransactionAsync(cancelled.Transaction, cancelledRead.Token);
                    });
                }
                cancelAfterAcquiring = null;
                await using var next = CreateDelivery(nextPosition);
                await using var scoped = await session.PrepareTransactionAsync(next.Transaction, token);
                Assert.Equal(ContinuousGraphMaintenanceTier.AuthoritativeDelta, scoped.Evaluation.MaintenanceTier);
                Assert.Equal(13, Assert.Single(scoped.Evaluation.Batch!.Snapshot.Rows).RelationshipId);
                await scoped.CommitAsync(token);
                Assert.Equal(3, lease.Returns);
            }

            Assert.Equal(useBorrowedLease ? 4 : useScopedConnection ? 2 : 0, authorizations);

            await ExecuteAsync(admin, $"UPDATE {PeopleTable} SET reader = '{BetaRole}' WHERE id = 2;", token);
            var repairPosition = new BlueTuskLogSequenceNumber(3);
            var people = new ChangeTable(2, "public", PeopleTable, 'f',
                [new ChangeColumn(0, "id", 23, -1, IsKey: true)]);
            var person = new ChangeRow(people, [ChangeColumnValue.FromValue("2"u8.ToArray(), ChangeValueEncoding.Text)]);
            await using (var delivery = ChangeDeliveryTestFactory.CreateCommitted(source, 3, repairPosition,
                [new UpdateChange(new ChangeId(source, repairPosition, 3, 0), person, person, new ChangedColumnSet(false, []))]))
            await using (var repair = await session.PrepareTransactionAsync(delivery.Transaction, token))
            {
                Assert.Equal(ContinuousGraphMaintenanceTier.AuthoritativeRepair, repair.Evaluation.MaintenanceTier);
                Assert.Empty(repair.Evaluation.Batch!.Snapshot.Rows);
                Assert.Empty(await alpha.LivePlan.ExecuteAsync(new LiveQueryExecutionContext(alphaArguments, alphaScope), token));
                await repair.CommitAsync(token);
            }

            ChangeTransactionDelivery CreateDelivery(BlueTuskLogSequenceNumber lsn)
            {
                var transactionId = checked((uint)lsn.Value);
                return ChangeDeliveryTestFactory.CreateCommitted(source, transactionId, lsn,
                    [new UpdateChange(new ChangeId(source, lsn, transactionId, 0), endpoint, endpoint, new ChangedColumnSet(false, []))]);
            }

            async Task AssertRevokedAsync(BlueTuskLogSequenceNumber lsn)
            {
                revoked = true;
                await using var denied = CreateDelivery(lsn);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                {
                    await using var unexpected = await session.PrepareTransactionAsync(denied.Transaction, token);
                });
                revoked = false;
            }
        }
        finally
        {
            await ResetSchemaAsync(admin, native: false);
            await ExecuteAsync(admin, $"DROP ROLE IF EXISTS {AlphaRole}, {BetaRole};", token);
        }
    }

    private sealed class TestConnectionLease(BlueTuskConnection connection) : IContinuousGraphScopedConnectionLease
    {
        public BlueTuskConnection Connection => connection;
        public int Returns { get; private set; }

        public ValueTask DisposeAsync()
        {
            Returns++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Full_reads_honor_current_order_limit_and_parameters_after_compilation()
    {
        var token = TestContext.Current.CancellationToken;
        var connectionString = GetConnectionString();
        await using var admin = new BlueTuskConnection(connectionString);
        await admin.OpenAsync(token);
        await ResetSchemaAsync(admin, native: false);
        try
        {
            await CreateSchemaAsync(admin, native: false);
            var reverse = false;
            var limit = 20;
            var factory = new GraphContextFactory(connectionString, BlueTuskPropertyGraphBackend.Relational);
            var plan = await ContinuousGraphQueryCompiler.CompileAsync(factory,
                CreateDefinition(() => reverse, () => limit), cancellationToken: token);
            var scope = new LiveSecurityScope("full-read", "v1");
            var arguments = plan.Bind(new Dictionary<string, object?> { ["sourceId"] = 1 });
            var execution = new LiveQueryExecutionContext(arguments, scope);
            Assert.Equal([2, 3], (await plan.LivePlan.ExecuteAsync(execution, token)).Select(row => row.TargetId));
            reverse = true;
            Assert.Equal([3, 2], (await plan.LivePlan.ExecuteAsync(execution, token)).Select(row => row.TargetId));
            limit = 1;
            Assert.Equal(3, Assert.Single(await plan.LivePlan.ExecuteAsync(execution, token)).TargetId);
            reverse = false;
            limit = 20;
            var empty = plan.Bind(new Dictionary<string, object?> { ["sourceId"] = 3 });
            Assert.Empty(await plan.LivePlan.ExecuteAsync(new LiveQueryExecutionContext(empty, scope), token));
            Assert.Equal([2, 3], (await plan.LivePlan.ExecuteAsync(execution, token)).Select(row => row.TargetId));
        }
        finally
        {
            await ResetSchemaAsync(admin, native: false);
        }
    }

    [Fact]
    public async Task Single_key_delta_rebinds_graph_arguments_and_detects_parallel_edges()
    {
        var token = TestContext.Current.CancellationToken;
        var connectionString = GetConnectionString();
        await using var admin = new BlueTuskConnection(connectionString);
        await admin.OpenAsync(token);
        await ResetSchemaAsync(admin, native: false);
        try
        {
            await CreateSchemaAsync(admin, native: false);
            await ExecuteAsync(admin, $"INSERT INTO {FriendshipsTable} VALUES (12, 2, 3);", token);
            var observer = new GraphReadObserver();
            var factory = new GraphContextFactory(connectionString, BlueTuskPropertyGraphBackend.Relational, observer);
            var plan = await ContinuousGraphQueryCompiler.CompileAsync(factory, CreateDefinition(), cancellationToken: token);
            var options = new ContinuousGraphIncrementalOptions<FriendResult, int>
            {
                ResultOrdering = Comparer<FriendResult>.Create(static (left, right) => left.TargetId.CompareTo(right.TargetId)),
                KeyOrdering = Comparer<int>.Default,
            };
            var source = new ChangeSourceIdentity("single-key", "graph", "slot", "publication");
            var table = new ChangeTable(1, "public", FriendshipsTable, 'f',
                [new ChangeColumn(0, "to_id", 23, -1, IsKey: false)]);

            await AssertUpdateAsync(1, 2, 20, "2"u8.ToArray(), expectDuplicate: false);
            await AssertUpdateAsync(2, 3, 21, "3"u8.ToArray(), expectDuplicate: true);

            async Task AssertUpdateAsync(
                int sourceId, int targetId, int relationshipId, byte[] encodedKey, bool expectDuplicate)
            {
                var arguments = plan.Bind(new Dictionary<string, object?> { ["sourceId"] = sourceId });
                await using var session = plan.CreateIncrementalSession(
                    arguments, new LiveSecurityScope("tenant:integration", "policy-v1"), options);
                await using (var initial = await session.PrepareInitialAsync(cancellationToken: token))
                {
                    await initial.CommitAsync(token);
                }

                await ExecuteAsync(admin,
                    $"UPDATE {FriendshipsTable} SET id = {relationshipId} WHERE from_id = {sourceId} AND to_id = {targetId};",
                    token);
                await using (var delivery = CreateChange(1))
                await using (var update = await session.PrepareTransactionAsync(delivery.Transaction, token))
                {
                    Assert.Equal(ContinuousGraphMaintenanceTier.AuthoritativeDelta, update.Evaluation.MaintenanceTier);
                    var commandText = Assert.IsType<string>(observer.LastCommandText);
                    Assert.Contains("LIMIT 2", commandText, StringComparison.Ordinal);
                    Assert.DoesNotContain("ORDER BY", commandText, StringComparison.Ordinal);
                    var change = Assert.Single(update.Evaluation.Batch!.Events);
                    Assert.Equal(targetId, change.Key);
                    Assert.Equal(relationshipId, change.Row!.RelationshipId);
                    await update.CommitAsync(token);
                }

                if (expectDuplicate)
                {
                    await ExecuteAsync(admin,
                        $"INSERT INTO {FriendshipsTable} VALUES (30, {sourceId}, {targetId});", token);
                    await using var delivery = CreateChange(2);
                    var duplicate = await Assert.ThrowsAsync<ContinuousGraphIncrementalException>(
                        () => session.PrepareTransactionAsync(delivery.Transaction, token).AsTask());
                    Assert.Contains("duplicate row key", duplicate.Message, StringComparison.Ordinal);
                }

                ChangeTransactionDelivery CreateChange(uint sequence)
                {
                    var position = new BlueTuskLogSequenceNumber(sequence);
                    var endpoint = new ChangeRow(table, [ChangeColumnValue.FromValue(encodedKey, ChangeValueEncoding.Text)]);
                    return ChangeDeliveryTestFactory.CreateCommitted(source, sequence, position,
                        [new UpdateChange(new ChangeId(source, position, sequence, 0),
                            endpoint, endpoint, new ChangedColumnSet(false, []))]);
                }
            }
        }
        finally
        {
            await ResetSchemaAsync(admin, native: false);
        }
    }

    private static async Task AssertBlockedExecutionCancelsAsync(
        string connectionString,
        ContinuousGraphQueryPlan<FriendResult, int> plan,
        LiveQueryArguments arguments,
        LiveSecurityScope securityScope)
    {
        await using var blocker = new BlueTuskConnection(connectionString);
        await blocker.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction =
            await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var lockCommand = blocker.CreateCommand();
        lockCommand.Transaction = transaction;
        lockCommand.CommandText =
            $"LOCK TABLE {PeopleTable}, {FriendshipsTable} IN ACCESS EXCLUSIVE MODE";
        _ = await lockCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => plan.LivePlan.ExecuteAsync(
                new LiveQueryExecutionContext(arguments, securityScope),
                timeout.Token).AsTask());
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Blocked graph cancellation took {stopwatch.Elapsed}.");
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private static ContinuousGraphQueryDefinition<GraphContext, FriendResult, int>
        CreateDefinition(Func<bool>? reverse = null, Func<int>? limit = null) =>
        new(
            "continuous-friends",
            "continuous-graph-integration",
            "1",
            GraphName,
            graphSchema: null,
            ["people", "friendships"],
            [new LiveQueryParameter("sourceId", typeof(int))],
            new Dictionary<string, object?> { ["sourceId"] = 1 },
            20,
            (context, arguments) =>
            {
                var sourceId = arguments.Get<int>("sourceId");
                var projected = context.PropertyGraph(GraphName)
                    .Match(pattern => pattern
                        .Vertex<Person>("source", person => person.Id == sourceId)
                        .Outgoing<Friendship>("relationship")
                        .Vertex<Person>("target"))
                    .Select<FriendResult>(projection => projection
                        .Property<Person, int>(
                            "source", person => person.Id, result => result.SourceId)
                        .Property<Friendship, int>(
                            "relationship", edge => edge.Id, result => result.RelationshipId)
                        .Property<Person, int>(
                            "target", person => person.Id, result => result.TargetId)
                        .Property<Person, string>(
                            "target", person => person.Name, result => result.TargetName));
                return (reverse?.Invoke() == true
                        ? projected.OrderByDescending(result => result.TargetId)
                        : projected.OrderBy(result => result.TargetId))
                    .Take(limit?.Invoke() ?? 20);
            },
            result => result.TargetId,
            new FriendResultComparer());

    private static async Task CreateSchemaAsync(BlueTuskConnection connection, bool native)
    {
        await ExecuteAsync(
            connection,
            $"""
            CREATE TABLE {PeopleTable} (
                id int4 PRIMARY KEY,
                name text NOT NULL);
            CREATE TABLE {FriendshipsTable} (
                id int4 PRIMARY KEY,
                from_id int4 NOT NULL REFERENCES {PeopleTable} (id),
                to_id int4 NOT NULL REFERENCES {PeopleTable} (id));
            INSERT INTO {PeopleTable} VALUES
                (1, 'Ada'), (2, 'Grace'), (3, 'Linus');
            INSERT INTO {FriendshipsTable} VALUES
                (10, 1, 2), (11, 1, 3);
            """,
            TestContext.Current.CancellationToken);
        if (!native)
        {
            return;
        }

        await ExecuteAsync(
            connection,
            $"""
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
            """,
            TestContext.Current.CancellationToken);
    }

    private static async Task ResetSchemaAsync(BlueTuskConnection connection, bool native)
    {
        if (native)
        {
            await ExecuteAsync(connection, $"DROP PROPERTY GRAPH IF EXISTS {GraphName};",
                TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(
            connection,
            $"""
            DROP TABLE IF EXISTS {FriendshipsTable};
            DROP TABLE IF EXISTS {PeopleTable};
            """,
            TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(
        BlueTuskConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }

    private sealed class GraphReadObserver : DbCommandInterceptor
    {
        public string? LastCommandText { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            LastCommandText = command.CommandText;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class GraphContextFactory(
        string connectionString,
        BlueTuskPropertyGraphBackend backend,
        params IInterceptor[] interceptors) : IDbContextFactory<GraphContext>
    {
        private readonly DbContextOptions<GraphContext> _options = new DbContextOptionsBuilder<GraphContext>()
            .UseBlueTusk(connectionString, options => options.UseGraphCompatibility(backend))
            .AddInterceptors(interceptors)
            .Options;

        public GraphContext CreateDbContext() => new(_options);
    }

    private sealed class RoleContextFactory(BlueTuskDataSource dataSource, string role)
        : IDbContextFactory<GraphContext>
    {
        private readonly DbContextOptions<GraphContext> _options = new DbContextOptionsBuilder<GraphContext>()
            .UseBlueTusk(dataSource).Options;

        public GraphContext CreateDbContext() => throw new NotSupportedException("Use the asynchronous factory.");

        public async Task<GraphContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var context = new GraphContext(_options);
            try
            {
                await context.Database.OpenConnectionAsync(cancellationToken);
                var connection = (BlueTuskConnection)context.Database.GetDbConnection();
                await ExecuteAsync(connection, $"SET ROLE {role};", cancellationToken);
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
            modelBuilder.Entity<Person>(entity =>
            {
                entity.ToTable(PeopleTable);
                entity.HasKey(person => person.Id);
                entity.Property(person => person.Id).HasColumnName("id");
                entity.Property(person => person.Name).HasColumnName("name");
            });
            modelBuilder.Entity<Friendship>(entity =>
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
                        .HasSource<Person>(
                            friendship => friendship.FromPersonId,
                            person => person.Id)
                        .HasDestination<Person>(
                            friendship => friendship.ToPersonId,
                            person => person.Id));
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

        public int RelationshipId { get; set; }

        public int TargetId { get; set; }

        public string TargetName { get; set; } = string.Empty;
    }

    private sealed class FriendResultComparer : IEqualityComparer<FriendResult>
    {
        public bool Equals(FriendResult? x, FriendResult? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             x.SourceId == y.SourceId &&
             x.RelationshipId == y.RelationshipId &&
             x.TargetId == y.TargetId &&
             string.Equals(x.TargetName, y.TargetName, StringComparison.Ordinal));

        public int GetHashCode(FriendResult obj) =>
            HashCode.Combine(
                obj.SourceId,
                obj.RelationshipId,
                obj.TargetId,
                obj.TargetName);
    }
}
