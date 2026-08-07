# Aspire integration

`BlueTusk.Streams.Aspire` wires Streams workers to Aspire connection-string
resources without resolving or copying secrets in the AppHost. It targets the
Aspire application model and supports both relay and explicit direct delivery.

```csharp
var source = builder.AddPostgres("source-server").AddDatabase("app");
var control = builder.AddPostgres("control-server").AddDatabase("streams");

builder.AddProject<Projects.SearchProjector>("search-projector")
    .WithBlueTuskStreams(
        source,
        control,
        new BlueTuskStreamsAspireOptions
        {
            Slot = "app_streams",
            Publications = ["app_changes"],
            ConsumerGroup = "search",
        });
```

The source and control connection expressions become
`BLUETUSK_STREAMS_SOURCE` and `BLUETUSK_STREAMS_CONTROL` only in the worker's
environment. Slot, group, schema, delivery mode, and each publication use
hierarchical .NET configuration keys. Publications are indexed separately so
PostgreSQL identifiers are never encoded through a lossy delimiter.

Durable relay is the default and requires a control resource. Direct
slot-per-group operation is intentionally a separate call and requires
`DeliveryMode.Direct`:

```csharp
worker.WithBlueTuskStreamsDirect(
    source,
    new BlueTuskStreamsAspireOptions
    {
        Slot = "app_streams_search",
        Publications = ["app_changes"],
        ConsumerGroup = "search",
        DeliveryMode = BlueTuskStreamsAspireDeliveryMode.Direct,
    });
```

The hosting integration uses `Aspire.Hosting` 13.4.6, the current stable Aspire
application-model package when this preview baseline was established.
