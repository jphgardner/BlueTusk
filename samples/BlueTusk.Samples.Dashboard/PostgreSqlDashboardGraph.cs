using System.Globalization;
using BlueTusk.ContinuousGraph;
using BlueTusk.ControlPlane;
using BlueTusk.Live;
using Microsoft.EntityFrameworkCore;

internal sealed class PostgreSqlDashboardGraphRuntime
{
    private const string GraphName = "release_topology";
    private const string GraphSchema = "bluetusk_dashboard";
    private const string DatabaseIdentityValue =
        "PostgreSQL 19 Beta 3 / bluetusk_dashboard / release_topology";

    private PostgreSqlDashboardGraphRuntime(
        ContinuousGraphQueryRegistry queryRegistry,
        ContinuousGraphControlPlaneExecutionRegistry executionRegistry,
        string mode,
        string? queryFingerprint,
        string databaseIdentity,
        string dataClassification)
    {
        QueryRegistry = queryRegistry;
        ExecutionRegistry = executionRegistry;
        Mode = mode;
        QueryFingerprint = queryFingerprint;
        DatabaseIdentity = databaseIdentity;
        DataClassification = dataClassification;
    }

    internal ContinuousGraphQueryRegistry QueryRegistry { get; }

    internal ContinuousGraphControlPlaneExecutionRegistry ExecutionRegistry { get; }

    internal string Mode { get; }

    internal string DatabaseIdentity { get; }

    internal string DataClassification { get; }

    internal string? QueryFingerprint { get; }

    internal static async Task<PostgreSqlDashboardGraphRuntime> CreateAsync(
        string? connectionString,
        bool required,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (required)
            {
                throw new InvalidOperationException(
                    "The public dashboard requires BLUETUSK_GRAPH_CONNECTION_STRING.");
            }

            return new PostgreSqlDashboardGraphRuntime(
                new ContinuousGraphQueryRegistry(),
                new ContinuousGraphControlPlaneExecutionRegistry(),
                "unavailable-no-postgresql-registration",
                null,
                DatabaseIdentityValue,
                "No PostgreSQL graph registration is configured");
        }

        var contextFactory = new DashboardGraphContextFactory(connectionString);
        var definition = CreateDefinition();
        var plan = await ContinuousGraphQueryCompiler.CompileAsync(
                contextFactory,
                definition,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var queryRegistry = new ContinuousGraphQueryRegistry();
        if (!queryRegistry.Register(plan))
        {
            throw new InvalidOperationException(
                "The PostgreSQL dashboard graph query could not be registered.");
        }

        var executionRegistry = new ContinuousGraphControlPlaneExecutionRegistry();
        if (!executionRegistry.Register(
                plan,
                new Dictionary<string, object?> { ["minimumWeight"] = 0m },
                editableParameters: ["minimumWeight"],
                securityScopeFactory: static actor => new LiveSecurityScope(
                    $"dashboard:{actor.ActorId}",
                    "public-read-only-release-topology-v1"),
                projector: Project))
        {
            throw new InvalidOperationException(
                "The PostgreSQL dashboard graph execution could not be registered.");
        }

        return new PostgreSqlDashboardGraphRuntime(
            queryRegistry,
            executionRegistry,
            "postgresql-graph-table-live-query",
            plan.Fingerprint,
            DatabaseIdentityValue,
            "PostgreSQL-backed deployment topology captured from the active BlueTusk Kubernetes release");
    }

