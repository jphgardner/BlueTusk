# Specification completion audit

This record maps the original BlueTusk product specification and the subsequent
architecture-gap review to concrete repository evidence. “Complete” here means
the engineering surface is implemented, documented, and covered by an
executable gate. All six families were published at `1.0.0` under the
[documented owner exception](releases/1.0.0-publication-record.md), but this
record does not replace external production experience or declare every
PostgreSQL deployment suitable without application-specific validation. Those
release boundaries remain in [Runtime release readiness](release-readiness.md).

## Original product specification

| Specification area | Engineering state | Primary evidence |
| --- | --- | --- |
| 1–5. Vision, layers, repository, package family, and branding | Implemented and published as six layered stable 1.0.0 product families; later publication remains gated. | [Architecture overview](architecture/overview.md), [layering ADR](architecture/decisions/0001-layered-dependency-direction.md), [roadmap](roadmap.md), and the Release packaging gate in [CI](../.github/workflows/build.yml). |
| 6. Transport | Implemented with genuine sync/async socket and TLS paths, cancellation-safe lifetimes, bounded buffers, and multi-host endpoints. | [Transport architecture](architecture/transport.md), [transport tests](../tests/BlueTusk.Transport.Tests/BlueTuskSocketTransportTests.cs), and [TLS tests](../tests/BlueTusk.Transport.Tests/BlueTuskTlsTransportTests.cs). |
| 7. PostgreSQL protocol engine | Implemented independently of ADO.NET and EF, including framing, state transitions, extended query, streaming payloads, cancellation, and capture tooling. | [Protocol guide](protocol/README.md), [state-machine tests](../tests/BlueTusk.Protocol.Tests/BlueTuskProtocolStateMachineTests.cs), [streaming tests](../tests/BlueTusk.Protocol.Tests/BlueTuskProtocolConnectionStreamingTests.cs), and [fake-server conformance](../tests/BlueTusk.ConformanceTests/FakePostgreSqlServerTests.cs). |
| 8. Authentication and security | Implemented for TLS, channel binding, SCRAM, MD5 compatibility, OAuth bearer, GSSAPI/Kerberos, SSPI, client certificates, password files, and redaction. | [Security guide](security.md), [security tests](../tests/BlueTusk.Security.Tests/BlueTuskScramSha256ClientTests.cs), and [authentication conformance](../tests/BlueTusk.ConformanceTests/AuthenticationConformanceTests.cs). |
| 9. Server capability model | Implemented from startup/catalogue facts and exposed through physical sessions and ADO.NET connections. | [Server-capability implementation](../src/BlueTusk.Client/BlueTuskServerCapabilities.cs), [environment integration tests](../tests/BlueTusk.IntegrationTests/BlueTuskEnvironmentIntegrationTests.cs), and [SQL/PGQ capability tests](../tests/BlueTusk.IntegrationTests/BlueTuskSqlPgqIntegrationTests.cs). |
| 10. PostgreSQL type system | Implemented with span-based codecs, catalogue-derived identity, arrays, ranges/multiranges, enums, domains, composites, records, geometric/network/temporal/text-search/catalogue types, source generation, and unknown values. | [Type-system guide](types/README.md), [type-system tests](../tests/BlueTusk.TypeSystem.Tests/BlueTuskTypeCatalogueTests.cs), [structured-codec tests](../tests/BlueTusk.TypeSystem.Tests/BlueTuskCompositeCodecTests.cs), and [live codec tests](../tests/BlueTusk.IntegrationTests/BlueTuskTypeCodecIntegrationTests.cs). |
| 11. Extension SDK | Implemented with immutable data-source feature registration, separate ADO.NET/EF plug-ins, compatibility testing, authoring template, and independently packaged extensions. | [Extension guide](extensions/README.md), [citext ADO.NET tests](../tests/BlueTusk.Extensions.Citext.Tests/BlueTuskCitextTests.cs), [citext EF tests](../tests/BlueTusk.Extensions.Citext.EntityFrameworkCore.Tests/BlueTuskCitextEntityFrameworkCoreTests.cs), and [template contract](../tests/BlueTusk.ExtensionTemplate.Tests/ExtensionTemplateContractTests.cs). |
| 12–14. ADO.NET, pooling, commands, batches, and prepared statements | Implemented with data-source-owned pools, reset isolation, multi-host selection, typed parameters, transactions, preparation, batches, sequential readers, cancellation, sync/async APIs, and provider-factory conformance. | [ADO.NET guide](ado-net/README.md), [pooling guide](ado-net/pooling.md), [pool tests](../tests/BlueTusk.Data.Tests/BlueTuskConnectionPoolTests.cs), [command/session integration](../tests/BlueTusk.IntegrationTests/BlueTuskSessionIntegrationTests.cs), [batch integration](../tests/BlueTusk.IntegrationTests/BlueTuskBatchIntegrationTests.cs), and [provider conformance](../tests/BlueTusk.ConformanceTests/ProviderFactoryConformanceTests.cs). |
| 15. PostgreSQL-native APIs | Implemented for raw and typed COPY, notifications, large objects, and cancellation with failure recovery. | [COPY guide](ado-net/copy.md), [notification guide](ado-net/notifications.md), [large-object guide](ado-net/large-objects.md), [COPY integration](../tests/BlueTusk.IntegrationTests/BlueTuskCopyIntegrationTests.cs), [notification integration](../tests/BlueTusk.IntegrationTests/BlueTuskNotificationIntegrationTests.cs), and [large-object integration](../tests/BlueTusk.IntegrationTests/BlueTuskLargeObjectIntegrationTests.cs). |
| 16. Replication | Implemented as dedicated unpooled physical/logical sessions with `COPY BOTH`, pgoutput, checkpoints, feedback, reconnect validation, ownership documentation, stress, and endurance gates. | [Replication guide](replication/README.md), [wire tests](../tests/BlueTusk.Replication.Tests/BlueTuskReplicationWireProtocolTests.cs), [pgoutput tests](../tests/BlueTusk.Replication.PgOutput.Tests/BlueTuskPgOutputDecoderTests.cs), [live replication](../tests/BlueTusk.IntegrationTests/BlueTuskReplicationIntegrationTests.cs), and [replication stress](../tests/BlueTusk.StressTests/ReplicationStressTests.cs). |
| 17. EF Core provider | Implemented for EF Core 10 relational services, model building, CRUD, query/update pipelines, data-source integration, runtime UDTs, database lifecycle, and design-time tooling. | [EF guide](ef-core/README.md), [data-source integration](../tests/BlueTusk.EntityFrameworkCore.Tests/DataSourceIntegrationTests.cs), [runtime type integration](../tests/BlueTusk.EntityFrameworkCore.Tests/TypeMappingIntegrationTests.cs), [CRUD integration](../tests/BlueTusk.EntityFrameworkCore.Tests/CrudIntegrationTests.cs), and [official specification-test record](ef-core/specification-tests.md). |
| 18. PostgreSQL LINQ support | Implemented for the documented operators, functions, aggregates, JSON, arrays, ranges, row values, set-returning functions, data modification, and supported graph composition. | [EF query documentation](ef-core/README.md), [operator tests](../tests/BlueTusk.EntityFrameworkCore.Tests/PostgreSqlOperatorTranslationTests.cs), [function tests](../tests/BlueTusk.EntityFrameworkCore.Tests/PostgreSqlFunctionTranslationTests.cs), [JSON tests](../tests/BlueTusk.EntityFrameworkCore.Tests/PostgreSqlJsonQueryTranslationTests.cs), and [query-construct tests](../tests/BlueTusk.EntityFrameworkCore.Tests/PostgreSqlQueryConstructTests.cs). |
| 19. PostgreSQL migrations | Implemented for relational objects plus extensions, collations, partitions, publications/subscriptions, views, routines, triggers, rules, event triggers, foreign data, row security, tablespaces, inheritance, UDTs, and property graphs. | [Migration tests](../tests/BlueTusk.EntityFrameworkCore.Tests/MigrationsTests.cs), [migration integration](../tests/BlueTusk.EntityFrameworkCore.Tests/MigrationsIntegrationTests.cs), and the PostgreSQL-specific migration test projects listed in [testing](contributing/testing.md). |
| 20. PostgreSQL 19 property graphs | Implemented behind detected PostgreSQL 19 capability with metadata, migration, reverse-engineering, typed query translation, raw SQL, relational composition, tooling, and an executable sample. | [Graph guide](graph/README.md), [metadata tests](../tests/BlueTusk.EntityFrameworkCore.Tests/PropertyGraphMetadataTests.cs), [migration integration](../tests/BlueTusk.EntityFrameworkCore.Tests/PropertyGraphMigrationIntegrationTests.cs), [query integration](../tests/BlueTusk.EntityFrameworkCore.Tests/PropertyGraphQueryIntegrationTests.cs), [raw SQL integration](../tests/BlueTusk.IntegrationTests/BlueTuskSqlPgqIntegrationTests.cs), and [graph sample](../samples/BlueTusk.Samples.Graph/Program.cs). |
| 21. Database-first scaffolding | Implemented through provider design services, reverse-engineering tests, schema inspection, generated code verification, and property-graph discovery. | [Reverse-engineering tests](../tests/BlueTusk.EntityFrameworkCore.Tests/ReverseEngineeringTests.cs), [design project](../src/BlueTusk.EntityFrameworkCore.Design/BlueTusk.EntityFrameworkCore.Design.csproj), and [schema inspector](../tooling/BlueTusk.SchemaInspector/README.md). |
| 22. Diagnostics and observability | Implemented with redacted diagnostics, activities, meters, pool/native/replication signals, and integration coverage. | [Observability guide](observability.md), [diagnostic unit tests](../tests/BlueTusk.Diagnostics.Tests/BlueTuskDiagnosticsTests.cs), and [diagnostic integration](../tests/BlueTusk.IntegrationTests/BlueTuskDiagnosticsIntegrationTests.cs). |
| 23. Testing strategy | Implemented with fake protocol servers, protocol capture, differential tests, official EF specifications, PostgreSQL 15–19, optional-extension images, PgBouncer, locale/time-zone, primary/standby topology, stress, endurance, and nightly PostgreSQL 19 gates. | [Testing guide](contributing/testing.md), [CI workflow](../.github/workflows/build.yml), [fake server](../tests/BlueTusk.ConformanceTests/FakePostgreSqlServer.cs), [differential tests](../tests/BlueTusk.CompatibilityTests/ProviderDifferentialTests.cs), and [official EF suite](../tests/BlueTusk.EntityFrameworkCore.SpecificationTests/BlueTusk.EntityFrameworkCore.SpecificationTests.csproj). |
| 24. Performance strategy | Implemented as end-to-end and isolated benchmarks with checked-in reports, explicit allocation budgets, transport-decision evidence, and paired Npgsql reference workloads. | [Allocation discipline](architecture/allocation-discipline.md), [benchmark guide](../benchmarks/README.md), [provider comparison](../benchmarks/baselines/windows-ryzen7-5800x-dotnet10/results/BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-github.md), and [transport ADR](architecture/decisions/0005-postgresql-pipeline-mode-and-transport-pipelines.md). |
| 25–29. Delivery, vertical slice, principles, release sequence, and success definition | Engineering milestones are closed and the principles are enforced by tests and CI; stable-release promotion remains an explicit, fail-closed maintainer decision. | [Roadmap](roadmap.md), [release readiness](release-readiness.md), [release process](release-process.md), [architecture conformance](../tests/BlueTusk.ConformanceTests/ArchitectureDependencyTests.cs), and [CI workflow](../.github/workflows/build.yml). |

