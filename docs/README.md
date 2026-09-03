# BlueTusk documentation

Start with the outcome you need. You do not need to read the whole library or
adopt every BlueTusk product.

## New to BlueTusk?

Follow these four guides once, in order:

1. [Choose and install packages](getting-started/install.md).
2. [Run your first query](getting-started/quickstart.md).
3. [Learn the core concepts](getting-started/concepts.md).
4. [Prepare for production](operations/production-checklist.md).

The [support matrix](../VERSIONING.md) is the authority for supported .NET,
EF Core, PostgreSQL, and package versions.

## Choose your goal

Each row is a short reading path. Start at the left and stop when you have the
information you need.

| I want to…                        | Start                                                      | Build                                          | Operate                                                  |
| --------------------------------- | ---------------------------------------------------------- | ---------------------------------------------- | -------------------------------------------------------- |
| Connect a .NET application        | [Install](getting-started/install.md)                      | [First query](getting-started/quickstart.md)   | [Provider choices](ado-net/README.md)                    |
| Use EF Core                       | [Provider choices](ado-net/README.md)                      | [EF Core guide](ef-core/README.md)             | [Deployment](operations/deployment.md)                   |
| Stream committed database changes | [Real-time overview](realtime-platform/README.md)          | [Streams](streams/README.md)                   | [Snapshot and catch-up](streams/snapshot-bootstrap.md)   |
| Keep another system in sync       | [Delivery guarantees](realtime-platform/contracts.md)      | [Sync](sync/README.md)                         | [Recovery and rebuilds](realtime-platform/operations.md) |
| Push live updates to users        | [Live](live/README.md)                                     | [Security](security.md)                        | [Observability](operations/observability.md)             |
| Query connected data              | [SQL/PGQ](graph/README.md)                                 | [Continuous Graph](continuous-graph/README.md) | [Real-time operations](realtime-platform/operations.md)  |
| Take a service to production      | [Production checklist](operations/production-checklist.md) | [Deployment](operations/deployment.md)         | [Troubleshooting](operations/troubleshooting.md)         |

## How the library is organized

### 1. Learn the essentials

Use the [installation guide](getting-started/install.md),
[quickstart](getting-started/quickstart.md), and
[core concepts](getting-started/concepts.md) for the first query, architecture,
and concepts shared by the rest of the platform.

### 2. Build with .NET

- [ADO.NET provider](ado-net/README.md) — connections, commands, transactions,
  pooling, COPY, authentication, routing, types, and notifications.
- [EF Core provider](ef-core/README.md) — LINQ, migrations, scaffolding, and
  provider-specific behavior.
- [PostgreSQL extensions](extensions/README.md) — PostGIS, pgvector,
  TimescaleDB, and the extension SDK.
- [Replication](replication/README.md) — low-level PostgreSQL replication
  protocols and decoding.

### 3. Build real-time systems

The products compose, but they solve different problems:

```text
PostgreSQL changes
       │
       ▼
    Streams ─────► Sync ───────────► another data system
       │
       ├─────────► Live ───────────► connected application clients
       │
       └─────────► Continuous Graph ► maintained graph query results

Control Plane observes and manages these running components.
```

- [Platform overview](realtime-platform/README.md) explains which product to
  choose.
- [Delivery guarantees](realtime-platform/contracts.md) defines checkpoints,
  acknowledgement, retries, and duplicate handling.
- [Streams](streams/README.md), [Sync](sync/README.md), [Live](live/README.md),
  [Control Plane](control-plane/README.md), and
  [Continuous Graph](continuous-graph/README.md) contain the product guides.

### 4. Run in production

Start with the [production checklist](operations/production-checklist.md), then
use the focused guides for:

- [Deployment](operations/deployment.md)
- [Security](security.md)
- [Observability](operations/observability.md)
- [Performance and capacity](operations/performance.md)
- [Troubleshooting](operations/troubleshooting.md)
- [Upgrades and rollback](operations/upgrade-guide.md)

### 5. Engineering reference

Architecture decisions, API compatibility records, test evidence, endurance
plans, approvals, and release records are maintained for reviewers and
incident investigations. They are searchable on the documentation website,
but deliberately separated from the normal learning paths.

- [Architecture overview](architecture/overview.md)
- [Architecture decisions](architecture/decisions/)
- [Allocation discipline](architecture/allocation-discipline.md)
- [API compatibility](api-compatibility.md)
- [Repository layout](contributing/repository-layout.md)
- [Testing](contributing/testing.md)
- [Release process](release-process.md)

## Reading conventions

- Commands and paths are written from the repository root unless a guide says
  otherwise.
- Examples use parameterized SQL and explicit resource ownership.
- Product availability, test evidence, and production approval are separate
  claims.
- Version-sensitive behavior links to the support matrix or a release record.
- Guarantees describe the actual durability boundary and duplicate behavior;
  they do not rely on an “exactly once” slogan.

The public documentation is generated from these Markdown files. If a guide
and the software disagree, report the affected package version, PostgreSQL
version, smallest reproducer, and guide URL.
