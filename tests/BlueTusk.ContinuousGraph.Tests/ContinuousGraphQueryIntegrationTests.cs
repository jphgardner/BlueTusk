using System.Diagnostics;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Live;
using BlueTusk.Live.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.ContinuousGraph.Tests;

public sealed class ContinuousGraphQueryIntegrationTests
{
    private const string PeopleTable = "bluetusk_continuous_graph_people";
    private const string FriendshipsTable = "bluetusk_continuous_graph_friendships";
    private const string GraphName = "bluetusk_continuous_graph";

    [Fact]
    public async Task PostgreSQL_19_requeries_affected_graphs_and_cancels_blocked_execution()
    {
        var connectionString = GetConnectionString();
        await using var administration = new BlueTuskConnection(connectionString);
        await administration.OpenAsync(TestContext.Current.CancellationToken);
        if (administration.SupportsSqlPgq is not true)
        {
            throw SkipException.ForSkip(
                $"Continuous Graph live acceptance requires PostgreSQL 19 SQL/PGQ; " +
                $"the configured server is {administration.ServerVersion}.");
        }

        await ResetSchemaAsync(administration);
        try
        {
            await CreateSchemaAsync(administration);
            var factory = new GraphContextFactory(connectionString);
            var plan = await ContinuousGraphQueryCompiler.CompileAsync(
                factory,
                CreateDefinition(),
                cancellationToken: TestContext.Current.CancellationToken);
            var arguments = plan.Bind(
                new Dictionary<string, object?> { ["sourceId"] = 1 });
            var securityScope = new LiveSecurityScope("tenant:integration", "policy-v1");
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
            await ResetSchemaAsync(administration);
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
        CreateDefinition() =>
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
        await ExecuteAsync(
            connection,
            $"""
            DROP PROPERTY GRAPH IF EXISTS {GraphName};
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
}
