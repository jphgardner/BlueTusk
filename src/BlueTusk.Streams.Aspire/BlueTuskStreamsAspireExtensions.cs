using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

public enum BlueTuskStreamsAspireDeliveryMode
{
    DurableRelay,
    Direct,
}

public sealed record BlueTuskStreamsAspireOptions
{
    public required string Slot { get; init; }

    public required IReadOnlyList<string> Publications { get; init; }

    public required string ConsumerGroup { get; init; }

    public string ControlSchema { get; init; } = "bluetusk_streams";

    public BlueTuskStreamsAspireDeliveryMode DeliveryMode { get; init; } =
        BlueTuskStreamsAspireDeliveryMode.DurableRelay;

    internal void Validate(bool hasControlResource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Slot);
        ArgumentNullException.ThrowIfNull(Publications);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(ControlSchema);
        if (Publications.Count == 0 || Publications.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "At least one non-empty publication is required.");
        }

        if (DeliveryMode == BlueTuskStreamsAspireDeliveryMode.DurableRelay && !hasControlResource)
        {
            throw new ArgumentException(
                "Durable relay mode requires a separate control resource.",
                nameof(hasControlResource));
        }

        if (DeliveryMode == BlueTuskStreamsAspireDeliveryMode.Direct && hasControlResource)
        {
            throw new ArgumentException(
                "Direct mode must use WithBlueTuskStreamsDirect and cannot reference relay control storage.",
                nameof(hasControlResource));
        }
    }
}

public static class BlueTuskStreamsAspireExtensions
{
    public static IResourceBuilder<TDestination> WithBlueTuskStreams<
        TDestination,
        TSource,
        TControl>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<TSource> source,
        IResourceBuilder<TControl> control,
        BlueTuskStreamsAspireOptions options)
        where TDestination : IResourceWithEnvironment
        where TSource : IResourceWithConnectionString
        where TControl : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(hasControlResource: true);

        builder.WithEnvironment("BLUETUSK_STREAMS_SOURCE", source.Resource.ConnectionStringExpression)
            .WithEnvironment("BLUETUSK_STREAMS_CONTROL", control.Resource.ConnectionStringExpression);
        ApplyConfiguration(builder, options);
        return builder;
    }

    public static IResourceBuilder<TDestination> WithBlueTuskStreamsDirect<TDestination, TSource>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<TSource> source,
        BlueTuskStreamsAspireOptions options)
        where TDestination : IResourceWithEnvironment
        where TSource : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(hasControlResource: false);
        if (options.DeliveryMode != BlueTuskStreamsAspireDeliveryMode.Direct)
        {
            throw new ArgumentException(
                "WithBlueTuskStreamsDirect requires DeliveryMode.Direct.",
                nameof(options));
        }

        builder.WithEnvironment("BLUETUSK_STREAMS_SOURCE", source.Resource.ConnectionStringExpression);
        ApplyConfiguration(builder, options);
        return builder;
    }

    private static void ApplyConfiguration<TDestination>(
        IResourceBuilder<TDestination> builder,
        BlueTuskStreamsAspireOptions options)
        where TDestination : IResourceWithEnvironment
    {
        builder.WithEnvironment("BlueTusk__Streams__Slot", options.Slot)
            .WithEnvironment("BlueTusk__Streams__ConsumerGroup", options.ConsumerGroup)
            .WithEnvironment("BlueTusk__Streams__ControlSchema", options.ControlSchema)
            .WithEnvironment("BlueTusk__Streams__DeliveryMode", options.DeliveryMode.ToString());
        for (var index = 0; index < options.Publications.Count; index++)
        {
            builder.WithEnvironment(
                $"BlueTusk__Streams__Publications__{index}",
                options.Publications[index]);
        }
    }
}
