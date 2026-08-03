using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace BlueTusk.Live.Aspire.Tests;

public sealed class BlueTuskLiveAspireExtensionsTests
{
    [Fact]
    public void Host_receives_query_relay_quota_retention_and_transport_configuration()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var source = builder.AddConnectionString("application");
        var control = builder.AddConnectionString("relay");
        var host = builder.AddContainer("live", "example/live");

        var configured = host.WithBlueTuskLive(
            source,
            control,
            new BlueTuskLiveAspireOptions
            {
                MaximumSharedSubscriptions = 500,
                Transports = BlueTuskLiveAspireTransports.ServerSentEvents |
                    BlueTuskLiveAspireTransports.Grpc,
            });

        Assert.Same(host, configured);
        Assert.Equal(
            9,
            host.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count());
    }

    [Fact]
    public void Host_rejects_shared_application_and_relay_storage()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var database = builder.AddConnectionString("database");
        var host = builder.AddContainer("live", "example/live");

        var exception = Assert.Throws<ArgumentException>(
            () => host.WithBlueTuskLive(database, database));

        Assert.Contains("separate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_rejects_unbounded_or_empty_transport_configuration()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var source = builder.AddConnectionString("application");
        var control = builder.AddConnectionString("relay");
        var host = builder.AddContainer("live", "example/live");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            host.WithBlueTuskLive(
                source,
                control,
                new BlueTuskLiveAspireOptions { SubscriberBufferCapacity = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            host.WithBlueTuskLive(
                source,
                control,
                new BlueTuskLiveAspireOptions { Transports = 0 }));
    }
}