## Architecture-gap closure

| Priority | Closure evidence |
| --- | --- |
| 0. Finish data-source EF and runtime UDT work | `UseBlueTusk(BlueTuskDataSource)` ownership, option switching, service-provider identity/debug metadata, pool reuse, and runtime catalogue mappings are covered by [provider configuration](../tests/BlueTusk.EntityFrameworkCore.Tests/ProviderConfigurationTests.cs), [data-source integration](../tests/BlueTusk.EntityFrameworkCore.Tests/DataSourceIntegrationTests.cs), and [type-mapping integration](../tests/BlueTusk.EntityFrameworkCore.Tests/TypeMappingIntegrationTests.cs). |
| 1. Data-source-first usage | The [ADO.NET guide](ado-net/README.md), [EF guide](ef-core/README.md), samples, and extension documentation lead with a long-lived `BlueTuskDataSource`; direct `BlueTuskConnection` construction is documented as unpooled. Replication derives a dedicated unpooled option snapshot from the data source as shown in the [replication guide](replication/README.md). |
| 2. Distinguish pipeline mode from `System.IO.Pipelines` | PostgreSQL pipeline mode has explicit synchronization groups, ordered results, error/cancellation recovery, disposal, fake-server, live, conformance, and stress coverage in the [pipeline guide](pipeline-mode.md), [pipeline conformance](../tests/BlueTusk.ConformanceTests/PipelineConformanceTests.cs), and [pipeline stress](../tests/BlueTusk.StressTests/PipelineStressTests.cs). The separate transport experiment and decision are recorded in [ADR 0005](architecture/decisions/0005-postgresql-pipeline-mode-and-transport-pipelines.md). |
| 3. Allocation discipline | Complete command/result, structured-codec, COPY, streaming, replication, EF, and graph paths have checked-in reports and 24 machine-checked budgets in [Allocation discipline](architecture/allocation-discipline.md) and the [benchmark baseline](../benchmarks/baselines/windows-ryzen7-5800x-dotnet10/README.md). Claims exclude inherently owned returned CLR values. |
| 4. Enforce boundaries | [ArchitectureDependencyTests](../tests/BlueTusk.ConformanceTests/ArchitectureDependencyTests.cs) enforce the directed project graph and reject ADO.NET/EF leakage into lower layers. The narrow EF/provider seam is documented in the [architecture overview](architecture/overview.md); protocol tests have no ADO.NET or EF dependency. |
| 5. Complete the extension seam | `BlueTuskDataSourceBuilder.Features` builds an immutable registry carried by the data source. Citext is implemented end-to-end for ADO.NET and EF, optional packages remain isolated, and the template plus compatibility harness are executable through the [extension guide](extensions/README.md) and [template contract](../tests/BlueTusk.ExtensionTemplate.Tests/ExtensionTemplateContractTests.cs). |
| 6. Keep replication first-class | Physical/logical replication and pgoutput remain separate packages and never borrow pooled ADO.NET sessions. Allocation/backpressure, cancellation/disposal, reconnect/checkpoint, PG15–19, stress, and scheduled endurance evidence is catalogued in the [replication guide](replication/README.md) and [release readiness](release-readiness.md). |
| 7. PostgreSQL 19 and SQL/PGQ | Capability detection, live DDL/query/preparation/batch/cancellation/pooling coverage, metadata, migrations, reverse engineering, typed `GRAPH_TABLE` translation, sample, and tooling are implemented while PG15–18 remain green. Beta-sensitive syntax is isolated and documented in the [graph guide](graph/README.md). |
| 8. Documentation and release truthfulness | README, roadmap, stable-candidate package versions, support limits, executable evidence, and publication boundaries are reconciled in [release readiness](release-readiness.md). The documentation gate validates every tracked local link on Windows and Linux. |

