using BlueTusk.OrderOperations.Domain;

namespace BlueTusk.OrderOperations.Application;

public interface IOrderRepository
{
    ValueTask AddAsync(FulfilmentOrder order, CancellationToken cancellationToken);

    ValueTask<FulfilmentOrder?> FindAsync(
        string tenantId,
        Guid orderId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<FulfilmentOrder>> SearchAsync(
        string tenantId,
        string? query,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<OrderTimelineEntry>> TimelineAsync(
        string tenantId,
        Guid orderId,
        CancellationToken cancellationToken);

    ValueTask SaveAsync(
        FulfilmentOrder order,
        string operation,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public sealed record OrderTimelineEntry(
    string Operation,
    string Payload,
    DateTimeOffset RecordedAt,
    DateTimeOffset? RelayedAt);

public sealed class OrderService(IOrderRepository repository)
{
    public async ValueTask<FulfilmentOrder> CreateAsync(
        string tenantId,
        string customerReference,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var order = FulfilmentOrder.Create(tenantId, customerReference);
        await repository.AddAsync(order, cancellationToken).ConfigureAwait(false);
        await repository.SaveAsync(order, "order.created", idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        return order;
    }

    public async ValueTask<FulfilmentOrder> TransitionAsync(
        string tenantId,
        Guid orderId,
        string transition,
        long expectedVersion,
        string? allocationReference,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var order = await repository.FindAsync(tenantId, orderId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Order was not found.");
        switch (transition)
        {
            case "allocate":
                order.Allocate(allocationReference ?? string.Empty, expectedVersion);
                break;
            case "pick":
                order.MarkPicked(expectedVersion);
                break;
            case "ship":
                order.Ship(expectedVersion);
                break;
            case "cancel":
                order.Cancel(expectedVersion);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition), transition, "Unknown transition.");
        }

        await repository.SaveAsync(
            order,
            $"order.{transition}",
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return order;
    }
}
