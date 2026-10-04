# BlueTusk documentation

BlueTusk is a PostgreSQL platform for .NET 10. Use this page to find the guide
for what you want to do.

## Start here

New to BlueTusk? Read these in order. Together they take about 20 minutes.

1. [What is BlueTusk?](getting-started/overview.md): the products and how they fit together.
2. [Install BlueTusk](getting-started/install.md): choose packages and a version.
3. [5-minute first app](getting-started/quickstart.md): connect and run a query.
4. [Core concepts](getting-started/concepts.md): the vocabulary every product uses.

## Products

Every product has the same set of pages, in the order you need them:
an overview, a quick start, concepts, task guides, configuration and
troubleshooting.

| Product | What it does | Quick start | Concepts | Configuration | Troubleshooting |
| --- | --- | --- | --- | --- | --- |
| [ADO.NET](ado-net/README.md) | Connections, commands, transactions, COPY, notifications | [Quick start](ado-net/quickstart.md) | [Concepts](ado-net/concepts.md) | [Configuration](ado-net/configuration.md) | [Troubleshooting](ado-net/troubleshooting.md) |
| [EF Core](ef-core/README.md) | LINQ, change tracking, migrations and scaffolding | [Quick start](ef-core/quickstart.md) | [Concepts](ef-core/concepts.md) | [Configuration](ef-core/configuration.md) | [Troubleshooting](ef-core/troubleshooting.md) |
| [Streams](streams/README.md) | Process every committed change, in order | [Quick start](streams/quickstart.md) | [Concepts](streams/concepts.md) | [Configuration](streams/configuration.md) | [Troubleshooting](streams/troubleshooting.md) |
| [Sync](sync/README.md) | Copy committed changes to another database, cache, broker or index | [Quick start](sync/quickstart.md) | [Concepts](sync/concepts.md) | [Configuration](sync/configuration.md) | [Troubleshooting](sync/troubleshooting.md) |
| [Live](live/README.md) | Push live query results to browsers and .NET clients | [Quick start](live/quickstart.md) | [Concepts](live/concepts.md) | [Configuration](live/configuration.md) | [Troubleshooting](live/troubleshooting.md) |
| [Control Plane](control-plane/README.md) | Inspect and operate running BlueTusk components | [Quick start](control-plane/quickstart.md) | [Concepts](control-plane/concepts.md) | [Configuration](control-plane/configuration.md) | [Troubleshooting](control-plane/troubleshooting.md) |

Related provider topics:

- [PostgreSQL types](types/README.md): how .NET values map to PostgreSQL types.
- [PostgreSQL extensions](extensions/README.md): pgvector, PostGIS,
  TimescaleDB, citext, hstore, ltree and pg_trgm.
- [Cloud identity](ado-net/cloud-identity.md): AWS, Azure and Google Cloud
  sign-in.
- [Replication protocol](replication/README.md): raw logical and physical
  replication, for when Streams is too high level.
- [Pipeline mode](pipeline-mode.md): low-level PostgreSQL pipelining.

Real-time products share one delivery model. Read
[delivery guarantees](realtime-platform/contracts.md) once before you build
with Streams, Sync or Live, and see
[choosing a real-time product](realtime-platform/README.md) if you are not
sure which one you need.

## Preview products

These are not part of the 1.1.0 release. You can evaluate them, but their API
and behavior may change.

- [Graph (SQL/PGQ)](graph/README.md) and
  [Continuous Graph](continuous-graph/README.md) need a PostgreSQL server that
  provides SQL/PGQ. PostgreSQL removed SQL/PGQ in PostgreSQL 19 Beta 4, so
  they wait for a PostgreSQL release that ships it.
- The ecosystem families are `0.1.0-preview.1` and not published yet. Each is
  released when its own checks pass:
  [Events](events/README.md), [Jobs](jobs/README.md),
  [Workflows](workflows/README.md), [Documents](documents/README.md),
  [Schema](schema/README.md), [Projections](projections/README.md),
  [Search](search/README.md), [Sql](sql/README.md), [Studio](studio/README.md)
  and [Edge](edge/README.md). See the
  [ecosystem release plan](ecosystem/release-qualification.md).

## Run in production

Work through the [production checklist](operations/production-checklist.md)
first. Then use the guide for each task:

| Task | Guide |
| --- | --- |
| Deploy | [Deployment](operations/deployment.md) |
| Secure | [Security](security.md) |
| Monitor | [Observability](operations/observability.md) |
| Size and tune | [Performance and capacity](operations/performance.md) |
| Fix problems | [Troubleshooting](operations/troubleshooting.md) |
| Upgrade or roll back | [Upgrade guide](operations/upgrade-guide.md) |
| Recover real-time products | [Recovery and rebuilds](realtime-platform/operations.md) |

## Reference

For maintainers, reviewers and incident investigations:

- [Support matrix and versioning](../VERSIONING.md)
- [Architecture overview](architecture/overview.md) and
  [architecture decisions](architecture/decisions/)
- [API compatibility](api-compatibility.md)
- Product references: [EF Core](ef-core/reference.md),
  [Sync](sync/reference.md), [Live](live/reference.md),
  [Control Plane](control-plane/reference.md), [types](types/reference.md)
- [Release process](release-process.md) and
  [release readiness records](v1-release-readiness.md)
- [Contributing](../CONTRIBUTING.md), [development setup](contributing/development.md),
  [testing](contributing/testing.md) and
  [repository layout](contributing/repository-layout.md)

## Conventions in these docs

- Shell commands are PowerShell. Where bash differs, the page says so.
- Examples use parameters for every value. Never build SQL from user input.
- `SSL Mode=Disable` appears only in examples that use a local test container.
  Keep the default, `VerifyFull`, everywhere else.
- **New in 1.1.0** marks a feature that is not in `1.0.0` or `1.1.0-rc.1`.

Found a mistake? Open an issue with the page URL, the package version, the
PostgreSQL version and the smallest example that shows the problem.
