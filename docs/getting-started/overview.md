# What is BlueTusk?

BlueTusk is a PostgreSQL platform for .NET 10. It gives .NET applications a
native PostgreSQL driver and EF Core provider, then adds products that react to
committed database changes: change streams, destination sync, live queries for
browsers, and an operations dashboard.

BlueTusk talks to PostgreSQL directly over the PostgreSQL wire protocol. It does
not wrap or depend on Npgsql at run time.

## The product families

BlueTusk is a set of product families. Install only the families you need.

| Family | What it does | Main package | Status |
| --- | --- | --- | --- |
| [Provider: ADO.NET](../ado-net/README.md) | Connections, commands, transactions, COPY, notifications and replication | `BlueTusk.Data` | Core |
| [Provider: EF Core](../ef-core/README.md) | LINQ, change tracking, migrations and scaffolding | `BlueTusk.EntityFrameworkCore` | Core |
| [Streams](../streams/README.md) | Turns committed PostgreSQL changes into ordered, acknowledged transactions (change data capture) | `BlueTusk.Streams` | Core |
| [Sync](../sync/README.md) | Applies those transactions to PostgreSQL, Redis, NATS, OpenSearch, Kafka, S3 or a webhook | `BlueTusk.Sync` | Core |
| [Live](../live/README.md) | Pushes the result of an authorized query to browsers and .NET clients as it changes | `BlueTusk.Live` and `@bluetusk/live` | Core |
| [Control Plane](../control-plane/README.md) | A dashboard and API to inspect and operate the other products | `BlueTusk.ControlPlane` | Core |
| [Graph](../graph/README.md) and [Continuous Graph](../continuous-graph/README.md) | SQL/PGQ property-graph queries and incrementally maintained graph results | `BlueTusk.Data`, `BlueTusk.ContinuousGraph` | Preview, not part of 1.1.0 |
| [Ecosystem](../ecosystem/release-qualification.md) | Events, Jobs, Workflows, Documents, Schema, Projections, Search, Sql, Studio and Edge | Several | Preview, not published |

The provider also has optional packages for
[PostgreSQL extensions](../extensions/README.md) such as PostGIS, pgvector and
TimescaleDB, and for [cloud identity](../ado-net/cloud-identity.md) on AWS, Azure
and Google Cloud.

## How the families fit together

```text
                ┌──────────────────────────────────────────────┐
 Your .NET app ─┤ Provider: BlueTusk.Data / EntityFrameworkCore├─► PostgreSQL
                └──────────────────────────────────────────────┘        │
                                                                         │ logical replication
                                                                         ▼
                                                                     Streams
                                                     ┌───────────────┼────────────────┐
                                                     ▼               ▼                ▼
                                                   Sync            Live      Continuous Graph (preview)
                                              (other systems)   (browsers)

                                Control Plane observes and operates all of them.
```

- The **provider** is the foundation. Everything else uses it.
- **Streams** is the only component that reads PostgreSQL's replication
  protocol. Sync, Live and Continuous Graph consume Streams.
- You can use the provider on its own. You only need Streams when you want to
  react to committed changes.

## Choose where to start

| I want to… | Start with |
| --- | --- |
| Run SQL from a .NET application | [5-minute first app](quickstart.md), then the [ADO.NET quick start](../ado-net/quickstart.md) |
| Use EF Core with PostgreSQL | [EF Core quick start](../ef-core/quickstart.md) |
| Process every committed change in .NET code | [Streams quick start](../streams/quickstart.md) |
| Keep a cache, search index, broker or another database current | [Sync quick start](../sync/quickstart.md) |
| Push live query results to a web page | [Live quick start](../live/quickstart.md) |
| See the health of a real-time deployment | [Control Plane quick start](../control-plane/quickstart.md) |

## Release status

| Version | Status |
| --- | --- |
| `1.0.0` | Current stable release, published on 2026-08-23. |
| `1.1.0-rc.1` | Public release candidate, published on 2026-08-29. |
| `1.1.0` | Next release, in qualification. It has not been published yet. |

The Core families (Provider, Streams, Sync, Live and Control Plane) share one
version. When `1.1.0` is published, it supersedes `1.0.0` and `1.1.0-rc.1`,
which will be deprecated.

Graph and Continuous Graph are not part of the `1.1.0` release. They need a
PostgreSQL server that provides SQL/PGQ, and PostgreSQL removed SQL/PGQ in
PostgreSQL 19 Beta 4. They stay in preview until a PostgreSQL release ships it.

The ecosystem families are `0.1.0-preview.1` and are not published to a
package feed. Each one is released when its own checks pass.

See [Install BlueTusk](install.md#choose-a-version) to choose a version.

### What the status labels mean

- **Core**: on the stable release track. Published as `1.0.0` and
  `1.1.0-rc.1`; `1.1.0` is next.
- **Preview**: you can evaluate it, but it is not production-qualified and its
  API or behavior may change.
- **Not published**: build the packages from source to evaluate them.

A published package is not the same as a production qualification. The
[evidence and qualification records](../v1-release-readiness.md) show what has
been measured and what is still open.

## Next steps

1. [Install BlueTusk](install.md).
2. Build the [5-minute first app](quickstart.md).
3. Read the [core concepts](concepts.md) that every product uses.
