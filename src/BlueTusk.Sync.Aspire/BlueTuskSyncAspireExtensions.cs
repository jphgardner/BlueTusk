using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>Identifies how a Sync worker consumes transaction deliveries.</summary>
public enum BlueTuskSyncAspireDeliveryMode
{
    /// <summary>Consumes an independently checkpointed PostgreSQL durable-relay group.</summary>
    DurableRelay,

    /// <summary>Consumes an independently owned direct replication slot.</summary>
    Direct,
}

/// <summary>Identifies the destination protocol configured for a Sync worker.</summary>
public enum BlueTuskSyncAspireDestination
{
    /// <summary>Uses the atomic PostgreSQL materialisation destination.</summary>
    PostgreSql,

    /// <summary>Uses the NATS JetStream destination.</summary>
    Nats,

    /// <summary>Uses the Redis materialisation destination.</summary>
    Redis,

    /// <summary>Uses the OpenSearch materialisation destination.</summary>
    OpenSearch,
}

/// <summary>Configures one Aspire-hosted BlueTusk Sync pipeline.</summary>
public sealed record BlueTuskSyncAspireOptions
{
    /// <summary>Gets the stable pipeline identifier.</summary>
    public required string PipelineId { get; init; }

    /// <summary>Gets the independent Streams or relay consumer-group name.</summary>
    public required string ConsumerGroup { get; init; }

    /// <summary>Gets the application transform version label.</summary>
    public required string TransformVersion { get; init; }

    /// <summary>Gets the configured destination protocol.</summary>
    public required BlueTuskSyncAspireDestination Destination { get; init; }

    /// <summary>Gets the relay control schema.</summary>
    public string ControlSchema { get; init; } = "bluetusk_streams";

    /// <summary>Gets the transaction delivery mode.</summary>
    public BlueTuskSyncAspireDeliveryMode DeliveryMode { get; init; } =
        BlueTuskSyncAspireDeliveryMode.DurableRelay;

    /// <summary>Gets whether the worker exposes operator reconciliation.</summary>
    public bool ReconciliationEnabled { get; init; } = true;

    /// <summary>Gets whether the worker exposes zero-downtime rebuild operations.</summary>
    public bool RebuildEnabled { get; init; } = true;

    internal void Validate(bool hasControlResource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(TransformVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(ControlSchema);
        if (!Enum.IsDefined(Destination))
        {
            throw new InvalidOperationException("A supported Sync destination is required.");
        }

        if (!Enum.IsDefined(DeliveryMode))
        {
            throw new InvalidOperationException("A supported Sync delivery mode is required.");
        }

        if (DeliveryMode is BlueTuskSyncAspireDeliveryMode.DurableRelay && !hasControlResource)
        {
            throw new ArgumentException(
                "Durable relay Sync mode requires a separate control resource.",
                nameof(hasControlResource));
        }

        if (DeliveryMode is BlueTuskSyncAspireDeliveryMode.Direct && hasControlResource)
        {
            throw new ArgumentException(
                "Direct Sync mode cannot reference relay control storage.",
                nameof(hasControlResource));
        }

    }
}

/// <summary>Adds BlueTusk Sync resource references and configuration to Aspire workers.</summary>
public static class BlueTuskSyncAspireExtensions
{
    /// <summary>Configures the default durable-relay Sync topology.</summary>
    public static IResourceBuilder<TWorker> WithBlueTuskSync<
        TWorker,
        TSource,
        TControl,
        TDestination>(
        this IResourceBuilder<TWorker> builder,
        IResourceBuilder<TSource> source,
        IResourceBuilder<TControl> control,
        IResourceBuilder<TDestination> destination,
        BlueTuskSyncAspireOptions options)
        where TWorker : IResourceWithEnvironment
        where TSource : IResourceWithConnectionString
        where TControl : IResourceWithConnectionString
        where TDestination : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(hasControlResource: true);
        if (ReferenceEquals(source.Resource, control.Resource))
        {
            throw new ArgumentException(
                "BlueTusk Sync durable relay source and control resources must be separate.",
                nameof(control));
        }

        builder.WithEnvironment("BLUETUSK_SYNC_SOURCE", source.Resource.ConnectionStringExpression)
            .WithEnvironment("BLUETUSK_SYNC_CONTROL", control.Resource.ConnectionStringExpression)
            .WithEnvironment(
                "BLUETUSK_SYNC_DESTINATION",
                destination.Resource.ConnectionStringExpression);
        ApplyConfiguration(builder, options);
        return builder;
    }

    /// <summary>Configures an explicit direct-slot Sync topology without relay control storage.</summary>
    public static IResourceBuilder<TWorker> WithBlueTuskSyncDirect<
        TWorker,
        TSource,
        TDestination>(
        this IResourceBuilder<TWorker> builder,
        IResourceBuilder<TSource> source,
        IResourceBuilder<TDestination> destination,
        BlueTuskSyncAspireOptions options)
        where TWorker : IResourceWithEnvironment
        where TSource : IResourceWithConnectionString
        where TDestination : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(hasControlResource: false);
        if (options.DeliveryMode is not BlueTuskSyncAspireDeliveryMode.Direct)
        {
            throw new ArgumentException(
                "WithBlueTuskSyncDirect requires DeliveryMode.Direct.",
                nameof(options));
        }

        builder.WithEnvironment("BLUETUSK_SYNC_SOURCE", source.Resource.ConnectionStringExpression)
            .WithEnvironment(
                "BLUETUSK_SYNC_DESTINATION",
                destination.Resource.ConnectionStringExpression);
        ApplyConfiguration(builder, options);
        return builder;
    }

    private static void ApplyConfiguration<TWorker>(
        IResourceBuilder<TWorker> builder,
        BlueTuskSyncAspireOptions options)
        where TWorker : IResourceWithEnvironment
    {
        builder.WithEnvironment("BlueTusk__Sync__PipelineId", options.PipelineId)
            .WithEnvironment("BlueTusk__Sync__ConsumerGroup", options.ConsumerGroup)
            .WithEnvironment("BlueTusk__Sync__TransformVersion", options.TransformVersion)
            .WithEnvironment("BlueTusk__Sync__Destination", options.Destination.ToString())
            .WithEnvironment("BlueTusk__Sync__ControlSchema", options.ControlSchema)
            .WithEnvironment("BlueTusk__Sync__DeliveryMode", options.DeliveryMode.ToString())
            .WithEnvironment(
                "BlueTusk__Sync__ReconciliationEnabled",
                options.ReconciliationEnabled.ToString())
            .WithEnvironment(
                "BlueTusk__Sync__RebuildEnabled",
                options.RebuildEnabled.ToString());
    }
}
