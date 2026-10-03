# Install BlueTusk

This page helps you choose the BlueTusk packages and the version you need, and
add them to a .NET or JavaScript project.

## Requirements

| Requirement | Version |
| --- | --- |
| .NET | .NET 10 (`net10.0`) |
| EF Core (only for `BlueTusk.EntityFrameworkCore`) | 10.0.11 |
| PostgreSQL | 15, 16, 17 or 18. PostgreSQL 19 is preview only. |
| Node.js (only for the browser clients) | A current LTS release |

Streams, Sync and Live also need PostgreSQL logical replication:
`wal_level = logical` on the server and a role with the `REPLICATION`
attribute. The [Streams quick start](../streams/quickstart.md) shows the setup.

The [support matrix](../../VERSIONING.md) is the authority on supported
versions.

## Choose a version

All Core packages share one version number. Use the same exact version for
every BlueTusk package in an application.

| Version | Status | Use it when |
| --- | --- | --- |
| `1.1.0` | Next release. In release qualification; **not published yet**. | When it is published, use it for all new and existing applications. |
| `1.1.0-rc.1` | Public release candidate, published 2026-08-29. | You want to evaluate 1.1 before `1.1.0` is published. |
| `1.0.0` | Stable release, published 2026-08-23. | You need a stable package today. |

When `1.1.0` is published, `1.0.0` and `1.1.0-rc.1` will be deprecated on
NuGet and npm and will point to `1.1.0`. To move an existing application, see
the [upgrade guide](../operations/upgrade-guide.md).

Some features documented here are new in `1.1.0` and are not in `1.0.0` or
`1.1.0-rc.1`. Those pages say **New in 1.1.0**. The
[1.1.0 release notes](../releases/1.1.0.md) list them all.

### Product status

| Family | Version line | Status |
| --- | --- | --- |
| Provider (ADO.NET, EF Core, extensions, cloud identity, `bluetusk` tool) | 1.1.0 | Core |
| Streams | 1.1.0 | Core |
| Sync | 1.1.0 | Core |
| Live (NuGet and npm) | 1.1.0 | Core |
| Control Plane | 1.1.0 | Core |
| Graph (SQL/PGQ) and Continuous Graph | Not part of 1.1.0 | Preview. PostgreSQL removed SQL/PGQ in PostgreSQL 19 Beta 4, so these wait for a PostgreSQL release that ships it. |
| Events, Jobs, Workflows, Documents, Schema, Projections, Search, Sql, Studio, Edge | `0.1.0-preview.1` | Preview. Not published; each family is released when its own checks pass. |

## Choose packages

Install only what you use. Each row lists the package to start with and the
packages you add for specific needs.

| You want to | Start with | Add when needed |
| --- | --- | --- |
| Run SQL with ADO.NET | `BlueTusk.Data` | `BlueTusk.Data.DependencyInjection` for `IServiceCollection` registration |
| Use EF Core | `BlueTusk.EntityFrameworkCore` | `BlueTusk.EntityFrameworkCore.Design` and `Microsoft.EntityFrameworkCore.Design` for migrations |
| Use a PostgreSQL extension | `BlueTusk.Extensions.PgVector`, `.PostGIS`, `.TimescaleDB`, `.Citext`, `.HStore`, `.LTree` or `.PgTrgm` | The matching `.EntityFrameworkCore` package where one exists |
| Sign in with a cloud identity | `BlueTusk.Identity.Aws`, `.Azure` or `.GoogleCloud` | |
| React to committed changes | `BlueTusk.Streams.DependencyInjection` | One state store: `BlueTusk.Streams.Storage.PostgreSql`, `.Redis` or `.File` |
| Copy changes to another system | `BlueTusk.Sync.DependencyInjection` | One destination: `BlueTusk.Sync.PostgreSql`, `.Redis`, `.Nats`, `.OpenSearch`, `.Kafka`, `.S3` or `.Webhooks` |
| Push live query results to clients | `BlueTusk.Live.AspNetCore` | A transport: `BlueTusk.Live.SignalR`, `.ServerSentEvents` or `.Grpc`; `BlueTusk.Live.EntityFrameworkCore` for EF queries |
| Use Live from a browser | `@bluetusk/live` | `@bluetusk/live-angular`, `-react`, `-vue` or `-svelte` |
| Operate the products from a dashboard | `BlueTusk.ControlPlane` | `BlueTusk.Dashboard`; `BlueTusk.ControlPlane.Kubernetes` for Kubernetes |
| Run them under .NET Aspire | `BlueTusk.Streams.Aspire`, `BlueTusk.Sync.Aspire` or `BlueTusk.Live.Aspire` | |
| Test your code | `BlueTusk.Streams.Testing`, `BlueTusk.Sync.Testing` or `BlueTusk.Live.Testing` | |

Each product page lists its packages in full.

## Add .NET packages

```powershell
dotnet add package BlueTusk.Data
```

Without `--version`, NuGet picks the latest stable version. To choose a
version, add it explicitly, for example to evaluate the release candidate:

```powershell
dotnet add package BlueTusk.Data --version 1.1.0-rc.1
```

### Keep every BlueTusk package on one version

If your solution uses
[central package management](https://learn.microsoft.com/nuget/consume-packages/central-package-management),
set the version once in `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <BlueTuskVersion>1.1.0-rc.1</BlueTuskVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="BlueTusk.Data" Version="$(BlueTuskVersion)" />
    <PackageVersion Include="BlueTusk.Data.DependencyInjection" Version="$(BlueTuskVersion)" />
    <PackageVersion Include="BlueTusk.EntityFrameworkCore" Version="$(BlueTuskVersion)" />
  </ItemGroup>
</Project>
```

Do not use floating versions such as `1.*` in a production project. Mixed
BlueTusk versions in one application are not supported.

## Add the browser clients

```powershell
npm install @bluetusk/live
```

Add the package for your framework:

```powershell
npm install @bluetusk/live-react    # or live-angular, live-vue, live-svelte
```

npm's `latest` tag points to the stable line. The release candidate is under
the `rc` tag (`npm install @bluetusk/live@rc`). Commit your lockfile so every
build uses the same exact version.

## Install the command-line tool

```powershell
dotnet tool install --global BlueTusk.Tool
```

The `bluetusk` command scaffolds an EF Core model from a database
(`bluetusk scaffold`) and, new in 1.1.0, checks that a server is ready for
BlueTusk (`bluetusk doctor`). See the
[tool README](../../tooling/BlueTusk.Tool/README.md).

## Install the project templates

```powershell
dotnet new install BlueTusk.Production.Templates
dotnet new bluetusk-production --name Contoso.Orders --ClientFramework react
```

**New in 1.1.0.** `bluetusk-production` creates a complete application: API,
worker, EF Core migrations, tests, a browser client, containers and Helm
charts. See the [template README](../../templates/BlueTusk.Production/README.md).

`BlueTusk.Templates` provides `bluetusk-extension`, a starting point for
writing your own PostgreSQL extension package.

## Check what you installed

List the resolved versions and confirm every BlueTusk package has the same one:

```powershell
dotnet list package --include-transitive
npm ls @bluetusk/live
```

## Next steps

1. Build the [5-minute first app](quickstart.md).
2. Read the [core concepts](concepts.md).
3. Open the guide for your product from the [documentation home](../README.md).
