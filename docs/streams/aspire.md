# Wire Streams into .NET Aspire

This guide shows you how to pass PostgreSQL connections and Streams settings to
a worker project from a .NET Aspire AppHost, without copying secrets into the
AppHost.

Add the package to your AppHost project, together with Aspire's PostgreSQL
hosting package:

```powershell
dotnet add package BlueTusk.Streams.Aspire
dotnet add package Aspire.Hosting.PostgreSQL
```

`BlueTusk.Streams.Aspire` builds on `Aspire.Hosting` 13.5.4.

## Use the durable relay (default)

`WithBlueTuskStreams` connects a worker to a source database and a separate
control database for the [durable relay](durable-relay.md):

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var source = builder.AddPostgres("source-server").AddDatabase("app");
var control = builder.AddPostgres("control-server").AddDatabase("streams");

builder.AddProject("search-projector", "../SearchProjector/SearchProjector.csproj")
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

In an AppHost created from the Aspire template you can use the generated
`builder.AddProject<Projects.SearchProjector>("search-projector")` instead of
the path.

## Read from a slot directly

For a consumer that owns its own slot, without relay storage, call
`WithBlueTuskStreamsDirect` and set `DeliveryMode` to `Direct`:

```csharp
builder.AddProject("audit-writer", "../AuditWriter/AuditWriter.csproj")
    .WithBlueTuskStreamsDirect(
        source,
        new BlueTuskStreamsAspireOptions
        {
            Slot = "app_streams_audit",
            Publications = ["app_changes"],
            ConsumerGroup = "audit",
            DeliveryMode = BlueTuskStreamsAspireDeliveryMode.Direct,
        });

builder.Build().Run();
```

Give every direct consumer its own slot. See
[direct consumers and the relay](concepts.md#direct-consumers-and-the-relay).

## What the worker receives

The AppHost sets these environment variables on the worker. Connection strings
are passed as Aspire connection-string expressions, so the AppHost never
resolves or stores the secret values.

| Environment variable | Configuration key | Value |
| --- | --- | --- |
| `BLUETUSK_STREAMS_SOURCE` | `BLUETUSK_STREAMS_SOURCE` | Source database connection string. |
| `BLUETUSK_STREAMS_CONTROL` | `BLUETUSK_STREAMS_CONTROL` | Control database connection string (relay mode only). |
| `BlueTusk__Streams__Slot` | `BlueTusk:Streams:Slot` | `Slot` |
| `BlueTusk__Streams__Publications__0`, `__1`, ... | `BlueTusk:Streams:Publications:0`, ... | One entry per publication. |
| `BlueTusk__Streams__ConsumerGroup` | `BlueTusk:Streams:ConsumerGroup` | `ConsumerGroup` |
| `BlueTusk__Streams__ControlSchema` | `BlueTusk:Streams:ControlSchema` | `ControlSchema` (default `bluetusk_streams`) |
| `BlueTusk__Streams__DeliveryMode` | `BlueTusk:Streams:DeliveryMode` | `DurableRelay` or `Direct` |

Each publication is a separate indexed key, so publication names never need
escaping.

Streams does not read these keys by itself; your worker reads them, as the
[quick start](quickstart.md) worker does with
`configuration.GetSection("BlueTusk:Streams")`. The
[`bluetusk-streams` tool](cli.md) reads the two connection variables too. See
[configuration](configuration.md#configuration-keys).

## Errors you may see

| Error | Cause |
| --- | --- |
| `ArgumentException`: "Durable relay mode requires a separate control resource." | `WithBlueTuskStreamsDirect` was called without `DeliveryMode = BlueTuskStreamsAspireDeliveryMode.Direct`. |
| `ArgumentException`: "Direct mode must use WithBlueTuskStreamsDirect and cannot reference relay control storage." | `WithBlueTuskStreams` was called with `DeliveryMode.Direct`. |
| `InvalidOperationException`: "At least one non-empty publication is required." | `Publications` is empty or has a blank name. |

## Related pages

- [Configuration](configuration.md)
- [Durable relay](durable-relay.md)
