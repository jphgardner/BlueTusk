# BlueTusk documentation

Use this page to find the shortest path from a goal to the relevant guide.
BlueTusk is split into independently versioned product families, so most
applications need only a small part of the documentation.

## Start here

1. [Choose and install packages](getting-started/install.md).
2. [Run the local quickstart](getting-started/quickstart.md).
3. Read the [core concepts](getting-started/concepts.md) before adding
   replication or real-time delivery.
4. Use the [production checklist](operations/production-checklist.md) before
   sending production traffic.

BlueTusk `1.0.0` is the stable line. `1.1.0-rc.1` is the current published
prerelease. The [support matrix](../VERSIONING.md) is the authority for .NET,
EF Core, and PostgreSQL compatibility.

## Find a guide by task

| I want to… | Read |
| --- | --- |
| Open connections and run commands | [ADO.NET provider](ado-net/README.md) |
| Configure dependency injection and health checks | [Dependency injection](ado-net/dependency-injection.md) |
| Authenticate securely | [Authentication](ado-net/authentication.md) and [cloud identity](ado-net/cloud-identity.md) |
| Tune connection use | [Pooling](ado-net/pooling.md), [multi-host routing](ado-net/multi-host.md), and [multiplexing](ado-net/multiplexing-compatibility.md) |
| Move bulk data | [COPY](ado-net/copy.md) |
| Decode PostgreSQL changes | [Replication](replication/README.md) |
| Use LINQ, migrations, or scaffolding | [EF Core](ef-core/README.md) |
| Add PostGIS, pgvector, or TimescaleDB | [Extensions](extensions/README.md) |
| Create a recoverable change stream | [Streams](streams/README.md) |
| Synchronize another system | [Sync](sync/README.md) |
| Push authorized updates to clients | [Live](live/README.md) |
| Query or maintain graph data | [Graph](graph/README.md) and [Continuous Graph](continuous-graph/README.md) |
| Deploy and operate BlueTusk | [Deployment](operations/deployment.md), [observability](operations/observability.md), and [troubleshooting](operations/troubleshooting.md) |
| Upgrade safely | [Upgrade guide](operations/upgrade-guide.md) |
| Contribute to the repository | [Repository layout](contributing/repository-layout.md) and [testing](contributing/testing.md) |

## Product guides

### Data access

- [ADO.NET provider](ado-net/README.md)
- [PostgreSQL type system](types/README.md)
- [Pipeline mode](pipeline-mode.md)
- [Replication](replication/README.md)
- [ADO.NET compatibility](ado-net/compatibility.md)

### EF Core and PostgreSQL features

- [EF Core provider](ef-core/README.md)
- [EF Core specification coverage](ef-core/specification-tests.md)
- [Extension SDK and catalogue](extensions/README.md)
- [PostgreSQL 19 SQL/PGQ](graph/README.md)

### Real-time platform

Use the products in this order only when the workload requires them:

```text
PostgreSQL → Streams → Sync / Live / Continuous Graph
                         ↑
                    Control Plane
```

- [Platform overview](realtime-platform/README.md)
- [Delivery contracts](realtime-platform/contracts.md)
- [Streams](streams/README.md)
- [Sync](sync/README.md)
- [Live](live/README.md)
- [Control Plane](control-plane/README.md)
- [Continuous Graph](continuous-graph/README.md)
- [Real-time operations](realtime-platform/operations.md)

## Operations

Start with these documents when preparing or running a deployment:

- [Production checklist](operations/production-checklist.md)
- [Deployment](operations/deployment.md)
- [Observability](operations/observability.md)
- [Troubleshooting](operations/troubleshooting.md)
- [Performance](operations/performance.md)
- [Upgrade guide](operations/upgrade-guide.md)
- [Security model](security.md)

The release evidence, endurance plans, approval records, and historical release
notes elsewhere in this directory are project records. They support review and
reproducibility, but they are not required reading for normal application
development.

## Architecture and maintenance

- [Architecture overview](architecture/overview.md)
- [Architecture decisions](architecture/decisions/)
- [Allocation discipline](architecture/allocation-discipline.md)
- [API compatibility](api-compatibility.md)
- [Release process](release-process.md)
- [1.1.0-rc.1 release record](releases/1.1.0-rc.1.md)

## Documentation conventions

- Examples use parameterized SQL and explicit resource ownership.
- Product availability, test evidence, and production approval are stated
  separately.
- Version-sensitive claims link to the support matrix or a release record.
- Repository paths and commands are written from the repository root unless a
  guide says otherwise.
- Public website guides are curated from these Markdown sources; project
  records remain available in GitHub without crowding the task-oriented index.

If documentation and behavior disagree, open an issue with the affected
version, PostgreSQL version, smallest reproducer, and the guide URL.
