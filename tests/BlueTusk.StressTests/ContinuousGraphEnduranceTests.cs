using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BlueTusk.Client;
using BlueTusk.ContinuousGraph;
using BlueTusk.Data;
using BlueTusk.Live;
using BlueTusk.Live.DependencyInjection;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.StressTests;

public sealed class ContinuousGraphEnduranceTests
{
    private const string PeopleTable = "bluetusk_graph_endurance_people";
    private const string FriendshipsTable = "bluetusk_graph_endurance_friendships";
    private const string GraphName = "bluetusk_graph_endurance";
    private const string ReplaySchema = "bluetusk_graph_endurance_live";
    private static readonly ChangeSourceIdentity Source = new(
        "continuous-graph-endurance",
        "bluetusk_tests",
        "continuous_graph_endurance",
        "public:bluetusk_graph_endurance_people");
    private static readonly JsonSerializerOptions ReportSerializerOptions =
        new() { WriteIndented = true };

    [Fact]
    public async Task Process_restart_seed_persists_replay_state()
    {
        RequirePhase("restart-seed");
        var connectionString = GetConnectionString();
        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(TestContext.Current.CancellationToken);
        RequireSqlPgq(administration);

        await ResetSchemaAsync(administration);
        await DropReplaySchemaAsync(administration);
        await CreateSchemaAsync(administration);
        await using var replayDataSource = BlueTuskDataSource.Create(connectionString);
        var replay = CreateReplayStore(replayDataSource);
        await replay.InitializeAsync(TestContext.Current.CancellationToken);
        var factory = new GraphContextFactory(connectionString);
        var plan = await ContinuousGraphQueryCompiler.CompileAsync(
            factory,
            CreateDefinition(),
            cancellationToken: TestContext.Current.CancellationToken);
        var arguments = CreateArguments(plan);
        var securityScope = CreateSecurityScope();
        var evaluator = new RepairEvaluator();
        await using var session = CreateIncrementalSession(
            plan,
            arguments,
            securityScope,
            evaluator);

        await CommitInitialAsync(
            session,
            replay,
            TestContext.Current.CancellationToken);
        var stored = await replay.ReadAsync(
            session.Identity,
            0,
            10,
            TestContext.Current.CancellationToken);
        Assert.Equal(LiveReplayReadStatus.Available, stored.Status);
        Assert.Equal(1, stored.LastSequence);
        Assert.All(
            stored.Events,
            replayEvent => Assert.True(
                LiveReplayJsonSerializer.VerifyIntegrity(replayEvent)));
    }

    [Fact]
    public async Task Process_restart_resume_reads_and_advances_replay_state()
    {
        RequirePhase("restart-resume");
        var connectionString = GetConnectionString();
        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(TestContext.Current.CancellationToken);
        RequireSqlPgq(administration);

        await using var replayDataSource = BlueTuskDataSource.Create(connectionString);
        var replay = CreateReplayStore(replayDataSource);
        await replay.InitializeAsync(TestContext.Current.CancellationToken);
        var factory = new GraphContextFactory(connectionString);
        var plan = await ContinuousGraphQueryCompiler.CompileAsync(
            factory,
            CreateDefinition(),
            cancellationToken: TestContext.Current.CancellationToken);
        var arguments = CreateArguments(plan);
        var securityScope = CreateSecurityScope();
        var evaluator = new RepairEvaluator();
        await using var session = CreateIncrementalSession(
            plan,
            arguments,
            securityScope,
            evaluator);
        var stored = await replay.ReadAsync(
            session.Identity,
            0,
            10,
            TestContext.Current.CancellationToken);
        Assert.Equal(LiveReplayReadStatus.Available, stored.Status);
        Assert.Equal(1, stored.LastSequence);
        Assert.All(
            stored.Events,
            replayEvent => Assert.True(
                LiveReplayJsonSerializer.VerifyIntegrity(replayEvent)));

        await CommitInitialAsync(
            session,
            replay,
            TestContext.Current.CancellationToken,
            checked(stored.LastSequence + 1));
        var advanced = await replay.ReadAsync(
            session.Identity,
            stored.LastSequence,
            10,
            TestContext.Current.CancellationToken);
        Assert.Equal(LiveReplayReadStatus.Available, advanced.Status);
        Assert.Equal(2, advanced.LastSequence);
        Assert.All(
            advanced.Events,
            replayEvent => Assert.True(
                LiveReplayJsonSerializer.VerifyIntegrity(replayEvent)));
    }

