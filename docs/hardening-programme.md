# V1 hardening programme

The Provider → Streams → Sync → Live → Continuous Graph development chain is
feature-complete for the V1 candidate. Publication remains disabled while the
evidence below is incomplete. Internal tests and candidate packages are not, by
themselves, a production-readiness claim.

## Gap-to-evidence matrix

| Priority | Current position | Evidence required to close | Status |
| --- | --- | --- | --- |
| 1. Minimal EF↔Data SPI | EF now consumes an internal Data-owned contract for connection/data-source creation, ownership, immutable type-registry snapshots, capabilities, admin connections, catalogue reload and diagnostics. Concrete types remain only at the public configuration boundary. | Contract tests, a source architecture guard, EF ownership tests, zero-warning build and documentation. | Complete |
| 2. NativeAOT and trimming | Transport through Data plus the required extension-abstraction dependency now pass full-trim and NativeAOT publishes. Runtime-selected shapes have explicit boundaries. | `PublishTrimmed` and `PublishAot` smoke applications, correct annotations/source generation, unsupported-feature diagnostics, Windows/Linux CI, startup/allocation/deployable-size baselines. | Complete |
| 3. Multiplexing hardening | Bounded multiplexing now fails closed for every known session-affine surface, exposes bounded scheduler telemetry, and has deterministic fairness/failure/recovery coverage. | Fairness, exhaustion, admission cancellation, timeout, pipeline isolation, disposal, forced shutdown, session recovery, stateful SQL, PgBouncer session/transaction modes, and a commit-bound MediumRun against both non-multiplexed BlueTusk and Npgsql. | Complete |
| 4. Coverage-guided fuzzing | Nine bounded SharpFuzz/AFL++ targets cover protocol frames, authentication, pgoutput, binary COPY, arrays, ranges, composites, Streams envelopes and Live resume tokens. Replayable Base64 corpus cases, deterministic tests, CI smoke, scheduled runs, finding archival and minimisation tooling are checked in. | Keep minimized findings in the deterministic corpus and require a clean bounded run for the exact candidate. | Complete |
| 5. Release evidence | Reproducible Streams 72-hour and Sync 24-hour harnesses and fail-closed report verifiers exist. Exact-candidate reports have not been completed and archived. | Reports tied to commit, package hashes, runtime, OS and image digests, including process, network, storage, credential, failover, clock and minor-upgrade faults. | Open |
| 6. ADO.NET compatibility | The supported/excluded contract now covers routines, parameters, transactions, reader behaviours, schema APIs, Dapper, DI, health checks and the Npgsql migration path. Unsupported modes fail explicitly. | Unit and live acceptance coverage plus the published compatibility matrix. | Complete |
| 7. API and supply chain | Exact per-family API budgets, CodeQL, dependency review, commit-pinned Actions, CycloneDX/SPDX generation, package-hash provenance verification, a fail-closed 24-occurrence intentional test-credential inventory and an independent-review handoff are enforced. | Run the gates, disposition the external scanner records and archive the generated evidence for the exact candidate. | Implemented; scanner disposition pending |
| 8. PostgreSQL 19 programme | Beta 3 is digest-pinned; capability guards, raw-SQL escape hatches, the typed-subset record, upstream drift detection and the later beta/RC/GA cadence are machine-enforced. | Repeat the matrix at each future milestone; GA evidence is a fail-closed stable-publication prerequisite. | Implemented; GA pending |

## Phased implementation

Work is deliberately sequential at the programme level. A phase closes only
when its code, tests, documentation and reproducible verification command are
present.

1. **Internal provider boundary — complete.** Keep
   `IProviderServices`, `IProviderConnection` and `IProviderDataSource`
   assembly-internal. EF may mention concrete provider types only in its public
   `UseBlueTusk` overloads. Decision: [ADR 0017](architecture/decisions/0017-internal-ef-data-provider-spi.md).
2. **NativeAOT and trimming — complete.** Transport, Protocol, Security,
   TypeSystem, Client, Diagnostics, Data, and the required extension abstraction
   now carry trimming/AOT contracts. Executable smoke applications cover the
   linker and native compiler, common arrays and ranges, source-generated and
   reflection composites, startup, allocation, and deployable size. Unsupported
   runtime-selected shapes fail explicitly. The measured results do not justify
   a slim builder yet.
3. **Multiplexing and ADO.NET compatibility — complete.** The scheduler/session
   hardening matrix, PgBouncer modes, telemetry and comparative performance
   record are complete. The provider contract is recorded in the
   [V1 ADO.NET compatibility matrix](ado-net/compatibility.md). ADR 0005 remains
   in force: the measured
   ArrayPool/Span/Memory transport is retained because no new end-to-end result
   has cleared its adoption gate.
