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
| 3. Multiplexing hardening | Bounded multiplexing, conservative affinity routing and scheduler metrics exist. The complete failure/fairness/session-state matrix and current comparative P95/P99 evidence remain open. | Fairness, exhaustion, cancellation, timeout, isolation, disposal/recovery and PgBouncer tests plus BlueTusk non-multiplexed and Npgsql comparisons. | Partial |
| 4. Coverage-guided fuzzing | Parser and malformed-input unit tests exist, but there is no coverage-guided harness or checked-in minimized corpus. | Bounded fuzz targets for protocol, authentication, pgoutput, COPY, structured codecs, Streams envelopes and resume tokens; CI smoke and scheduled run. | Open |
| 5. Release evidence | Reproducible Streams 72-hour and Sync 24-hour harnesses and fail-closed report verifiers exist. Exact-candidate reports have not been completed and archived. | Reports tied to commit, package hashes, runtime, OS and image digests, including process, network, storage, credential, failover, clock and minor-upgrade faults. | Open |
| 6. ADO.NET compatibility | Core conformance and EF relational specification coverage exist. A single supported/excluded compatibility matrix does not. | StoredProcedure/INOUT, `System.Transactions`, `CommandBehavior`, schema APIs, Dapper, DI, health checks and migration-path audit with tests or explicit exclusions. | Open |
| 7. API and supply chain | Candidate API freezes and release provenance exist. API budgets, CodeQL, dependency review, SHA-pinned Actions and SBOM evidence are incomplete. | Per-family API budget gate, security workflows, SBOM/provenance retention and independent review preparation. | Partial |
| 8. PostgreSQL 19 programme | Beta 2, capability guards, raw-SQL escape hatches and an upstream branch-snapshot gate exist. The later beta/RC/GA cadence is not yet evidenced. | Repeatable beta/RC/GA matrix, catalogue/grammar drift checks and a published typed-subset compatibility record. | Partial |

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
3. **Multiplexing and ADO.NET behaviour.** Complete the scheduler/session
   hardening matrix first, then use the same fixtures to publish the ADO.NET
   compatibility table and comparative performance record. ADR 0005 remains in
   force: the measured ArrayPool/Span/Memory transport is retained unless a new
   end-to-end result clears its adoption gate.
4. **Coverage-guided fuzzing.** Introduce bounded harnesses and a minimized
   regression corpus after the provider execution surfaces are stable.
5. **Supply-chain and public-surface gates.** Add API budgets, security analysis,
   dependency review, SHA pinning and SBOM production without expanding public
   product-family breadth.
6. **Candidate evidence.** Run and archive the exact 72-hour Streams and 24-hour
   Sync candidates with the full fault matrix. No short run substitutes for
   either release gate.
7. **PostgreSQL 19 progression.** Re-run the isolated SQL/PGQ programme for every
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
