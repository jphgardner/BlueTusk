using BlueTusk.ContinuousGraph;
using BlueTusk.Data;
using BlueTusk.Live;
using BlueTusk.Live.Testing;
using Microsoft.EntityFrameworkCore;

var connectionString = Environment.GetEnvironmentVariable(
    "BLUETUSK_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "Set BLUETUSK_CONNECTION_STRING to a PostgreSQL 19 database.");
    return 1;
}

await using var connection = new BlueTuskConnection(connectionString);
await connection.OpenAsync();
if (connection.SupportsSqlPgq is not true)
{
    Console.Error.WriteLine(
        $"Continuous Graph requires PostgreSQL 19 SQL/PGQ; " +
        $"the server is {connection.ServerVersion}.");
    return 2;
}

await ExecuteAsync(
    """
    CREATE TEMP TABLE network_services (
        id int4 PRIMARY KEY,
        name text NOT NULL,
        health text NOT NULL);
    CREATE TEMP TABLE network_links (
        id int4 PRIMARY KEY,
        source_id int4 NOT NULL REFERENCES network_services (id),
        destination_id int4 NOT NULL REFERENCES network_services (id),
        latency_ms int4 NOT NULL);
    INSERT INTO network_services VALUES
        (1, 'gateway', 'healthy'),
        (2, 'orders', 'healthy'),
        (3, 'payments', 'degraded');
    INSERT INTO network_links VALUES
        (10, 1, 2, 12), (11, 1, 3, 42);
    CREATE TEMP PROPERTY GRAPH network_graph
        VERTEX TABLES (
            network_services AS services
            KEY (id)
            LABEL service PROPERTIES (
                id AS "Id", name AS "Name", health AS "Health"))
        EDGE TABLES (
            network_links AS links
            KEY (id)
            SOURCE KEY (source_id) REFERENCES services (id)
            DESTINATION KEY (destination_id) REFERENCES services (id)
            LABEL link PROPERTIES (
                id AS "Id",
                source_id AS "SourceId",
                destination_id AS "DestinationId",
                latency_ms AS "LatencyMilliseconds"));
    """);

try
{
    var contextFactory = new NetworkContextFactory(connection);
    var definition =
        new ContinuousGraphQueryDefinition<NetworkContext, ServiceDependency, int>(
            "gateway-dependencies",
            "network-demo",
            "1",
            "network_graph",
            graphSchema: null,
            ["services", "links"],
            [new LiveQueryParameter("serviceId", typeof(int))],
            new Dictionary<string, object?> { ["serviceId"] = 1 },
            20,
            (context, arguments) =>
            {
                var serviceId = arguments.Get<int>("serviceId");
                return context.PropertyGraph("network_graph")
                    .Match(pattern => pattern
                        .Vertex<Service>("source", service => service.Id == serviceId)
                        .Outgoing<ServiceLink>("link")
                        .Vertex<Service>("target"))
                    .Select<ServiceDependency>(projection => projection
                        .Property<ServiceLink, int>(
                            "link", link => link.Id, result => result.LinkId)
                        .Property<Service, int>(
                            "target", service => service.Id, result => result.TargetId)
                        .Property<Service, string>(
                            "target", service => service.Name, result => result.TargetName)
                        .Property<Service, string>(
                            "target", service => service.Health, result => result.Health)
                        .Property<ServiceLink, int>(
                            "link",
                            link => link.LatencyMilliseconds,
                            result => result.LatencyMilliseconds))
                    .OrderByDescending(result => result.LatencyMilliseconds)
                    .ThenBy(result => result.LinkId)
                    .Take(20);
            },
            result => result.LinkId,
            ServiceDependencyComparer.Instance);
    var plan = await ContinuousGraphQueryCompiler.CompileAsync(
        contextFactory,
        definition);
    var arguments = plan.Bind(
        new Dictionary<string, object?> { ["serviceId"] = 1 });
    var invalidations = new InMemoryLiveInvalidationLog();
    await using var session = plan.CreateSession(
        arguments,
        new LiveSecurityScope("tenant:network-demo", "policy-v1"),
        invalidations);
    var initial = await session.StartAsync();
    Print("Initial gateway dependencies", initial);

    await ExecuteAsync(
        "UPDATE network_services SET health = 'healthy' WHERE id = 3");
    _ = invalidations.Append("network-demo", plan.Dependencies);
    var refresh = await session.RefreshToCurrentAsync();
    if (refresh is not null)
    {
        Print("After payments recovered", refresh);
    }
}
finally
{
    await ExecuteAsync("DROP PROPERTY GRAPH IF EXISTS network_graph");
}