## Real-time platform phased plan

The real-time platform adds a second release train on top of the provider. The
engineering state and the release state are tracked separately: passing unit,
integration, package, and compatibility gates creates a candidate, while a
required real-duration endurance report is the evidence that permits promotion.

| Phase | Engineering state | Primary evidence | Remaining release boundary |
| --- | --- | --- | --- |
| 0. Architecture and release groundwork | Implemented. Delivery, checkpoint, snapshot, spool, relay, Live security, and Sync destination decisions are recorded; dependency direction and publication policy are executable. | [ADRs 0006–0012](architecture/decisions/0006-streams-delivery-semantics.md), [product-family manifest](../eng/product-families.json), [release process](release-process.md), and [product-family architecture tests](../tests/BlueTusk.ConformanceTests/ProductFamilyArchitectureTests.cs). | None for the architecture gate. |
| 1. Streams transaction kernel | Implemented for ordinary, streamed, committed, aborted, prepared, and two-phase transactions with explicit tuple state and bounded spooling. | [pgoutput stream tests](../tests/BlueTusk.Streams.Tests/PgOutputChangeStreamTests.cs), [durable-state tests](../tests/BlueTusk.Streams.Tests/DurableStateTests.cs), and [live replication tests](../tests/BlueTusk.IntegrationTests/BlueTuskReplicationIntegrationTests.cs). | Included in the Streams release-endurance gate. |
| 2. Checkpoints, leases, groups, and relay | Implemented with monotonic compare-and-swap state, fencing, direct groups, durable PostgreSQL relay, replay, retention, and separate-control-schema validation. | [state-store conformance](../tests/BlueTusk.Streams.Tests/StateStoreConformanceTests.cs), [relay integration](../tests/BlueTusk.IntegrationTests/BlueTuskStreamsRelayIntegrationTests.cs), and [storage validation](../tests/BlueTusk.Streams.Tests/PostgreSqlStorageValidationTests.cs). | Included in the Streams release-endurance gate. |
| 3. Typed Streams and snapshot bootstrap | Implemented with typed/dynamic mappings, EF mapping, exported-snapshot restart semantics, CloudEvents, DI, Aspire, CLI, health, telemetry, testing helpers, and all registered packages. | [typed mapping tests](../tests/BlueTusk.Streams.Tests/TypedChangeMappingTests.cs), [snapshot coordinator tests](../tests/BlueTusk.Streams.Tests/SnapshotThenStreamCoordinatorTests.cs), [Streams package manifest](../eng/product-families.json), and [Streams guide](streams/README.md). | Stable 1.0.0 artifacts are published; the deferred exact-candidate evidence remains gated hardening work. |
| 4. Streams hardening and Control Plane foundation | Engineering slices are implemented, including format/API freezes, relay upgrades, operations APIs, dashboard views, and release evidence tooling. | [Streams API freeze](../eng/streams-api-freeze.json), [Streams format registry](../eng/streams-formats.json), [Control Plane API freeze](../eng/control-plane-api-freeze.json), [Control Plane format registry](../eng/control-plane-formats.json), [Control Plane tests](../tests/BlueTusk.ControlPlane.Tests/ControlPlaneOperationTests.cs), and [Streams endurance verifier](../eng/verify-streams-endurance-report.ps1). | A verified, archived 72-hour report for the exact Streams candidate is still mandatory. |
| 5. Sync 1.0 | The pipeline, all four destinations, conformance kit, transforms, quarantine/replay, reconciliation/repair, rebuild/cutover, hosting, telemetry, Aspire, candidate API freeze, and durable-format registry are implemented. | [shared destination conformance](../tests/BlueTusk.Sync.Testing.Tests/SyncDestinationConformanceSuiteTests.cs), [Sync API freeze](../eng/sync-api-freeze.json), [format registry](../eng/sync-formats.json), and [Sync guide](sync/README.md). | A verified, archived 24-hour report for the exact Sync candidate is still mandatory before publication. |
| 6. Live 1.0 | Authorised registered EF plans, gap-free initial delivery, keyed diffing, replay, signed resume, security-scoped sharing, quotas, SignalR, SSE, gRPC, TypeScript, Angular, React, advanced query capabilities, candidate API freeze, and durable-format registry are implemented. | [query compiler tests](../tests/BlueTusk.Live.Tests/LiveEfQueryCompilerTests.cs), [adversarial/load tests](../tests/BlueTusk.Live.Tests/LiveLoadGateTests.cs), [transport matrix](../tests/BlueTusk.Live.Tests/LivePostgreSqlTransportMatrixTests.cs), and [Live API freeze](../eng/live-api-freeze.json). | Live 1.0 cannot be published ahead of its Streams dependency and final release verification. |
| 7. ContinuousGraph 1.0 | Implemented as capability-guarded registered SQL/PGQ plans with dependency-aware invalidation, authoritative `GRAPH_TABLE` requery/diff, bounded affected-key incremental maintenance with authoritative repair, samples, an optional Control Plane adapter, API freeze, and benchmarks. | [compiler tests](../tests/BlueTusk.ContinuousGraph.Tests/ContinuousGraphQueryCompilerTests.cs), [incremental state-machine tests](../tests/BlueTusk.ContinuousGraph.Tests/ContinuousGraphIncrementalTests.cs), [PostgreSQL 19 integration](../tests/BlueTusk.ContinuousGraph.Tests/ContinuousGraphQueryIntegrationTests.cs), [API freeze](../eng/continuous-graph-api-freeze.json), [ADR 0016](architecture/decisions/0016-authoritative-incremental-graph-maintenance.md), and the [ContinuousGraph guide](continuous-graph/README.md). | PostgreSQL 19 GA, the exact 24-hour/100,000-evaluation endurance report, dependency publication, and an independent pilot remain mandatory. |

