using BlueTusk.OrderOperations.Domain;
using Xunit;

namespace BlueTusk.OrderOperations.Tests;

public sealed class FulfilmentOrderTests
{
    [Fact]
    public void Order_follows_the_fulfilment_lifecycle_with_optimistic_versions()
    {
        var order = FulfilmentOrder.Create("tenant-a", "customer-42");

        order.Allocate("bin-7", 0);
        order.MarkPicked(1);
        order.Ship(2);

        Assert.Equal(FulfilmentState.Shipped, order.State);
        Assert.Equal(3, order.Version);
    }

    [Fact]
    public void Stale_mutation_is_rejected_without_changing_state()
    {
        var order = FulfilmentOrder.Create("tenant-a", "customer-42");
        order.Allocate("bin-7", 0);

        _ = Assert.Throws<InvalidOperationException>(() => order.MarkPicked(0));

        Assert.Equal(FulfilmentState.Allocated, order.State);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void Shipped_order_cannot_be_cancelled()
    {
        var order = FulfilmentOrder.Create("tenant-a", "customer-42");
        order.Allocate("bin-7", 0);
        order.MarkPicked(1);
        order.Ship(2);

        _ = Assert.Throws<InvalidOperationException>(() => order.Cancel(3));
    }
}