    private static ContinuousGraphQueryDefinition<
        DashboardGraphContext,
        DashboardGraphPath,
        string> CreateDefinition() =>
        new(
            "live-bluetusk-release-topology",
            DatabaseIdentityValue,
            "1",
            GraphName,
            GraphSchema,
            ["entities", "relationships"],
            [new LiveQueryParameter("minimumWeight", typeof(decimal))],
            new Dictionary<string, object?> { ["minimumWeight"] = 0m },
            2_000,
            (context, arguments) =>
            {
                var minimumWeight = arguments.Get<decimal>("minimumWeight");
                return context.PropertyGraph(GraphName, GraphSchema)
                    .Match(pattern => pattern
                        .Vertex<DashboardGraphEntity>("source")
                        .Outgoing<DashboardGraphRelationship>("relationship")
                        .Vertex<DashboardGraphEntity>("target"))
                    .Select<DashboardGraphPath>(projection => projection
                        .Property<DashboardGraphRelationship, string>(
                            "relationship",
                            relationship => relationship.Id,
                            result => result.RelationshipId)
                        .Property<DashboardGraphRelationship, string>(
                            "relationship",
                            relationship => relationship.Kind,
                            result => result.RelationshipKind)
                        .Property<DashboardGraphRelationship, decimal>(
                            "relationship",
                            relationship => relationship.Weight,
                            result => result.Weight)
                        .Property<DashboardGraphRelationship, DateTimeOffset>(
                            "relationship",
                            relationship => relationship.ObservedAt,
                            result => result.ObservedAt)
                        .Property<DashboardGraphRelationship, string>(
                            "relationship",
                            relationship => relationship.Detail,
                            result => result.RelationshipDetail)
                        .Property<DashboardGraphEntity, string>(
                            "source",
                            entity => entity.Id,
                            result => result.SourceId)
                        .Property<DashboardGraphEntity, string>(
                            "source",
                            entity => entity.DisplayName,
                            result => result.SourceName)
                        .Property<DashboardGraphEntity, string>(
                            "source",
                            entity => entity.Kind,
                            result => result.SourceKind)
                        .Property<DashboardGraphEntity, string>(
                            "source",
                            entity => entity.Status,
                            result => result.SourceStatus)
                        .Property<DashboardGraphEntity, string>(
                            "source",
                            entity => entity.Detail,
                            result => result.SourceDetail)
                        .Property<DashboardGraphEntity, string>(
                            "source",
                            entity => entity.Provenance,
                            result => result.SourceProvenance)
                        .Property<DashboardGraphEntity, string>(
                            "target",
                            entity => entity.Id,
                            result => result.TargetId)
                        .Property<DashboardGraphEntity, string>(
                            "target",
                            entity => entity.DisplayName,
                            result => result.TargetName)
                        .Property<DashboardGraphEntity, string>(
                            "target",
                            entity => entity.Kind,
                            result => result.TargetKind)
                        .Property<DashboardGraphEntity, string>(
                            "target",
                            entity => entity.Status,
                            result => result.TargetStatus)
                        .Property<DashboardGraphEntity, string>(
                            "target",
                            entity => entity.Detail,
                            result => result.TargetDetail)
                        .Property<DashboardGraphEntity, string>(
                            "target",
                            entity => entity.Provenance,
                            result => result.TargetProvenance))
                    .Where(result => result.Weight >= minimumWeight)
                    .OrderBy(result => result.RelationshipKind)
                    .ThenBy(result => result.RelationshipId)
                    .Take(2_000);
            },
            result => result.RelationshipId,
            DashboardGraphPathComparer.Instance);

    private static ControlPlaneContinuousGraphFragment Project(DashboardGraphPath path) =>
        new(
            [Node(
                 path.SourceId,
                 path.SourceName,
                 path.SourceKind,
                 path.SourceStatus,
                 path.SourceDetail,
                 path.SourceProvenance),
             Node(
                 path.TargetId,
                 path.TargetName,
                 path.TargetKind,
                 path.TargetStatus,
                 path.TargetDetail,
                 path.TargetProvenance)],
            [new ControlPlaneContinuousGraphEdge(
                path.RelationshipId,
                path.SourceId,
                path.TargetId,
                path.RelationshipKind.Replace('_', ' '),
                path.RelationshipKind,
                true,
                [new ControlPlaneContinuousGraphProperty(
                     "weight",
                     path.Weight.ToString(CultureInfo.InvariantCulture)),
                 new ControlPlaneContinuousGraphProperty(
                     "observedAt",
                     path.ObservedAt.ToString("O", CultureInfo.InvariantCulture)),
                 new ControlPlaneContinuousGraphProperty(
                     "detail",
                     path.RelationshipDetail),
                 new ControlPlaneContinuousGraphProperty(
                     "queryEngine",
                     "PostgreSQL GRAPH_TABLE")])]);

    private static ControlPlaneContinuousGraphNode Node(
        string id,
        string label,
        string category,
        string status,
        string detail,
        string provenance) =>
        new(
            id,
            label,
            category,
            [new ControlPlaneContinuousGraphProperty("status", status),
             new ControlPlaneContinuousGraphProperty("detail", detail),
             new ControlPlaneContinuousGraphProperty("provenance", provenance),
             new ControlPlaneContinuousGraphProperty(
                 "storage",
                 "PostgreSQL 19 Beta 3 / bluetusk_dashboard")]);
}

internal sealed class DashboardGraphContextFactory(string connectionString) :
    IDbContextFactory<DashboardGraphContext>
{
    public DashboardGraphContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DashboardGraphContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new DashboardGraphContext(options);
    }
}

