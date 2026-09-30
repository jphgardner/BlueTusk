using BlueTusk.Live;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.ContinuousGraph.Tests;

public sealed class ContinuousGraphQueryCompilerTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public async Task Compiler_derives_only_registered_graph_dependencies_and_stable_live_plan()
    {
        var factory = new GraphContextFactory();
        var probe = new SupportedProbe();
        var definition = CreateDefinition(["people", "friendships"]);

        var first = await ContinuousGraphQueryCompiler.CompileAsync(factory, definition, probe);
        var second = await ContinuousGraphQueryCompiler.CompileAsync(factory, definition, probe);

        Assert.Equal("social", first.GraphName);
        Assert.Equal("graphs", first.GraphSchema);
        Assert.Equal(["people", "friendships"], first.ElementTableAliases);
        Assert.Equal(
            ["graphs.friendships", "graphs.people"],
            first.Dependencies.Select(static dependency => dependency.ToString()));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.True(first.LivePlan.Capabilities.HasFlag(LiveQueryCapabilities.BoundedTake));
        Assert.True(first.LivePlan.Capabilities.HasFlag(LiveQueryCapabilities.DeterministicOrdering));
        Assert.False(first.LivePlan.Capabilities.HasFlag(LiveQueryCapabilities.SingleTable));
        Assert.True(first.MaintenanceCapabilities.HasFlag(
            ContinuousGraphMaintenanceCapabilities.AuthoritativeDelta));
        Assert.Equal(2, probe.InvocationCount);

        var arguments = first.Bind(new Dictionary<string, object?> { ["sourceId"] = 11 });
        await using var session = first.CreateSession(
            arguments,
            new LiveSecurityScope("tenant:alpha", "policy-v1"),
            new NoChangesInvalidationLog());
        Assert.Equal(first.Fingerprint, session.Identity.QueryPlanFingerprint);
    }

    [Theory]
    [InlineData(GraphPatternKind.BoundedPath, "1..3")]
    [InlineData(GraphPatternKind.Undirected, "Undirected")]
    public async Task Compiler_forces_authoritative_repair_for_broad_impact_patterns(
        GraphPatternKind patternKind,
        string impactEvidence)
    {
        var plan = await ContinuousGraphQueryCompiler.CompileAsync(
            new GraphContextFactory(),
            CreateDefinition(["people", "friendships"], patternKind: patternKind),
            new SupportedProbe());
        var directed = await ContinuousGraphQueryCompiler.CompileAsync(
            new GraphContextFactory(),
            CreateDefinition(["people", "friendships"]),
            new SupportedProbe());

        Assert.Equal(
            ContinuousGraphMaintenanceCapabilities.AuthoritativeRepair,
            plan.MaintenanceCapabilities);
        Assert.Contains(
            plan.ImpactPlan.PatternElements,
            element => element.Contains(impactEvidence, StringComparison.Ordinal));
        Assert.NotEqual(directed.Fingerprint, plan.Fingerprint);
    }

    [Fact]
    public async Task Compiler_rejects_unknown_graph_element_alias()
    {
        var exception = await Assert.ThrowsAsync<ContinuousGraphQueryRegistrationException>(
            () => ContinuousGraphQueryCompiler.CompileAsync(
                new GraphContextFactory(),
                CreateDefinition(["missing"]),
                new SupportedProbe()).AsTask());

        Assert.Contains("has no element table alias 'missing'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compiler_fails_closed_when_PostgreSQL_19_capability_is_unavailable()
    {
        var exception = await Assert.ThrowsAsync<ContinuousGraphCapabilityException>(
            () => ContinuousGraphQueryCompiler.CompileAsync(
                new GraphContextFactory(),
                CreateDefinition(["people"]),
                new UnsupportedProbe()).AsTask());

        Assert.Contains("PostgreSQL 19", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(QueryDefect.MissingTake, "bounded Take")]
    [InlineData(QueryDefect.MissingKeyOrder, "includes result key")]
    [InlineData(QueryDefect.Skip, "Skip")]
    public async Task Compiler_rejects_unbounded_nondeterministic_or_unsupported_shapes(
        QueryDefect defect,
        string expected)
    {
        var exception = await Assert.ThrowsAsync<ContinuousGraphQueryRegistrationException>(
            () => ContinuousGraphQueryCompiler.CompileAsync(
                new GraphContextFactory(),
                CreateDefinition(["people"], defect),
                new SupportedProbe()).AsTask());

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    private static ContinuousGraphQueryDefinition<GraphContext, FriendResult, int> CreateDefinition(
        string[] aliases,
        QueryDefect defect = QueryDefect.None,
        GraphPatternKind patternKind = GraphPatternKind.Directed) =>
        new(
            "friends",
            "primary",
            "1",
            "social",
            "graphs",
            aliases,
            [new LiveQueryParameter("sourceId", typeof(int))],
            new Dictionary<string, object?> { ["sourceId"] = 7 },
            20,
            (context, arguments) =>
            {
                var sourceId = arguments.Get<int>("sourceId");
                var root = context.PropertyGraph("social", "graphs");
                var match = patternKind switch
                {
                    GraphPatternKind.BoundedPath => root.Match(pattern => pattern
                        .Vertex<Person>("source", person => person.Id == sourceId)
                        .OutgoingPath<Friendship>("relationship", 1, 3)
                        .Vertex<Person>("target")),
                    GraphPatternKind.Undirected => root.Match(pattern => pattern
                        .Vertex<Person>("source", person => person.Id == sourceId)
                        .Undirected<Friendship>("relationship")
                        .Vertex<Person>("target")),
                    _ => root.Match(pattern => pattern
                        .Vertex<Person>("source", person => person.Id == sourceId)
                        .Outgoing<Friendship>("relationship")
                        .Vertex<Person>("target")),
                };
                var query = match
                    .Select<FriendResult>(projection => projection
                        .Property<Person, int>("source", person => person.Id, result => result.SourceId)
                        .Property<Person, int>("target", person => person.Id, result => result.TargetId)
                        .Property<Person, string>("target", person => person.Name, result => result.TargetName));
                return defect switch
                {
                    QueryDefect.MissingTake => query.OrderBy(result => result.TargetId),
                    QueryDefect.MissingKeyOrder => query.OrderBy(result => result.TargetName).Take(20),
                    QueryDefect.Skip => query.OrderBy(result => result.TargetId).Skip(1).Take(20),
                    _ => query
                        .Where(result => result.TargetName != "blocked")
                        .OrderBy(result => result.TargetName)
                        .ThenBy(result => result.TargetId)
                        .Take(20),
                };
            },
            result => result.TargetId,
            new FriendResultComparer());

    public enum QueryDefect
    {
        None,
        MissingTake,
        MissingKeyOrder,
        Skip,
    }

    public enum GraphPatternKind
    {
        Directed,
        BoundedPath,
        Undirected,
    }

    private sealed class SupportedProbe : IContinuousGraphCapabilityProbe
    {
        public int InvocationCount { get; private set; }

        public ValueTask EnsureSupportedAsync(
            DbContext context,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnsupportedProbe : IContinuousGraphCapabilityProbe
    {
        public ValueTask EnsureSupportedAsync(
            DbContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new ContinuousGraphCapabilityException(
                "Continuous Graph requires PostgreSQL 19 SQL/PGQ support."));
    }

    private sealed class NoChangesInvalidationLog : ILiveInvalidationLog
    {
        public ValueTask<LiveInvalidationCursor> GetCurrentCursorAsync(
            string databaseIdentity,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveInvalidationCursor(0));

        public ValueTask<bool> HasChangesAsync(
            string databaseIdentity,
            IReadOnlyCollection<LiveTableDependency> dependencies,
            LiveInvalidationCursor afterExclusive,
            LiveInvalidationCursor throughInclusive,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class GraphContextFactory : IDbContextFactory<GraphContext>
    {
        public GraphContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<GraphContext>()
                .UseBlueTusk(ConnectionString)
                .Options;
            return new GraphContext(options);
        }
    }

    private sealed class GraphContext(DbContextOptions<GraphContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Person>(entity =>
            {
                entity.ToTable("people", "graphs");
                entity.HasKey(person => person.Id);
                entity.Property(person => person.Id).HasColumnName("id");
                entity.Property(person => person.Name).HasColumnName("name");
            });
            modelBuilder.Entity<Friendship>(entity =>
            {
                entity.ToTable("friendships", "graphs");
                entity.HasKey(friendship => friendship.Id);
                entity.Property(friendship => friendship.Id).HasColumnName("id");
                entity.Property(friendship => friendship.FromPersonId).HasColumnName("from_id");
                entity.Property(friendship => friendship.ToPersonId).HasColumnName("to_id");
            });
            modelBuilder.HasPropertyGraph(
                "social",
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
                        .HasSource<Person>(friendship => friendship.FromPersonId, person => person.Id)
                        .HasDestination<Person>(friendship => friendship.ToPersonId, person => person.Id));
                },
                schema: "graphs");
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

        public int TargetId { get; set; }

        public string TargetName { get; set; } = string.Empty;
    }

    private sealed class FriendResultComparer : IEqualityComparer<FriendResult>
    {
        public bool Equals(FriendResult? x, FriendResult? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             x.SourceId == y.SourceId &&
             x.TargetId == y.TargetId &&
             string.Equals(x.TargetName, y.TargetName, StringComparison.Ordinal));

        public int GetHashCode(FriendResult obj) =>
            HashCode.Combine(obj.SourceId, obj.TargetId, obj.TargetName);
    }
}