    [Fact]
    public async Task Continuous_graph_survives_repair_restart_cancellation_and_disconnect()
    {
        RequirePhase("run");
        var settings = ReadSettings();
        var connectionString = GetConnectionString();
        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(TestContext.Current.CancellationToken);
        RequireSqlPgq(administration);

        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        var durations = new List<double>();
        long evaluations = 0;
        long committed = 0;
        long authoritativeRepairs = 0;
        long processRestartRecoveries = 0;
        long cancellationRecoveries = 0;
        long disconnectRecoveries = 0;
        long replayCorruptionDetections = 0;
        long replaySequenceErrors = 0;
        long incorrectlyOrderedResults = 0;
        long unreconciledResults = 0;
        try
        {
            var factory = new GraphContextFactory(connectionString);
            var plan = await ContinuousGraphQueryCompiler.CompileAsync(
                factory,
                CreateDefinition(),
                cancellationToken: TestContext.Current.CancellationToken);
            var arguments = CreateArguments(plan);
            var securityScope = CreateSecurityScope();
            await using var replayDataSource = BlueTuskDataSource.Create(
                connectionString);
            var replay = CreateReplayStore(replayDataSource);
            await replay.InitializeAsync(TestContext.Current.CancellationToken);
            var evaluator = new RepairEvaluator();
            await using var session = CreateIncrementalSession(
                plan,
                arguments,
                securityScope,
                evaluator);
            var replayAfterProcessRestart = await replay.ReadAsync(
                session.Identity,
                0,
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                LiveReplayReadStatus.Available,
                replayAfterProcessRestart.Status);
            Assert.Equal(2, replayAfterProcessRestart.LastSequence);
            Assert.All(
                replayAfterProcessRestart.Events,
                replayEvent => Assert.True(
                    LiveReplayJsonSerializer.VerifyIntegrity(replayEvent)));
            processRestartRecoveries++;

            await CommitInitialAsync(
                session,
                replay,
                TestContext.Current.CancellationToken,
                checked(replayAfterProcessRestart.LastSequence + 1));
            await AssertReplayCorruptionFailsClosedAsync(
                session.Identity,
                replay,
                TestContext.Current.CancellationToken);
            replayCorruptionDetections++;

            await AssertBlockedExecutionCancelsAsync(
                connectionString,
                plan,
                arguments,
                securityScope);
            cancellationRecoveries++;

            await AssertDisconnectedSessionRecoversAsync(
                connectionString,
                TestContext.Current.CancellationToken);
            disconnectRecoveries++;

            while (stopwatch.Elapsed < settings.Duration ||
                   evaluations < settings.MinimumEvaluations)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var transactionNumber = checked((uint)(evaluations + 1));
                var expectedName = (transactionNumber & 1) == 0
                    ? "Hopper"
                    : "Hamilton";
                var lifecycle = Stopwatch.StartNew();
                await ExecuteAsync(
                    administration,
                    $"UPDATE {PeopleTable} SET name = '{expectedName}' WHERE id = 2",
                    TestContext.Current.CancellationToken);
                await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
                    Source,
                    transactionNumber,
                    new BlueTuskLogSequenceNumber(transactionNumber));
                await using var evaluation =
                    await session.PrepareTransactionAsync(
                        delivery.Transaction,
                        TestContext.Current.CancellationToken);
                evaluations++;
                if (evaluation.Evaluation.Mode is
                    ContinuousGraphEvaluationMode.AuthoritativeRepair)
                {
                    authoritativeRepairs++;
                }

                var batch = evaluation.Evaluation.Batch;
                if (batch is null)
                {
                    incorrectlyOrderedResults++;
                    unreconciledResults++;
                }
                else
                {
                    if (!IsStrictlyOrdered(batch.Snapshot.Rows))
                    {
                        incorrectlyOrderedResults++;
                    }
                    if (!ContainsReconciledTarget(
                            batch.Snapshot.Rows,
                            expectedName))
                    {
                        unreconciledResults++;
                    }
                }

                try
                {
                    await PersistAsync(
                        session.Identity,
                        replay,
                        batch,
                        TestContext.Current.CancellationToken);
                }
                catch (LiveReplaySequenceException)
                {
                    replaySequenceErrors++;
                    throw;
                }

                await evaluation.CommitAsync(TestContext.Current.CancellationToken);
                await delivery.AcknowledgeAsync(TestContext.Current.CancellationToken);
                committed++;
                lifecycle.Stop();
                durations.Add(lifecycle.Elapsed.TotalMilliseconds);

                if (settings.Interval > TimeSpan.Zero)
                {
                    await Task.Delay(
                        settings.Interval,
                        TestContext.Current.CancellationToken);
                }
            }