return 0;

async Task ExecuteAsync(string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    _ = await command.ExecuteNonQueryAsync();
}

static void Print(string heading, LiveDiffBatch<ServiceDependency, int> batch)
{
    Console.WriteLine(heading);
    if (batch.Events.Any(graphEvent =>
            graphEvent.Kind is LiveEventKind.InitialResult or LiveEventKind.ResultReset))
    {
        foreach (var row in batch.Snapshot.Rows)
        {
            Console.WriteLine(
                $"  snapshot: link={row.LinkId}, service={row.TargetName}, " +
                $"health={row.Health}, latency={row.LatencyMilliseconds}ms");
        }

        return;
    }

    foreach (var graphEvent in batch.Events)
    {
        Console.WriteLine(
            $"  {graphEvent.Kind}: link={graphEvent.Key}, " +
            $"service={graphEvent.Row?.TargetName}, health={graphEvent.Row?.Health}, " +
            $"latency={graphEvent.Row?.LatencyMilliseconds}ms");
    }
}

internal sealed class NetworkContextFactory(BlueTuskConnection connection) :
    IDbContextFactory<NetworkContext>
{
    public NetworkContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<NetworkContext>()
            .UseBlueTusk(connection)
            .Options;
        return new NetworkContext(options);
    }
}

internal sealed class NetworkContext(DbContextOptions<NetworkContext> options) :
    DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Service>(entity =>
        {
            entity.ToTable("network_services");
            entity.HasKey(service => service.Id);
            entity.Property(service => service.Id).HasColumnName("id");
            entity.Property(service => service.Name).HasColumnName("name");
            entity.Property(service => service.Health).HasColumnName("health");
        });
        modelBuilder.Entity<ServiceLink>(entity =>
        {
            entity.ToTable("network_links");
            entity.HasKey(link => link.Id);
            entity.Property(link => link.Id).HasColumnName("id");
            entity.Property(link => link.SourceId).HasColumnName("source_id");
            entity.Property(link => link.DestinationId).HasColumnName("destination_id");
            entity.Property(link => link.LatencyMilliseconds).HasColumnName("latency_ms");
        });
        modelBuilder.HasPropertyGraph(
            "network_graph",
            graph =>
            {
                graph.Vertex<Service>("services", vertex => vertex
                    .HasLabel("service")
                    .HasKey(service => service.Id)
                    .Properties(service => new
                    {
                        service.Id,
                        service.Name,
                        service.Health,
                    }));
                graph.Edge<ServiceLink>("links", edge => edge
                    .HasLabel("link")
                    .HasKey(link => link.Id)
                    .Properties(link => new
                    {
                        link.Id,
                        link.SourceId,
                        link.DestinationId,
                        link.LatencyMilliseconds,
                    })
                    .HasSource<Service>(
                        link => link.SourceId,
                        service => service.Id)
                    .HasDestination<Service>(
                        link => link.DestinationId,
                        service => service.Id));
            });
    }
}

internal sealed class Service
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Health { get; set; } = string.Empty;
}

internal sealed class ServiceLink
{
    public int Id { get; set; }

    public int SourceId { get; set; }

    public int DestinationId { get; set; }

    public int LatencyMilliseconds { get; set; }
}

internal sealed class ServiceDependency
{
    public int LinkId { get; set; }

    public int TargetId { get; set; }

    public string TargetName { get; set; } = string.Empty;

    public string Health { get; set; } = string.Empty;

    public int LatencyMilliseconds { get; set; }
}

internal sealed class ServiceDependencyComparer :
    IEqualityComparer<ServiceDependency>
{
    public static ServiceDependencyComparer Instance { get; } = new();

    public bool Equals(ServiceDependency? x, ServiceDependency? y) =>
        ReferenceEquals(x, y) ||
        (x is not null && y is not null &&
         x.LinkId == y.LinkId &&
         x.TargetId == y.TargetId &&
         string.Equals(x.TargetName, y.TargetName, StringComparison.Ordinal) &&
         string.Equals(x.Health, y.Health, StringComparison.Ordinal) &&
         x.LatencyMilliseconds == y.LatencyMilliseconds);

    public int GetHashCode(ServiceDependency obj) =>
        HashCode.Combine(
            obj.LinkId,
            obj.TargetId,
            obj.TargetName,
            obj.Health,
            obj.LatencyMilliseconds);
}
