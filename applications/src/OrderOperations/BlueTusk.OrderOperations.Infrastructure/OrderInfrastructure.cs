using System.Text.Json;
using BlueTusk.OrderOperations.Application;
using BlueTusk.OrderOperations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.OrderOperations.Infrastructure;

public sealed class OrderOperationsDbContext(DbContextOptions<OrderOperationsDbContext> options) :
    DbContext(options)
{
    public DbSet<FulfilmentOrder> Orders => Set<FulfilmentOrder>();

    public DbSet<OperationalAudit> Audit => Set<OperationalAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orders");
        modelBuilder.Entity<FulfilmentOrder>(entity =>
        {
            entity.ToTable("fulfilment_orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.TenantId).HasMaxLength(80);
            entity.Property(order => order.CustomerReference).HasMaxLength(200);
            entity.Property(order => order.AllocationReference).HasMaxLength(200);
            entity.Property(order => order.Version).IsConcurrencyToken();
            entity.HasIndex(order => new { order.TenantId, order.CustomerReference });
        });
        modelBuilder.Entity<OperationalAudit>(entity =>
        {
            entity.ToTable("operational_audit");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.TenantId).HasMaxLength(80);
            entity.Property(entry => entry.IdempotencyKey).HasMaxLength(200);
            entity.Property(entry => entry.Operation).HasMaxLength(100);
            entity.HasIndex(entry => new { entry.TenantId, entry.IdempotencyKey }).IsUnique();
        });
    }
}

public sealed class OperationalAudit
{
    public long Id { get; set; }

    public required string TenantId { get; set; }

    public required Guid AggregateId { get; set; }

    public required string Operation { get; set; }

    public required string IdempotencyKey { get; set; }

    public required string Payload { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public DateTimeOffset? RelayedAt { get; set; }
}

internal sealed class EfOrderRepository(OrderOperationsDbContext context) : IOrderRepository
{
    public async ValueTask AddAsync(FulfilmentOrder order, CancellationToken cancellationToken) =>
        _ = await context.Orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

    public ValueTask<FulfilmentOrder?> FindAsync(
        string tenantId,
        Guid orderId,
        CancellationToken cancellationToken) =>
        new(context.Orders.SingleOrDefaultAsync(
            order => order.TenantId == tenantId && order.Id == orderId,
            cancellationToken));

    public async ValueTask<IReadOnlyList<FulfilmentOrder>> SearchAsync(
        string tenantId,
        string? query,
        CancellationToken cancellationToken)
    {
        var orders = context.Orders.AsNoTracking().Where(order => order.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            orders = orders.Where(order => order.CustomerReference.Contains(query));
        }

        return await orders.OrderByDescending(order => order.UpdatedAt).Take(200)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<OrderTimelineEntry>> TimelineAsync(
        string tenantId,
        Guid orderId,
        CancellationToken cancellationToken) =>
        await context.Audit.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.AggregateId == orderId)
            .OrderBy(entry => entry.RecordedAt)
            .Select(entry => new OrderTimelineEntry(
                entry.Operation,
                entry.Payload,
                entry.RecordedAt,
                entry.RelayedAt))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask SaveAsync(
        FulfilmentOrder order,
        string operation,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        context.Audit.Add(new OperationalAudit
        {
            TenantId = order.TenantId,
            AggregateId = order.Id,
            Operation = operation,
            IdempotencyKey = idempotencyKey,
            Payload = JsonSerializer.Serialize(order),
            RecordedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class OrderInfrastructure
{
    public static IServiceCollection AddOrderInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<OrderOperationsDbContext>(options =>
            options.UseBlueTusk(connectionString));
        services.AddScoped<IOrderRepository, EfOrderRepository>();
        services.AddScoped<OrderService>();
        return services;
    }
}
