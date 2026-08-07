using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace BlueTusk.Sync.Aspire.Tests;

public sealed class BlueTuskSyncAspireExtensionsTests
{
    [Fact]
    public void Durable_relay_worker_receives_source_control_destination_and_configuration()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var source = builder.AddConnectionString("source");
        var control = builder.AddConnectionString("control");
        var destination = builder.AddConnectionString("opensearch");
        var worker = builder.AddContainer("worker", "example/sync-worker");

        var configured = worker.WithBlueTuskSync(
            source,
            control,
            destination,
            Options());

        Assert.Same(worker, configured);
        Assert.Equal(
            11,
            worker.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());
    }

    [Fact]
    public void Durable_relay_requires_a_distinct_control_resource()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var source = builder.AddConnectionString("source");
        var destination = builder.AddConnectionString("destination");
        var worker = builder.AddContainer("worker", "example/sync-worker");

        var exception = Assert.Throws<ArgumentException>(() =>
            worker.WithBlueTuskSync(source, source, destination, Options()));

        Assert.Contains("must be separate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_mode_is_explicit_and_omits_control_storage()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var source = builder.AddConnectionString("source");
        var destination = builder.AddConnectionString("redis");
        var worker = builder.AddContainer("worker", "example/sync-worker");
        var options = Options() with
        {
            Destination = BlueTuskSyncAspireDestination.Redis,
            DeliveryMode = BlueTuskSyncAspireDeliveryMode.Direct,
            RebuildEnabled = false,
        };

        _ = worker.WithBlueTuskSyncDirect(source, destination, options);

        Assert.Equal(
            10,
            worker.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());
    }

    [Fact]
    public void Direct_helper_rejects_default_durable_relay_options()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var source = builder.AddConnectionString("source");
        var destination = builder.AddConnectionString("nats");
        var worker = builder.AddContainer("worker", "example/sync-worker");
        var options = Options() with { Destination = BlueTuskSyncAspireDestination.Nats };

        var exception = Assert.Throws<ArgumentException>(() =>
            worker.WithBlueTuskSyncDirect(source, destination, options));

        Assert.Contains("requires a separate control", exception.Message, StringComparison.Ordinal);
    }

    private static BlueTuskSyncAspireOptions Options() =>
        new()
        {
            PipelineId = "orders-search",
            ConsumerGroup = "sync-orders-search",
            TransformVersion = "orders-v2",
            Destination = BlueTuskSyncAspireDestination.OpenSearch,
        };
}