`artifacts/` is ignored by source control because endurance runs contain large
binary and test-result trees. A release gate is complete only after its verifier
accepts the report for the expected source commit and the workflow archives that
report with the release record. A running process, short smoke result, or report
from an earlier candidate is not acceptable evidence.

## Current verification snapshot

The final local matrix on 2026-08-02 used the same Release binaries and the CI
connection contract across all supported PostgreSQL versions:

| PostgreSQL | Test assemblies | Passed | Intentional skips | Failed |
| --- | ---: | ---: | ---: | ---: |
| 15 | 28 | 2,963 | 147 | 0 |
| 16 | 28 | 2,964 | 146 | 0 |
| 17 | 28 | 2,966 | 146 | 0 |
| 18 | 28 | 2,978 | 146 | 0 |
| 19 Beta 2 | 28 | 2,978 | 146 | 0 |

On 2026-08-17, after the official milestone advanced, the complete serial
solution suite was rerun against the digest-pinned PostgreSQL 19 Beta 3 image
with zero failures. The table above remains the exact dated 2026-08-02
provider-only snapshot rather than retroactively relabelling that evidence.

The provider audit produced a zero-warning Release build and 31 stable 1.0.0
candidate packages. Its V1 candidate API budget locks 8,308 signatures across 28
API-governed Provider library surfaces, while package conformance excludes
embedded template content projects. The current monorepo-wide gate covers 123
solution projects, reports no vulnerable direct or transitive NuGet
dependencies, validates every repository-local documentation link, and passes
46 allocation budgets.
The two additional solution projects are the non-packable pg_durable preview
adapter and its tests. They do not alter the stable Provider package or API
totals and cannot be used as production-readiness evidence for upstream
pg_durable.
The final two-launch
provider MediumRun records lower BlueTusk mean latency and managed allocation in
all five paired workloads; parameterized scalar, warm checkout, and 1,000-row
streaming have non-overlapping latency intervals, while prepared scalar and the
1 MiB stream remain statistical parity despite lower BlueTusk means. Exact
values, confidence intervals, workload fairness, and environment details are in
the [checked-in provider report](../benchmarks/baselines/windows-ryzen7-5800x-dotnet10/results/BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-github.md).

## Explicit release boundaries

- Packages are published at stable `1.0.0`, but publication and engineering-gate
  completion do not substitute for the deferred external production experience
  or independent approval.
- PostgreSQL 19 coverage uses the official Beta 3 image plus a scheduled build of
  the upstream PostgreSQL 19 branch; beta syntax can still change before GA.
- Real-account AWS, Azure, and Google Cloud identity acceptance remains opt-in
  because repository CI does not hold customer credentials; deterministic SDK
  contract tests are mandatory and pass without those credentials.
- Bounded statement multiplexing is implemented for data-source-owned,
  session-neutral commands. Explicit connections, transactions, prepared
  commands, replication, notifications, and classified stateful SQL remain
  intentionally session-affine. The release gate includes forced shutdown,
  cancellation/error isolation, and a direct Npgsql benchmark comparison.
- One tablespace integration case requires a server-owned filesystem directory
  and is intentionally skipped where that external directory is not configured.