4. **Coverage-guided fuzzing — complete.** Nine parser targets enforce a
   64-KiB input ceiling, execution and managed-heap limits, bounded protocol
   loops and collection-size limits. The checked-in encoded corpus is replayed
   deterministically; CI and scheduled AFL++ runs archive and minimize findings.
   Commands and triage procedure are in [the fuzzing guide](fuzzing.md).
5. **Supply-chain and public-surface gates — complete.** Exact per-family API
   budgets, security analysis, dependency review, immutable Action pins,
   CycloneDX/SPDX generation, package hashes and provenance verification are
   enforced without expanding public product-family breadth.
6. **Candidate evidence — open.** Run and archive the exact 72-hour Streams and
   24-hour Sync candidates with all seven operational disturbances inside each
   native report window. The cross-run verifier requires 14 recoveries and 28
   content-addressed observation summaries. No short run substitutes for
   either release gate.
7. **PostgreSQL 19 progression — GA pending.** Re-run the isolated SQL/PGQ programme for every
   later beta, RC and GA build; keep beta-sensitive syntax isolated until GA
   evidence is archived.

## Completed slice verification

The internal provider-boundary slice is reproducible with:

```powershell
dotnet test tests/BlueTusk.EntityFrameworkCore.Tests/BlueTusk.EntityFrameworkCore.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ProviderSpiTests|FullyQualifiedName~DatabaseCreatorTests|FullyQualifiedName~ProviderConfigurationTests"
dotnet build BlueTusk.slnx --no-restore --configuration Release
./eng/verify-documentation.ps1
```

Live database lifecycle tests additionally require
`BLUETUSK_TEST_CONNECTION_STRING`. A skipped live test does not count as
external production evidence.

The provider-core NativeAOT/trimming slice is reproducible on Windows x64 with:

```powershell
dotnet restore tests/BlueTusk.TrimSmoke/BlueTusk.TrimSmoke.csproj -r win-x64
dotnet restore tests/BlueTusk.NativeAotSmoke/BlueTusk.NativeAotSmoke.csproj -r win-x64
./eng/verify-provider-core-publish.ps1 -RuntimeIdentifier win-x64 -NoRestore
dotnet test tests/BlueTusk.TypeSystem.Tests/BlueTusk.TypeSystem.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~BlueTuskRangeCodecTests|FullyQualifiedName~BlueTuskArrayCodecTests"
```

CI runs the equivalent `win-x64` and `linux-x64` publish matrix and archives the
measurement reports.

The multiplexing slice is reproducible with:

```powershell
dotnet test tests/BlueTusk.Data.Tests/BlueTusk.Data.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~BlueTuskMultiplexingTests"
dotnet test tests/BlueTusk.IntegrationTests/BlueTusk.IntegrationTests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~BlueTuskMultiplexingIntegrationTests"
$env:BLUETUSK_BENCHMARK_CONNECTION_STRING = "<dedicated PostgreSQL connection string>"
dotnet run --project benchmarks/BlueTusk.Benchmarks/BlueTusk.Benchmarks.csproj `
  --configuration Release -- `
  --job medium --inProcess --filter "*MultiplexingComparisonBenchmarks*"
dotnet run --project benchmarks/BlueTusk.Benchmarks/BlueTusk.Benchmarks.csproj `
  --configuration Release --no-build -- `
  --multiplexing-paired-evidence artifacts/benchmarks/multiplexing-paired-evidence.json
./eng/verify-multiplexing-performance.ps1 `
  -ReportPath artifacts/benchmarks/results/BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json `
  -PairedReportPath artifacts/benchmarks/multiplexing-paired-evidence.json
./eng/test-multiplexing-performance-verifier.ps1
```

The checked-in run is bound to source commit `9ba2c50`, PostgreSQL image digest
`sha256:9a8afca54e7861fd90fab5fdf4c42477a6b1cb7d293595148e674e0a3181de15`,
and a SHA-256-protected full report. It records lower BlueTusk mean, P95 and
P99 latency than Npgsql for fresh and reused multiplexed commands in this named
loopback environment. It also records lower latency and allocation than
non-multiplexed BlueTusk. This closes the regression-evidence gate, not the
independent production-validation gate.

Exact-candidate runs retain that full BenchmarkDotNet evidence for absolute
latency and allocation, and add five alternating-provider trials for the
provider-relative latency decision. Each trial contains 101 paired blocks of 32
bursts per provider, with provider order reversed between blocks and trials.
The gate recomputes mean, P95 and P99 from raw timings and evaluates the median
trial ratio against the existing 5% ceiling. The 101 observations prevent a
single maximum from dominating P99, while provider alternation prevents
sequential machine or server drift from manufacturing a pass or a failure.
