using BlueTusk.ServiceTopology.Domain;

namespace BlueTusk.ServiceTopology.Application;

public interface ITopologyRepository
{
    ValueTask AddServiceAsync(ServiceNode service, CancellationToken cancellationToken);

    ValueTask AddDependencyAsync(ServiceDependency dependency, CancellationToken cancellationToken);

    ValueTask<ServiceNode?> FindServiceAsync(
        string tenantId,
        Guid serviceId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ServiceNode>> ListServicesAsync(
        string tenantId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ServiceDependency>> ListDependenciesAsync(
        string tenantId,
        CancellationToken cancellationToken);

    ValueTask AddIncidentAsync(TopologyIncident incident, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<TopologyIncident>> ListIncidentsAsync(
        string tenantId,
        CancellationToken cancellationToken);

    ValueTask SaveAsync(CancellationToken cancellationToken);
}

public sealed class TopologyService(ITopologyRepository repository)
{
    public async ValueTask<ServiceNode> RegisterAsync(
        string tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var service = ServiceNode.Create(tenantId, name);
        await repository.AddServiceAsync(service, cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        return service;
    }

    public async ValueTask<ServiceDependency> ConnectAsync(
        string tenantId,
        Guid sourceId,
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        _ = await repository.FindServiceAsync(tenantId, sourceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Source service was not found.");
        _ = await repository.FindServiceAsync(tenantId, destinationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Destination service was not found.");
        var dependency = ServiceDependency.Create(tenantId, sourceId, destinationId);
        await repository.AddDependencyAsync(dependency, cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        return dependency;
    }

    public async ValueTask<ServiceNode> ReportHealthAsync(
        string tenantId,
        Guid serviceId,
        ServiceHealth health,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var service = await repository.FindServiceAsync(tenantId, serviceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Service was not found.");
        service.ReportHealth(health, expectedVersion);
        await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        return service;
    }

    public async ValueTask<TopologyIncident> OpenIncidentAsync(
        string tenantId,
        Guid serviceId,
        string summary,
        CancellationToken cancellationToken)
    {
        _ = await repository.FindServiceAsync(tenantId, serviceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Service was not found.");
        var incident = new TopologyIncident(tenantId, serviceId, summary);
        await repository.AddIncidentAsync(incident, cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        return incident;
    }

    public async ValueTask<IReadOnlyList<Guid>> BlastRadiusAsync(
        string tenantId,
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        _ = await repository.FindServiceAsync(tenantId, serviceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Service was not found.");
        var dependencies = await repository.ListDependenciesAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        var visited = new HashSet<Guid> { serviceId };
        var queue = new Queue<Guid>();
        queue.Enqueue(serviceId);
        while (queue.TryDequeue(out var current))
        {
            foreach (var dependent in dependencies
                         .Where(edge => edge.DestinationId == current)
                         .Select(edge => edge.SourceId))
            {
                if (visited.Add(dependent)) { queue.Enqueue(dependent); }
            }
        }
        _ = visited.Remove(serviceId);
        return visited.Order().ToArray();
    }

    public async ValueTask<IReadOnlyList<Guid>> FindPathAsync(
        string tenantId,
        Guid sourceId,
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        var dependencies = await repository.ListDependenciesAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        var parent = new Dictionary<Guid, Guid?> { [sourceId] = null };
        var queue = new Queue<Guid>();
        queue.Enqueue(sourceId);
        while (queue.TryDequeue(out var current) && !parent.ContainsKey(destinationId))
        {
            foreach (var next in dependencies
                         .Where(edge => edge.SourceId == current)
                         .Select(edge => edge.DestinationId))
            {
                if (parent.TryAdd(next, current)) { queue.Enqueue(next); }
            }
        }
        if (!parent.ContainsKey(destinationId)) { return []; }
        var path = new List<Guid>();
        for (Guid? current = destinationId; current is not null; current = parent[current.Value])
        {
            path.Add(current.Value);
        }
        path.Reverse();
        return path;
    }
}
