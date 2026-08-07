using BlueTusk.ServiceTopology.Application;
using BlueTusk.ServiceTopology.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.ServiceTopology.Infrastructure;

public sealed class TopologyDbContext(DbContextOptions<TopologyDbContext> options) : DbContext(options)
{
    public DbSet<ServiceNode> Services => Set<ServiceNode>();

    public DbSet<ServiceDependency> Dependencies => Set<ServiceDependency>();

    public DbSet<TopologyIncident> Incidents => Set<TopologyIncident>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("topology");
        modelBuilder.Entity<ServiceNode>(entity =>
        {
            entity.ToTable("services");
            entity.HasKey(service => service.Id);
            entity.Property(service => service.TenantId).HasMaxLength(80);
            entity.Property(service => service.Name).HasMaxLength(200);
            entity.Property(service => service.Version).IsConcurrencyToken();
            entity.HasIndex(service => new { service.TenantId, service.Name }).IsUnique();
        });
        modelBuilder.Entity<ServiceDependency>(entity =>
        {
            entity.ToTable("dependencies");
            entity.HasKey(dependency => dependency.Id);
            entity.Property(dependency => dependency.TenantId).HasMaxLength(80);
            entity.HasIndex(dependency => new
            {
                dependency.TenantId,
                dependency.SourceId,
                dependency.DestinationId,
            }).IsUnique();
        });
        modelBuilder.Entity<TopologyIncident>(entity =>
        {
            entity.ToTable("incidents");
            entity.HasKey(incident => incident.Id);
            entity.Property(incident => incident.TenantId).HasMaxLength(80);
            entity.Property(incident => incident.Summary).HasMaxLength(500);
        });
        modelBuilder.HasPropertyGraph("service_topology_graph", graph =>
        {
            graph.Vertex<ServiceNode>("services", vertex => vertex
                .HasLabel("service")
                .HasKey(service => service.Id)
                .Properties(service => new { service.Id, service.Name, service.Health }));
            graph.Edge<ServiceDependency>("dependencies", edge => edge
                .HasLabel("depends_on")
                .HasKey(dependency => dependency.Id)
                .HasSource<ServiceNode>(dependency => dependency.SourceId, service => service.Id)
                .HasDestination<ServiceNode>(dependency => dependency.DestinationId, service => service.Id));
        });
    }
}

internal sealed class EfTopologyRepository(TopologyDbContext context) : ITopologyRepository
{
    public async ValueTask AddServiceAsync(ServiceNode service, CancellationToken cancellationToken) =>
        _ = await context.Services.AddAsync(service, cancellationToken).ConfigureAwait(false);

    public async ValueTask AddDependencyAsync(
        ServiceDependency dependency,
        CancellationToken cancellationToken) =>
        _ = await context.Dependencies.AddAsync(dependency, cancellationToken).ConfigureAwait(false);

    public ValueTask<ServiceNode?> FindServiceAsync(
        string tenantId,
        Guid serviceId,
        CancellationToken cancellationToken) =>
        new(context.Services.SingleOrDefaultAsync(
            service => service.TenantId == tenantId && service.Id == serviceId,
            cancellationToken));

    public async ValueTask<IReadOnlyList<ServiceNode>> ListServicesAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        await context.Services.AsNoTracking()
            .Where(service => service.TenantId == tenantId)
            .OrderBy(service => service.Name)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ServiceDependency>> ListDependenciesAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        await context.Dependencies.AsNoTracking()
            .Where(dependency => dependency.TenantId == tenantId)
            .OrderBy(dependency => dependency.CreatedAt)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask AddIncidentAsync(
        TopologyIncident incident,
        CancellationToken cancellationToken) =>
        _ = await context.Incidents.AddAsync(incident, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<TopologyIncident>> ListIncidentsAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        await context.Incidents.AsNoTracking()
            .Where(incident => incident.TenantId == tenantId)
            .OrderByDescending(incident => incident.OpenedAt)
            .Take(200)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public ValueTask SaveAsync(CancellationToken cancellationToken) =>
        new(context.SaveChangesAsync(cancellationToken));
}

public static class TopologyInfrastructure
{
    public static IServiceCollection AddTopologyInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<TopologyDbContext>(options => options.UseBlueTusk(connectionString));
        services.AddScoped<ITopologyRepository, EfTopologyRepository>();
        services.AddScoped<TopologyService>();
        return services;
    }
}