internal sealed class DashboardGraphContext(DbContextOptions<DashboardGraphContext> options) :
    DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DashboardGraphEntity>(entity =>
        {
            entity.ToTable("graph_entities", "bluetusk_dashboard");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).HasColumnName("id");
            entity.Property(value => value.DisplayName).HasColumnName("display_name");
            entity.Property(value => value.Kind).HasColumnName("kind");
            entity.Property(value => value.Status).HasColumnName("status");
            entity.Property(value => value.Detail).HasColumnName("detail");
            entity.Property(value => value.Provenance).HasColumnName("provenance");
        });
        modelBuilder.Entity<DashboardGraphRelationship>(entity =>
        {
            entity.ToTable("graph_relationships", "bluetusk_dashboard");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).HasColumnName("id");
            entity.Property(value => value.SourceId).HasColumnName("source_id");
            entity.Property(value => value.TargetId).HasColumnName("target_id");
            entity.Property(value => value.Kind).HasColumnName("kind");
            entity.Property(value => value.Weight).HasColumnName("weight");
            entity.Property(value => value.ObservedAt).HasColumnName("observed_at");
            entity.Property(value => value.Detail).HasColumnName("detail");
        });
        modelBuilder.HasPropertyGraph(
            "release_topology",
            graph =>
            {
                graph.Vertex<DashboardGraphEntity>("entities", vertex => vertex
                    .HasLabel("component")
                    .HasKey(entity => entity.Id)
                    .Properties(entity => new
                    {
                        entity.Id,
                        entity.DisplayName,
                        entity.Kind,
                        entity.Status,
                        entity.Detail,
                        entity.Provenance,
                    }));
                graph.Edge<DashboardGraphRelationship>("relationships", edge => edge
                    .HasLabel("dependency")
                    .HasKey(relationship => relationship.Id)
                    .Properties(relationship => new
                    {
                        relationship.Id,
                        relationship.SourceId,
                        relationship.TargetId,
                        relationship.Kind,
                        relationship.Weight,
                        relationship.ObservedAt,
                        relationship.Detail,
                    })
                    .HasSource<DashboardGraphEntity>(
                        relationship => relationship.SourceId,
                        entity => entity.Id)
                    .HasDestination<DashboardGraphEntity>(
                        relationship => relationship.TargetId,
                        entity => entity.Id));
            },
            schema: "bluetusk_dashboard");
    }
}

internal sealed class DashboardGraphEntity
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string Provenance { get; set; } = string.Empty;
}

internal sealed class DashboardGraphRelationship
{
    public string Id { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public decimal Weight { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public string Detail { get; set; } = string.Empty;
}

internal sealed class DashboardGraphPath
{
    public string RelationshipId { get; set; } = string.Empty;

    public string RelationshipKind { get; set; } = string.Empty;

    public decimal Weight { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public string RelationshipDetail { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string SourceName { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public string SourceStatus { get; set; } = string.Empty;

    public string SourceDetail { get; set; } = string.Empty;

    public string SourceProvenance { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;

    public string TargetName { get; set; } = string.Empty;

    public string TargetKind { get; set; } = string.Empty;

    public string TargetStatus { get; set; } = string.Empty;

    public string TargetDetail { get; set; } = string.Empty;

    public string TargetProvenance { get; set; } = string.Empty;
}

internal sealed class DashboardGraphPathComparer : IEqualityComparer<DashboardGraphPath>
{
    internal static DashboardGraphPathComparer Instance { get; } = new();

    public bool Equals(DashboardGraphPath? x, DashboardGraphPath? y) =>
        ReferenceEquals(x, y) ||
        (x is not null && y is not null &&
         string.Equals(x.RelationshipId, y.RelationshipId, StringComparison.Ordinal) &&
         string.Equals(x.RelationshipKind, y.RelationshipKind, StringComparison.Ordinal) &&
         x.Weight == y.Weight &&
         x.ObservedAt == y.ObservedAt &&
         string.Equals(x.RelationshipDetail, y.RelationshipDetail, StringComparison.Ordinal) &&
         string.Equals(x.SourceId, y.SourceId, StringComparison.Ordinal) &&
         string.Equals(x.SourceName, y.SourceName, StringComparison.Ordinal) &&
         string.Equals(x.SourceKind, y.SourceKind, StringComparison.Ordinal) &&
         string.Equals(x.SourceStatus, y.SourceStatus, StringComparison.Ordinal) &&
         string.Equals(x.SourceDetail, y.SourceDetail, StringComparison.Ordinal) &&
         string.Equals(x.SourceProvenance, y.SourceProvenance, StringComparison.Ordinal) &&
         string.Equals(x.TargetId, y.TargetId, StringComparison.Ordinal) &&
         string.Equals(x.TargetName, y.TargetName, StringComparison.Ordinal) &&
         string.Equals(x.TargetKind, y.TargetKind, StringComparison.Ordinal) &&
         string.Equals(x.TargetStatus, y.TargetStatus, StringComparison.Ordinal) &&
         string.Equals(x.TargetDetail, y.TargetDetail, StringComparison.Ordinal) &&
         string.Equals(x.TargetProvenance, y.TargetProvenance, StringComparison.Ordinal));

    public int GetHashCode(DashboardGraphPath value) =>
        HashCode.Combine(
            value.RelationshipId,
            value.RelationshipKind,
            value.Weight,
            value.ObservedAt,
            value.SourceId,
            value.TargetId);
}
