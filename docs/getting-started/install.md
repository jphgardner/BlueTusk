# Install BlueTusk

Start with the package that owns the feature you need. Check the target
frameworks and dependencies of the exact version you install. Use one
compatible set of package versions in each application and commit its lockfile.

## Start with a supported environment

The [first-query tutorial](quickstart.md) uses .NET 10, PostgreSQL 15–18,
and the `BlueTusk.Data` 1.2.0 package. Use a current PostgreSQL
minor release for the major version you run. For EF Core, match the EF Core
runtime, design package, and `dotnet-ef` tool. See [runtime compatibility](runtime-compatibility.md) for the .NET 5–11 mappings and integration minimums.

These guides target BlueTusk 1.2. Use a feed that contains the 1.2.0 packages and check the prerequisites of the feature you are installing.

## Choose packages by workload

Use the [package catalog](https://bluetusk.io/packages) to find libraries, tools, templates, and browser clients with copyable 1.2.0 commands.

| Workload | Package to start with | Add when needed |
| --- | --- | --- |
| ADO.NET commands and pooling | `BlueTusk.Data` | `BlueTusk.Data.DependencyInjection` for host registration |
| LINQ, models, and migrations | `BlueTusk.EntityFrameworkCore` | `BlueTusk.EntityFrameworkCore.Design` and the matching EF toolchain |
| PostgreSQL-specific extension types | See the [extensions guide](../extensions/README.md) | EF adapters for LINQ and migrations |
| Committed PostgreSQL transactions | `BlueTusk.Streams` | A durable checkpoint store and hosting integration |
| Destination synchronization | `BlueTusk.Sync` | The connector for your destination |
| Authorized live query results | `BlueTusk.Live` | ASP.NET Core, SSE, SignalR, gRPC, or the browser client |
| Property graphs | `BlueTusk.EntityFrameworkCore` | `BlueTusk.ContinuousGraph` for live graph results |
| Instance and pipeline operations | `BlueTusk.ControlPlane` | `BlueTusk.Dashboard` and the required hosting or persistence adapter |
| Application data services | See the [data services guide](../ecosystem/README.md) | Only the database, hosting, and transport adapters you use |

## Install the provider

From an existing .NET project:

```sh
dotnet add package BlueTusk.Data --version 1.2.0
```

See [BlueTusk.Data on NuGet](https://www.nuget.org/packages/BlueTusk.Data)
for package information. Install the matching 1.2.0 version of each BlueTusk dependency.

## Install the EF Core provider

```sh
dotnet add package BlueTusk.EntityFrameworkCore --version 1.2.0
```

For model creation and connection registration, follow the
[EF Core setup guide](../ef-core/README.md). Provision PostgreSQL server
extensions separately before running migrations that use their types or
operators.

## Add a browser client

Use `@bluetusk/live` for the Live client, `@bluetusk/live-react` for React,
or `@bluetusk/live-angular` for Angular. Choose the client version supported
by your host and transport. The [Live reference](../live/reference.md)
describes authentication, replay, and reconnect behavior.

## Configure PostgreSQL access

Use a dedicated database and a restricted login for your application.
Provide credentials through your development secret store or deployment
configuration. Verify server certificates in deployed environments. Use a
separate role to run migrations and provision replication slots.

Next, [run a first query](quickstart.md) or use the
[deployment guide](../operations/deployment.md) to configure an existing app.
