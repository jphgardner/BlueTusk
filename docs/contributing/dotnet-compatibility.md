# .NET runtime compatibility

Published BlueTusk `1.0.0` and `1.1.0-rc.1` packages target .NET 10. Work in
this checkout is expanding runtime compatibility to every .NET major from
.NET 5 through .NET 11. The compatibility inventory records migration status
for each package.

The executable inventory is
[`eng/dotnet-compatibility.json`](../../eng/dotnet-compatibility.json).
Every product-family package appears in it. Runtime packages remain `pending`
until their declared targets have executable tests. The inventory records
analyzers and templates as build-time packages.

The Streams, Sync, and Live Aspire packages target .NET 8–11. Their hosting
dependency requires .NET 8 or later. [Aspire.Hosting 13.6.5](https://www.nuget.org/packages/Aspire.Hosting/13.6.5)

The EF Core provider and its add-ons use the EF Core major selected for each
application target.

| Application target | EF Core version |
| --- | --- |
| .NET 5 | 5.0.17 |
| .NET 6 and 7 | 7.0.20 |
| .NET 8 | 8.0.34 |
| .NET 9 | 9.0.23 |
| .NET 10 | 10.0.11 |
| .NET 11 | 11.0.0 |

Use the corresponding EF Core major for application and design-time package
references. See Microsoft's [EF Core release table](https://learn.microsoft.com/en-us/ef/core/what-is-new/)
for framework requirements and servicing dates.

Entity mappings with EF Core's `ComplexProperty` API require EF Core 8.
Mapping complex types to JSON requires EF Core 10. See
[EF Core 8 complex types](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-8.0/whatsnew#value-objects-using-complex-types)
and [EF Core 10 JSON mapping](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew#json).

Use `BlueTuskDataSource`, `BlueTuskBatch`, and `BlueTuskBatchCommand` in
application code shared across all seven runtimes. On .NET 5 and 6 the data
source extends `BlueTuskLegacyDataSource` because `DbDataSource` was
introduced in .NET 7. On .NET 5, the batch types use BlueTusk's compatibility
base classes because `DbBatch` and `DbBatchCommand` were introduced in .NET 6.
The concrete BlueTusk types support the same provider operations on each target.

The repository builds with the SDK pinned in `global.json` (currently
11.0.100). This build-time requirement is separate from the application's
runtime version. The C# compiler and private PolySharp build dependency supply
language support. Runtime-specific code paths preserve functionality where
framework APIs differ. Libraries may use a dependency's compatible .NET
Standard assets on an older runtime. The compatibility suite verifies BlueTusk
behavior on the requested runtime. Microsoft tracks upstream servicing
separately in its [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

The optional PostgreSQL-native Graph engine uses a separate server extension.
Its CI workflow verifies EF and Continuous Graph on .NET 5–11 with PostgreSQL
18, and checks PostgreSQL 15–18 with .NET 10. Install the server binary for
your PostgreSQL distribution, or build it with that server's PGXS toolchain.
See [engine installation and verification](../../extensions/bluetusk_graph/README.md).


## Run the compatibility matrix

PowerShell 7 and the platform's normal .NET native prerequisites are required.
The Edge checkpoint suite also uses Node.js 24. Use the standalone Linux distribution when running in the legacy containers.
The installer creates an isolated SDK/runtime installation under `artifacts`.
Versions come from the checked-in contract, and each downloaded archive must
match the SHA512 in Microsoft's official release metadata before extraction.

```powershell
./eng/install-dotnet-compatibility.ps1
$dotnet = Join-Path $PWD $(if ($IsWindows) { 'artifacts/dotnet/dotnet.exe' } else { 'artifacts/dotnet/dotnet' })
./eng/verify-dotnet-compatibility.ps1
./eng/test-dotnet-compatibility-contract.ps1
./eng/run-dotnet-compatibility.ps1 -DotNetPath $dotnet -UseLegacyLinuxContainers:$IsLinux
```

Select one version while developing:

```powershell
./eng/install-dotnet-compatibility.ps1 -Framework net5.0
./eng/run-dotnet-compatibility.ps1 -DotNetPath $dotnet -Framework net5.0 -UseLegacyLinuxContainers:$IsLinux
```

.NET 5 and 6 Linux runs use digest-pinned official SDK containers. This retains
their required native libraries on modern CI hosts. The runner builds with
the current SDK and stages portable test outputs for the older test controller.
It invokes that controller directly, so the repository's SDK selection cannot
redirect a legacy runtime test to the modern SDK. The .NET 6 controller's
required empty probing-path list is written only into the staged copy.

The Edge SQLite Linux x86-64 fixture uses a bundled native library that
requires glibc 2.34 or later. The .NET 5 Edge job uses the pinned legacy SDK
and OpenSSL from the .NET 5 image on a separately pinned Ubuntu host image.
Its evidence records the derived image and both source image digests. The
official .NET 6 test image already uses Ubuntu 22.04.

The provider and dependency injection suites use an isolated PostgreSQL 18.6
database. The runner starts it automatically from the digest-pinned Linux
image or the SHA256-verified Windows binaries. It generates a per-run SCRAM
password, binds the server to loopback, and stops it after testing. The legacy
Linux test container shares the PostgreSQL container's network namespace. CI
evidence records the server version and distribution identity. Logical WAL
is enabled for tests that exercise replication. Suites that
require an extension use the server and extension versions pinned for that
suite in the contract.

PgVector tests use pgvector 0.8.6. Windows builds the extension from the
verified upstream source using Visual C++ and the PostgreSQL archive headers.
Linux uses a digest-pinned pgvector image. S3 tests use the same MinIO release
on both platforms. The runner verifies each executable's SHA256 and starts
a loopback server with generated credentials. Use `-TestProject` to rerun
registered test projects or CLI packages by project path.

Suites that use Redis, NATS JetStream, Kafka, or OpenSearch run against
digest-pinned Docker services on Linux for every supported .NET runtime.
To include them in a local Windows run with Linux Docker, add
`-UseDockerServiceFixtures`. Ordinary Windows suites run on every target.
For the separate Dashboard browser case, set
`BLUETUSK_DASHBOARD_BROWSER_MODULE` to the installed Playwright module before
building the Control Plane tests.

Each run uses an isolated SDK artifacts path for its build and restore outputs,
so concurrent work on another framework cannot overwrite its NuGet assets.
The runner forbids rolling a test forward to another .NET major, requires the
pinned runtime patch to be present, and rejects zero-test or skipped-test
results. For a registered command-line tool it installs the packed tool for
the selected target and runs its help command. It keeps build logs, test
assemblies and runtime configurations, TRX results, package and payload
hashes, source fingerprints, and a JSON report under
`artifacts/dotnet-compatibility/runs`. A report also records pending package
coverage.

The dedicated
[`dotnet-compatibility` workflow](../../.github/workflows/dotnet-compatibility.yml)
executes each registered suite on Windows and Linux for every contracted .NET
major. Its summary displays both covered and pending package counts. As
migration work completes, add a package's tests and change its status to
`covered`. The verifier checks project targeting and an explicit unconditional
project-reference chain from each registered test suite. Shared provider tests
can therefore also cover the core libraries they execute through the provider.

Before declaring complete runtime support, the following must pass:

```powershell
./eng/verify-dotnet-compatibility.ps1 -RequireComplete
./eng/run-dotnet-compatibility.ps1 -DotNetPath $dotnet -RequireComplete -UseLegacyLinuxContainers:$IsLinux
```

The full gate accepts documented upstream minimums, and fails if any runtime
package remains pending. Release qualification also requires the package,
PostgreSQL, security, performance, endurance, and release-approval gates.

An individual run never declares full runtime qualification. Collect the
reports for every required OS, framework, and suite combination from the same
unchanged source, then verify the complete evidence set:

```powershell
./eng/verify-dotnet-compatibility-evidence.ps1 -ReportPath $reports -RequireMatrixComplete -RequireComplete
```

The aggregate verifier rejects missing or duplicate pairs, changed source
fingerprints, stale contracts, wrong compiled frameworks, modified payloads,
zero tests, failures, and skipped tests. The CI workflow runs this aggregate
check after its matrix jobs. `-Historical` permits inspection of older source
snapshots, and can never establish complete support for the current checkout.

Run asset verification against the candidate NuGet feed after packing. The
verifier checks the framework folders and nuspec dependency groups for
libraries. For CLI packages it checks every framework's installed command,
entrypoint, and runtime configuration. It checks the compiled target in each
managed assembly and lists the SHA256 of each verified artifact.

```powershell
./eng/verify-dotnet-compatibility-packages.ps1 -PackageDirectory artifacts/prerelease/feed
./eng/verify-dotnet-compatibility-packages.ps1 -PackageDirectory artifacts/prerelease/feed -RequireComplete
```
