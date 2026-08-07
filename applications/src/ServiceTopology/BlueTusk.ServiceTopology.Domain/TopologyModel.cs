namespace BlueTusk.ServiceTopology.Domain;

public enum ServiceHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unavailable,
}

public sealed class ServiceNode
{
    private ServiceNode()
    {
    }

    private ServiceNode(Guid id, string tenantId, string name)
    {
        Id = id;
        TenantId = Required(tenantId, nameof(tenantId));
        Name = Required(name, nameof(name));
        Health = ServiceHealth.Unknown;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public ServiceHealth Health { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ServiceNode Create(string tenantId, string name, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), tenantId, name);

    public void ReportHealth(ServiceHealth health, long expectedVersion)
    {
        if (expectedVersion != Version)
        {
            throw new InvalidOperationException(
                $"Expected service version {expectedVersion}, but found {Version}.");
        }

        Health = health;
        Version++;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

public sealed class ServiceDependency
{
    private ServiceDependency()
    {
    }

    private ServiceDependency(Guid id, string tenantId, Guid sourceId, Guid destinationId)
    {
        if (sourceId == destinationId)
        {
            throw new ArgumentException("A service cannot depend on itself.", nameof(destinationId));
        }

        Id = id;
        TenantId = tenantId;
        SourceId = sourceId;
        DestinationId = destinationId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = string.Empty;

    public Guid SourceId { get; private set; }

    public Guid DestinationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static ServiceDependency Create(
        string tenantId,
        Guid sourceId,
        Guid destinationId,
        Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return new(id ?? Guid.NewGuid(), tenantId.Trim(), sourceId, destinationId);
    }
}

public sealed class TopologyIncident
{
    private TopologyIncident()
    {
    }

    public TopologyIncident(string tenantId, Guid serviceId, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        Id = Guid.NewGuid();
        TenantId = tenantId.Trim();
        ServiceId = serviceId;
        Summary = summary.Trim();
        OpenedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = string.Empty;

    public Guid ServiceId { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public void Close() => ClosedAt ??= DateTimeOffset.UtcNow;
}
