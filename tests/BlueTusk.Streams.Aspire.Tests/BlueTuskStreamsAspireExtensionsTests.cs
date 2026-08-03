using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace BlueTusk.Streams.Aspire.Tests;

public sealed class BlueTuskStreamsAspireExtensionsTests
{
    [Fact]
    public void Relay_worker_receives_source_control_and_indexed_configuration()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var source = builder.AddConnectionString("source");
        var control = builder.AddConnectionString("control");
        var worker = builder.AddContainer("worker", "example/worker");

        var configured = worker.WithBlueTuskStreams(
            source,
            control,
            new BlueTuskStreamsAspireOptions
            {
                Slot = "app_streams",
                ConsumerGroup = "search",
                Publications = ["app", "audit"],
            });

        Assert.Same(worker, configured);
        Assert.Equal(
            8,
            worker.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());
    }

    [Fact]
    public void Relay_is_the_default_and_requires_control_storage()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var source = builder.AddConnectionString("source");
        var worker = builder.AddContainer("worker", "example/worker");
        var options = new BlueTuskStreamsAspireOptions
        {
            Slot = "app_streams",
            ConsumerGroup = "search",
            Publications = ["app"],
        };

        var exception = Assert.Throws<ArgumentException>(
            () => worker.WithBlueTuskStreamsDirect(source, options));
        Assert.Contains("requires a separate control resource", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_mode_is_an_explicit_control_free_opt_out()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var source = builder.AddConnectionString("source");
        var worker = builder.AddContainer("worker", "example/worker");

        _ = worker.WithBlueTuskStreamsDirect(
            source,
            new BlueTuskStreamsAspireOptions
            {
                Slot = "app_streams_search",
                ConsumerGroup = "search",
                Publications = ["app"],
                DeliveryMode = BlueTuskStreamsAspireDeliveryMode.Direct,
            });

        Assert.Equal(
            6,
            worker.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());
    }
}