            Assert.Equal(0, replaySequenceErrors);
            Assert.Equal(0, incorrectlyOrderedResults);
            Assert.Equal(0, unreconciledResults);
            Assert.True(evaluations >= settings.MinimumEvaluations);
            Assert.Equal(evaluations, committed);
            Assert.Equal(evaluations, authoritativeRepairs);
            Assert.True(processRestartRecoveries > 0);
            Assert.True(cancellationRecoveries > 0);
            Assert.True(disconnectRecoveries > 0);
            Assert.True(replayCorruptionDetections > 0);

            durations.Sort();
            var p95Index = Math.Max(
                0,
                (int)Math.Ceiling(durations.Count * 0.95) - 1);
            var p95Milliseconds = durations[p95Index];
            Assert.True(
                p95Milliseconds <= 1_000,
                $"Continuous Graph lifecycle P95 was {p95Milliseconds:N3} ms.");

            await WriteReportAsync(
                settings.ReportPath,
                new GraphEnduranceHarnessReport(
                    StartedAt: startedAt,
                    CompletedAt: DateTimeOffset.UtcNow,
                    RequestedDuration: settings.Duration,
                    ActualDuration: stopwatch.Elapsed,
                    Evaluations: evaluations,
                    CommittedEvaluations: committed,
                    AuthoritativeRepairs: authoritativeRepairs,
                    ProcessRestartRecoveries: processRestartRecoveries,
                    CancellationRecoveries: cancellationRecoveries,
                    CancellationCleanupVerified: cancellationRecoveries > 0,
                    DisconnectRecoveries: disconnectRecoveries,
                    ReplayCorruptionDetections: replayCorruptionDetections,
                    ReplaySequenceErrors: replaySequenceErrors,
                    IncorrectlyOrderedResults: incorrectlyOrderedResults,
                    UnreconciledResults: unreconciledResults,
                    LifecycleP95Milliseconds: p95Milliseconds),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await ResetSchemaAsync(administration);
            await DropReplaySchemaAsync(administration);
        }
    }

    private static ContinuousGraphIncrementalSession<FriendResult, int>
        CreateIncrementalSession(
            ContinuousGraphQueryPlan<FriendResult, int> plan,
            LiveQueryArguments arguments,
            LiveSecurityScope securityScope,
            RepairEvaluator evaluator) =>
        plan.CreateIncrementalSession(
            arguments,
            securityScope,
            evaluator,
            new ContinuousGraphIncrementalOptions<FriendResult, int>
            {
                ResultOrdering = Comparer<FriendResult>.Create(
                    static (left, right) =>
                        left.TargetId.CompareTo(right.TargetId)),
                KeyOrdering = Comparer<int>.Default,
                RepairAfterTransactions = int.MaxValue,
                MaximumRepairInterval = TimeSpan.FromDays(7),
            });

    private static async Task CommitInitialAsync(
        ContinuousGraphIncrementalSession<FriendResult, int> session,
        PostgreSqlLiveInvalidationStore replay,
        CancellationToken cancellationToken,
        long nextSequence = 1)
    {
        await using var initial =
            await session.PrepareInitialAsync(nextSequence, cancellationToken);
        await PersistAsync(
            session.Identity,
            replay,
            initial.Evaluation.Batch,
            cancellationToken);
        await initial.CommitAsync(cancellationToken);
    }

    private static async Task PersistAsync(
        LiveSubscriptionIdentity identity,
        PostgreSqlLiveInvalidationStore replay,
        LiveDiffBatch<FriendResult, int>? batch,
        CancellationToken cancellationToken)
    {
        if (batch is null || batch.Events.Count == 0)
        {
            return;
        }

        var payloads = batch.Events
            .Select(static graphEvent =>
                LiveReplayJsonSerializer.Serialize(graphEvent))
            .ToArray();
        var result = await replay.AppendReplayAsync(
            new LiveReplayAppendRequest(
                identity,
                checked(payloads[0].Sequence - 1),
                payloads),
            cancellationToken);
        if (result.Status is LiveReplayAppendStatus.SequenceConflict ||
            result.CurrentLastSequence != payloads[^1].Sequence)
        {
            throw new LiveReplaySequenceException(
                $"Graph replay ended at {result.CurrentLastSequence}, " +
                $"expected {payloads[^1].Sequence}.");
        }
    }

    private static async Task AssertReplayCorruptionFailsClosedAsync(
        LiveSubscriptionIdentity identity,
        PostgreSqlLiveInvalidationStore replay,
        CancellationToken cancellationToken)
    {
        var stored = await replay.ReadAsync(
            identity,
            0,
            1,
            cancellationToken);
        var replayEvent = Assert.Single(stored.Events);
        var corruptedPayload = replayEvent.Payload.ToArray();
        corruptedPayload[0] ^= 0x5a;
        Assert.Throws<ArgumentException>(() => LiveReplayEvent.Restore(
            replayEvent.Sequence,
            replayEvent.Kind,
            replayEvent.ContentType,
            corruptedPayload,
            replayEvent.IntegrityHash.Span));
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
        await using var command = blocker.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"LOCK TABLE {PeopleTable}, {FriendshipsTable} IN ACCESS EXCLUSIVE MODE";
        _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => plan.LivePlan.ExecuteAsync(
                new LiveQueryExecutionContext(arguments, securityScope),
                timeout.Token).AsTask());
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        var recovered = await plan.LivePlan.ExecuteAsync(
            new LiveQueryExecutionContext(arguments, securityScope),
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(recovered);
    }

    private static async Task AssertDisconnectedSessionRecoversAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var victim = new BlueTuskConnection(connectionString);
        await victim.OpenAsync(cancellationToken);
        await using var pidCommand = victim.CreateCommand();
        pidCommand.CommandText = "SELECT pg_backend_pid()";
        var pid = Convert.ToInt32(
            await pidCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        await using var killer = new BlueTuskConnection(connectionString);
        await killer.OpenAsync(cancellationToken);
        await using var killCommand = killer.CreateCommand();
        killCommand.CommandText = $"SELECT pg_terminate_backend({pid})";
        Assert.True(Convert.ToBoolean(
            await killCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture));

        await using var recovered = new BlueTuskConnection(connectionString);
        await recovered.OpenAsync(cancellationToken);
        await using var recoveryCommand = recovered.CreateCommand();
        recoveryCommand.CommandText = "SELECT 1";
        Assert.Equal(
            1,
            Convert.ToInt32(
                await recoveryCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture));
    }

    private static bool IsStrictlyOrdered(IReadOnlyList<FriendResult> rows)
    {
        var previousTargetId = int.MinValue;
        foreach (var row in rows)
        {
            if (row.TargetId <= previousTargetId)
            {
                return false;
            }

            previousTargetId = row.TargetId;
        }

        return true;
    }

    private static bool ContainsReconciledTarget(
        IReadOnlyList<FriendResult> rows,
        string expectedName)
    {
        foreach (var row in rows)
        {
            if (row.TargetId == 2)
            {
                return string.Equals(
                    row.TargetName,
                    expectedName,
                    StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static ContinuousGraphQueryDefinition<GraphContext, FriendResult, int>
        CreateDefinition() =>
        new(
            "continuous-graph-endurance",
            "continuous-graph-endurance",
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
                return context.PropertyGraph(GraphName)
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
                            "target", person => person.Name, result => result.TargetName))
                    .OrderBy(result => result.TargetId)
                    .Take(20);
            },
            result => result.TargetId,
            new FriendResultComparer());

    private static LiveQueryArguments CreateArguments(
        ContinuousGraphQueryPlan<FriendResult, int> plan) =>
        plan.Bind(new Dictionary<string, object?> { ["sourceId"] = 1 });

    private static LiveSecurityScope CreateSecurityScope() =>
        new("tenant:continuous-graph-endurance", "policy-v1");

    private static PostgreSqlLiveInvalidationStore CreateReplayStore(
        BlueTuskDataSource dataSource) =>
        new(new PostgreSqlLiveStoreOptions
        {
            ControlDataSource = dataSource,
            ControlSchema = ReplaySchema,
            ReplayRetentionWindow = TimeSpan.FromDays(2),
        });

    private static async Task CreateSchemaAsync(BlueTuskConnection connection)
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

    private static async Task ResetSchemaAsync(BlueTuskConnection connection)
    {
        if (connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(
            connection,
            $"""
            DROP PROPERTY GRAPH IF EXISTS {GraphName};
            DROP TABLE IF EXISTS {FriendshipsTable};
            DROP TABLE IF EXISTS {PeopleTable};
            """,
            TestContext.Current.CancellationToken);
    }

    private static Task DropReplaySchemaAsync(BlueTuskConnection connection) =>
        ExecuteAsync(
            connection,
            $"DROP SCHEMA IF EXISTS {ReplaySchema} CASCADE",
            TestContext.Current.CancellationToken);

    private static async Task ExecuteAsync(
        BlueTuskConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static EnduranceSettings ReadSettings()
    {
        var rawDuration = Environment.GetEnvironmentVariable(
            "BLUETUSK_GRAPH_ENDURANCE_DURATION");
        if (!TimeSpan.TryParse(
                rawDuration,
                CultureInfo.InvariantCulture,
                out var duration) ||
            duration < TimeSpan.FromSeconds(1))
        {
            throw SkipException.ForSkip(
                "BLUETUSK_GRAPH_ENDURANCE_DURATION is not configured to at least one second.");
        }

        var rawMinimum = Environment.GetEnvironmentVariable(
            "BLUETUSK_GRAPH_ENDURANCE_MIN_EVALUATIONS");
        var minimumEvaluations = string.IsNullOrWhiteSpace(rawMinimum)
            ? 1L
            : long.Parse(rawMinimum, NumberStyles.None, CultureInfo.InvariantCulture);
        if (minimumEvaluations <= 0)
        {
            throw new InvalidOperationException(
                "BLUETUSK_GRAPH_ENDURANCE_MIN_EVALUATIONS must be positive.");
        }

        var rawInterval = Environment.GetEnvironmentVariable(
            "BLUETUSK_GRAPH_ENDURANCE_INTERVAL_MS");
        var intervalMilliseconds = string.IsNullOrWhiteSpace(rawInterval)
            ? 250
            : int.Parse(rawInterval, NumberStyles.None, CultureInfo.InvariantCulture);
        if (intervalMilliseconds is < 0 or > 60_000)
        {
            throw new InvalidOperationException(
                "BLUETUSK_GRAPH_ENDURANCE_INTERVAL_MS must be between 0 and 60000.");
        }

        return new EnduranceSettings(
            duration,
            minimumEvaluations,
            TimeSpan.FromMilliseconds(intervalMilliseconds),
            Environment.GetEnvironmentVariable("BLUETUSK_GRAPH_ENDURANCE_REPORT"));
    }

    private static void RequirePhase(string required)
    {
        var phase = Environment.GetEnvironmentVariable(
            "BLUETUSK_GRAPH_ENDURANCE_PHASE");
        if (!string.Equals(phase, required, StringComparison.Ordinal))
        {
            throw SkipException.ForSkip(
                $"Continuous Graph endurance phase '{required}' was not requested.");
        }
    }

    private static void RequireSqlPgq(BlueTuskConnection connection)
    {
        if (connection.SupportsSqlPgq is not true)
        {
            throw SkipException.ForSkip(
                $"Continuous Graph endurance requires PostgreSQL 19 SQL/PGQ; " +
                $"the configured server is {connection.ServerVersion}.");
        }
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

    private static async ValueTask WriteReportAsync(
        string? path,
        GraphEnduranceHarnessReport report,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, FindRepositoryRoot());
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = File.Create(fullPath);
        await JsonSerializer.SerializeAsync(
            stream,
            report,
            ReportSerializerOptions,
            cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlueTusk.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the BlueTusk repository root.");
    }

    private sealed class RepairEvaluator :
        IContinuousGraphIncrementalEvaluator<FriendResult, int>
    {
        public ValueTask<ContinuousGraphIncrementalResult<FriendResult, int>>
            EvaluateAsync(
                ChangeTransaction transaction,
                LiveQueryExecutionContext context,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                ContinuousGraphIncrementalResult<FriendResult, int>.RequiresRepair(
                    "endurance-authoritative-reconciliation"));
        }
    }

    private sealed class GraphContextFactory(string connectionString) :
        IDbContextFactory<GraphContext>
    {
        public GraphContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<GraphContext>()
                .UseBlueTusk(connectionString)
                .Options;
            return new GraphContext(options);
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

    private sealed record EnduranceSettings(
        TimeSpan Duration,
        long MinimumEvaluations,
        TimeSpan Interval,
        string? ReportPath);

    private sealed record GraphEnduranceHarnessReport(
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        TimeSpan RequestedDuration,
        TimeSpan ActualDuration,
        long Evaluations,
        long CommittedEvaluations,
        long AuthoritativeRepairs,
        long ProcessRestartRecoveries,
        long CancellationRecoveries,
        bool CancellationCleanupVerified,
        long DisconnectRecoveries,
        long ReplayCorruptionDetections,
        long ReplaySequenceErrors,
        long IncorrectlyOrderedResults,
        long UnreconciledResults,
        double LifecycleP95Milliseconds);

    private sealed class LiveReplaySequenceException(string message) :
        Exception(message);
}
