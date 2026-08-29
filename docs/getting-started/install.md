# Install BlueTusk

BlueTusk publishes one coordinated package version across Provider, Streams,
Sync, Live, Control Plane, Continuous Graph, and the three Live browser
clients. Keep every BlueTusk dependency in an application on the same exact
version.

## Choose a release channel

| Channel | Version | Intended use | PostgreSQL boundary |
| --- | --- | --- | --- |
| Stable | `1.0.0` | Existing applications that require a stable package line | PostgreSQL 15–18; PostgreSQL 19 features remain capability guarded |
| Release candidate | `1.1.0-rc.1` | Production-like evaluation of the coordinated 1.1 performance release | PostgreSQL 15–18 for general workloads; SQL/PGQ and Continuous Graph require PostgreSQL 19 and are not stable before GA |

The `1.1.0-rc.1` train was published from commit
`2e735ed46aec11d5009158a00ca7b862f9ec12af` as 62 NuGet packages and three npm
packages. Its six family workflows, registry availability, package-only
restore, and smoke applications passed. It is a public prerelease, not the
stable `1.1.0` release. Read the
[release record](../releases/1.1.0-rc.1.md) before selecting it.

The official PostgreSQL project currently lists PostgreSQL 19 Beta 3 and
[advises against production use of beta releases](https://www.postgresql.org/developer/beta/).
Use PostgreSQL 15–18 for production-like general workloads and keep SQL/PGQ or
Continuous Graph evaluation isolated until the GA programme passes.

For repeatable deployments, use exact versions in project files and lockfiles.
Do not use floating versions such as `1.*`, `*-*`, or the npm `rc` tag in a
committed production manifest.

## Prerequisites

- .NET 10 for the .NET packages;
- EF Core 10.0.11 when using `BlueTusk.EntityFrameworkCore`;
- PostgreSQL 15, 16, 17, or 18 for the released general-purpose surface;
- a PostgreSQL 19 server with negotiated SQL/PGQ capability for graph APIs;
- Node.js and npm only for the optional browser clients; and
- TLS, credentials, database roles, and server extensions appropriate to the
  target environment.

The repository `global.json` and `Directory.Packages.props` are the authority
for contributor toolchain versions. Applications may use a compatible later
.NET 10 SDK feature band.

## Select the smallest package set

Start with the package that owns the capability. Add adapters only when the
application uses them.

| Workload | Start with | Common additions |
| --- | --- | --- |
| Direct ADO.NET | `BlueTusk.Data` | `BlueTusk.Data.DependencyInjection`, cloud identity, or extension packages |
| EF Core | `BlueTusk.EntityFrameworkCore` | `BlueTusk.Data.DependencyInjection` and matching EF extension packages |
| Logical change delivery | `BlueTusk.Streams` | `BlueTusk.Streams.DependencyInjection` and one durable state-store package |
| Destination synchronization | `BlueTusk.Sync.DependencyInjection` | One or more of `BlueTusk.Sync.PostgreSql`, `.Redis`, `.Nats`, or `.OpenSearch` |
| Authorized live queries | `BlueTusk.Live.DependencyInjection` | `.AspNetCore`, `.SignalR`, `.ServerSentEvents`, or `.Grpc` |
| Operations and inventory | `BlueTusk.ControlPlane` | `BlueTusk.Dashboard` and the required persistence/hosting adapters |
| Incremental graph results | `BlueTusk.ContinuousGraph` | The PostgreSQL operations adapter and a compatible Streams/Live topology |

Provider and real-time packages have deliberate dependency direction. Do not
add every package to a shared application project “just in case”; that makes
ownership, startup, trimming, and incident diagnosis harder.

## Install the Provider release candidate

Create an application and pin the exact RC:

```powershell
dotnet new console --framework net10.0 --name BlueTuskQuickstart
Set-Location BlueTuskQuickstart
dotnet add package BlueTusk.Data --version 1.1.0-rc.1
```

For dependency injection:

```powershell
dotnet add package BlueTusk.Data.DependencyInjection --version 1.1.0-rc.1
```

Use a single long-lived data source per distinct connection configuration:

```csharp
await using var dataSource =
    new BlueTuskDataSourceBuilder(connectionString).Build();

await using var command = dataSource.CreateCommand(
    "SELECT $1::int4 + $2::int4");

command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));

var answer = await command.ExecuteScalarAsync<int>();
Console.WriteLine(answer);
```

Parameters are sent through PostgreSQL protocol binding. Do not interpolate
untrusted values into SQL.

## Install EF Core

```powershell
dotnet add package BlueTusk.EntityFrameworkCore --version 1.1.0-rc.1
dotnet add package Microsoft.EntityFrameworkCore.Design --version 10.0.11
```

Register BlueTusk with the same long-lived data source used by direct ADO.NET
work:

```csharp
var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();

services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource));
```

Keep the data source alive for the application lifetime and let each context
own only its logical connection.

## Install the browser clients

The npm packages are published under the `rc` dist-tag. Exact versions are
recommended:

```powershell
npm install --save-exact `
  @bluetusk/live@1.1.0-rc.1 `
  @bluetusk/live-angular@1.1.0-rc.1
```

React applications use:

```powershell
npm install --save-exact `
  @bluetusk/live@1.1.0-rc.1 `
  @bluetusk/live-react@1.1.0-rc.1
```

`npm install @bluetusk/live@rc` resolves the current RC for exploration, but
the resulting exact version and integrity hash should be committed in the
lockfile. The npm `latest` tag remains on the stable line.

## Verify the resolved graph

Confirm every BlueTusk package resolved to the intended train:

```powershell
dotnet list package --include-transitive
npm ls @bluetusk/live @bluetusk/live-angular @bluetusk/live-react
```

Then run a clean Release build and the application’s PostgreSQL smoke test:

```powershell
dotnet restore --force-evaluate
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
```

CI should fail when stable and RC packages are mixed, a floating version is
introduced, or the lockfile changes unexpectedly.

## Before production-like evaluation

1. Read the [production checklist](../operations/production-checklist.md).
2. Configure TLS and least-privilege database roles; never copy repository test
   credentials into an application.
3. Register BlueTusk meters and traces with OpenTelemetry.
4. Set explicit pool, queue, transaction, spool, replay, and destination
   bounds from measured workload data.
5. Exercise startup, readiness, failover, cancellation, shutdown, backup,
   restore, replay, and rollback against the real topology.
6. For Continuous Graph, verify the negotiated SQL/PGQ capability and retain
   authoritative repair. Do not treat a PostgreSQL 19 prerelease as a stable
   production dependency.

Continue with [core concepts](concepts.md), the
[deployment guide](../operations/deployment.md), and
[production observability](../operations/observability.md).
