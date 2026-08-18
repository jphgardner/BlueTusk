# BlueTusk documentation

BlueTusk is a PostgreSQL-native .NET platform built as a set of independently
versioned product families. The documentation follows the same boundaries as
the code: start with the provider, add EF Core or an extension when the
application needs it, and adopt Streams, Sync, Live, Control Plane or
Continuous Graph only when their delivery contracts match the workload.

The V1 implementation and release-hardening work are complete. Stable
publication is still fail closed while exact-candidate endurance evidence,
its 14 required in-window operational disturbance recoveries, PostgreSQL 19 GA
evidence, independent review and operational rehearsals remain outstanding.
Start with [V1 release readiness](v1-release-readiness.md) if you need to
distinguish implemented code from release authorization.

## Choose a path

| You want to… | Start here | Then read |
| --- | --- | --- |
| Evaluate BlueTusk locally | [Quickstart](getting-started/quickstart.md) | [Core concepts](getting-started/concepts.md) |
| Build with ADO.NET | [Provider guide](ado-net/README.md) | [Compatibility matrix](ado-net/compatibility.md) and [dependency injection](ado-net/dependency-injection.md) |
| Build with EF Core | [EF Core guide](ef-core/README.md) | [Specification coverage](ef-core/specification-tests.md) |
| Move committed changes | [Real-time platform](realtime-platform/README.md) | [Streams](streams/README.md) and [operations](realtime-platform/operations.md) |
| Synchronize destinations | [Sync](sync/README.md) | [Delivery contracts](realtime-platform/contracts.md) |
| Deliver authorized live data | [Live](live/README.md) | [Security](security.md) |
| Use PostgreSQL extensions | [Extensions](extensions/README.md) | [Type system](types/README.md) |
| Query or maintain graph data | [SQL/PGQ](graph/README.md) | [Continuous Graph](continuous-graph/README.md) |
| Operate a deployment | [Deployment](operations/deployment.md) | [Troubleshooting](operations/troubleshooting.md), [production observability](operations/observability.md) and [V1 production readiness](operations/production-readiness.md) |
| Contribute | [Repository layout](contributing/repository-layout.md) | [Testing](contributing/testing.md) |
| Prepare a release | [Release process](release-process.md) | [Operational approval evidence](operations/approval-evidence.md) and [independent review handoff](release-review-handoff.md) |

## Product families

### Provider

The Provider family owns the PostgreSQL wire path: transport, protocol,
authentication, pooling, ADO.NET, types, COPY, notifications, large objects,
pipeline mode and replication. It does not wrap Npgsql at runtime.

Read:

- [ADO.NET provider](ado-net/README.md)
- [Supported and intentionally excluded ADO.NET behavior](ado-net/compatibility.md)
- [Authentication](ado-net/authentication.md) and
  [cloud identity](ado-net/cloud-identity.md)
- [Pooling](ado-net/pooling.md), [multi-host routing](ado-net/multi-host.md) and
  [bounded multiplexing](ado-net/multiplexing-compatibility.md)
- [Type system](types/README.md), [COPY](ado-net/copy.md) and
  [replication](replication/README.md)

### EF Core

The EF Core family adds PostgreSQL-native mappings, translations, migrations,
scaffolding and design-time tooling on top of the provider-owned data source.
It consumes an internal Provider SPI rather than taking ownership of the wire
stack.

Read:

- [EF Core provider](ef-core/README.md)
- [Official relational specification coverage](ef-core/specification-tests.md)
- [Internal EF↔Data boundary](architecture/decisions/0017-internal-ef-data-provider-spi.md)

### Streams, Sync and Live

The real-time families share source identity, transaction and checkpoint
concepts but deliberately keep different delivery semantics:

- **Streams** turns logical replication into acknowledged committed
  transactions and owns snapshot bootstrap, state stores and durable relay.
- **Sync** applies versioned transforms to PostgreSQL, Redis, NATS and
  OpenSearch destinations, with quarantine, reconciliation and rebuilds.
- **Live** performs authorized re-query and delivery over server-sent events,
  SignalR and gRPC/browser client surfaces.

Read:

- [Platform contracts](realtime-platform/contracts.md)
- [Streams](streams/README.md), [Sync](sync/README.md) and
  [Live](live/README.md)
- [Production operations](realtime-platform/operations.md)
- [Streams 72-hour](streams/release-endurance.md) and
  [Sync 24-hour](sync/release-endurance.md) evidence contracts

### Control Plane and Continuous Graph

Control Plane manages deployment state, operational queries, reconciliation,
auditing and the dashboard boundary. Continuous Graph consumes acknowledged
changes into checkpointed graph projections for workloads such as fraud and
network topology.

Read:

- [Control Plane](control-plane/README.md)
- [Continuous Graph](continuous-graph/README.md)
- [Managed-hosting reconciliation decision](architecture/decisions/0014-managed-hosting-reconciliation.md)
- [Incremental graph maintenance decision](architecture/decisions/0016-authoritative-incremental-graph-maintenance.md)

## Architectural foundations

BlueTusk uses a strict downward dependency direction:

```text
Application
  ├─ EF Core / extensions
  ├─ ADO.NET Provider
  ├─ Client / protocol / transport
  └─ PostgreSQL

Logical replication
  └─ Streams
      ├─ Sync
      ├─ Live
      ├─ Control Plane
      └─ Continuous Graph
```

The important cross-cutting rules are:

1. PostgreSQL behavior is the specification.
2. Capability discovery is stronger than version-string inference.
3. Every untrusted length, count and collection is bounded before allocation.
4. Logical connection ownership is distinct from physical pooled-session
   ownership.
5. Acknowledgement, checkpointing and destination application are separate
   operations.
6. Public API, persisted format and package changes are mechanically checked.
7. A successful local build is evidence, not publication permission.

The [architecture overview](architecture/overview.md) explains the layers and
the [architecture decisions](architecture/decisions/) record why the important
boundaries exist.

## Operations and release truth

Use these documents when the question is not “how do I call the API?”:

- [Deployment and configuration](operations/deployment.md)
- [Application platform health and rollout acceptance](operations/application-platform-health.md)
- [Troubleshooting](operations/troubleshooting.md)
- [Performance engineering](operations/performance.md)
- [Production observability and SLOs](operations/observability.md)
- [V1 production-readiness gates and exact-candidate evidence](operations/production-readiness.md)
- [Canonical V1 package evidence](operations/package-evidence.md)
- [V1 operational approval evidence](operations/approval-evidence.md)
- [Angular website production contract](operations/website-production.md)
- [Upgrade guide](operations/upgrade-guide.md)
- [Observability](observability.md)
- [Security model](security.md)
- [Testing profiles](contributing/testing.md)
- [Release process](release-process.md)
- [V1 hardening programme](hardening-programme.md)
- [PostgreSQL 19 programme](postgresql19-programme.md)
- [V1 application suite and RC deployment](v1-applications.md)

## Documentation contract

Repository Markdown is canonical. The Angular website transforms the selected
Markdown files into searchable guides during its build and fails when generated
content drifts. When behavior changes:

1. update the implementation and focused tests;
2. update the relevant public API or format baseline;
3. update the canonical Markdown guide and examples;
4. update release/readiness evidence if the claim changed; and
5. regenerate and validate the website documentation.

Examples must use parameterized SQL, explicit cancellation where useful and
honest package availability. Experimental-feature boundaries,
stable-publication gates and
external production validation must never be collapsed into one status.
