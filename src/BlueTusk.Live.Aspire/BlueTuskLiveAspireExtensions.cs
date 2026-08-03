using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

[Flags]
public enum BlueTuskLiveAspireTransports
{
    SignalR = 1,
    ServerSentEvents = 2,
    Grpc = 4,
    All = SignalR | ServerSentEvents | Grpc,
}

public sealed record BlueTuskLiveAspireOptions
{
    public string ControlSchema { get; init; } = "bluetusk_streams";

    public int MaximumSharedSubscriptions { get; init; } = 10_000;

    public int MaximumSubscribersPerQuery { get; init; } = 1_000;

    public int SubscriberBufferCapacity { get; init; } = 128;

    public int MaximumReplayEventsPerConnect { get; init; } = 1_024;

    public TimeSpan ReplayRetention { get; init; } = TimeSpan.FromMinutes(30);

    public BlueTuskLiveAspireTransports Transports { get; init; } =
        BlueTuskLiveAspireTransports.All;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ControlSchema);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSharedSubscriptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSubscribersPerQuery);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SubscriberBufferCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumReplayEventsPerConnect);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReplayRetention, TimeSpan.Zero);
        if (Transports == 0 || (Transports & ~BlueTuskLiveAspireTransports.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Transports));
        }
    }
}

public static class BlueTuskLiveAspireExtensions
{
    public static IResourceBuilder<THost> WithBlueTuskLive<THost, TSource, TControl>(
        this IResourceBuilder<THost> builder,
        IResourceBuilder<TSource> source,
        IResourceBuilder<TControl> control,
        BlueTuskLiveAspireOptions? options = null)
        where THost : IResourceWithEnvironment
        where TSource : IResourceWithConnectionString
        where TControl : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(control);
        if (ReferenceEquals(source.Resource, control.Resource))
        {
            throw new ArgumentException(
                "BlueTusk Live requires separate application-query and relay-control resources.",
                nameof(control));
        }

        options ??= new BlueTuskLiveAspireOptions();
        options.Validate();
        return builder
            .WithEnvironment("BLUETUSK_LIVE_SOURCE", source.Resource.ConnectionStringExpression)
            .WithEnvironment("BLUETUSK_LIVE_CONTROL", control.Resource.ConnectionStringExpression)
            .WithEnvironment("BlueTusk__Live__ControlSchema", options.ControlSchema)
            .WithEnvironment(
                "BlueTusk__Live__MaximumSharedSubscriptions",
                options.MaximumSharedSubscriptions.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment(
                "BlueTusk__Live__MaximumSubscribersPerQuery",
                options.MaximumSubscribersPerQuery.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment(
                "BlueTusk__Live__SubscriberBufferCapacity",
                options.SubscriberBufferCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment(
                "BlueTusk__Live__MaximumReplayEventsPerConnect",
                options.MaximumReplayEventsPerConnect.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment(
                "BlueTusk__Live__ReplayRetentionSeconds",
                options.ReplayRetention.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("BlueTusk__Live__Transports", options.Transports.ToString());
    }
}
