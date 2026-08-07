using BlueTusk.ServiceTopology.Domain;
using Xunit;

namespace BlueTusk.ServiceTopology.Tests;

public sealed class TopologyModelTests
{
    [Fact]
    public void Health_updates_are_versioned()
    {
        var service = ServiceNode.Create("tenant-a", "billing");

        service.ReportHealth(ServiceHealth.Healthy, 0);

        Assert.Equal(ServiceHealth.Healthy, service.Health);
        Assert.Equal(1, service.Version);
        _ = Assert.Throws<InvalidOperationException>(() =>
            service.ReportHealth(ServiceHealth.Degraded, 0));
    }

    [Fact]
    public void Self_dependency_is_rejected()
    {
        var serviceId = Guid.NewGuid();
        _ = Assert.Throws<ArgumentException>(() =>
            ServiceDependency.Create("tenant-a", serviceId, serviceId));
    }
}
