namespace BlueTusk.OrderOperations.Domain;

public enum FulfilmentState
{
    Created,
    Allocated,
    Picked,
    Shipped,
    Cancelled,
}

public sealed class FulfilmentOrder
{
    private FulfilmentOrder()
    {
    }

    private FulfilmentOrder(Guid id, string tenantId, string customerReference)
    {
        Id = id;
        TenantId = Required(tenantId, nameof(tenantId));
        CustomerReference = Required(customerReference, nameof(customerReference));
        State = FulfilmentState.Created;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = string.Empty;

    public string CustomerReference { get; private set; } = string.Empty;

    public FulfilmentState State { get; private set; }

    public string? AllocationReference { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static FulfilmentOrder Create(
        string tenantId,
        string customerReference,
        Guid? id = null) =>
        new(id ?? Guid.NewGuid(), tenantId, customerReference);

    public void Allocate(string allocationReference, long expectedVersion)
    {
        ExpectVersion(expectedVersion);
        EnsureState(FulfilmentState.Created);
        AllocationReference = Required(allocationReference, nameof(allocationReference));
        TransitionTo(FulfilmentState.Allocated);
    }

    public void MarkPicked(long expectedVersion)
    {
        ExpectVersion(expectedVersion);
        EnsureState(FulfilmentState.Allocated);
        TransitionTo(FulfilmentState.Picked);
    }

    public void Ship(long expectedVersion)
    {
        ExpectVersion(expectedVersion);
        EnsureState(FulfilmentState.Picked);
        TransitionTo(FulfilmentState.Shipped);
    }

    public void Cancel(long expectedVersion)
    {
        ExpectVersion(expectedVersion);
        if (State is FulfilmentState.Shipped or FulfilmentState.Cancelled)
        {
            throw new InvalidOperationException($"An order in state {State} cannot be cancelled.");
        }

        TransitionTo(FulfilmentState.Cancelled);
    }

    private void TransitionTo(FulfilmentState state)
    {
        State = state;
        Version++;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void ExpectVersion(long expectedVersion)
    {
        if (Version != expectedVersion)
        {
            throw new InvalidOperationException(
                $"Expected order version {expectedVersion}, but found {Version}.");
        }
    }

    private void EnsureState(FulfilmentState expected)
    {
        if (State != expected)
        {
            throw new InvalidOperationException(
                $"Expected order state {expected}, but found {State}.");
        }
    }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
