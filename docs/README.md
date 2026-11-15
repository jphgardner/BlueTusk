# BlueTusk documentation

These guides cover BlueTusk 1.2. BlueTusk connects .NET applications to PostgreSQL through ADO.NET and EF Core.
Its other packages handle committed changes, live query results, connected
data, and application workloads that need durable state.

## Start here

1. [Choose packages](getting-started/install.md) and check [runtime compatibility](getting-started/runtime-compatibility.md).
2. [Run your first query](getting-started/quickstart.md) against a disposable database.
3. [Learn the ownership model](getting-started/concepts.md) before configuring pooling, replication, or replay.
4. [Explore working examples](getting-started/examples.md) when you need a complete application flow.

## Connect and query

| If you need to | Read |
| --- | --- |
| Execute SQL, manage transactions, or stream large results | [ADO.NET provider](ado-net/README.md) |
| Configure dependency injection, credentials, and pooling | [Dependency injection](ado-net/dependency-injection.md), [authentication](ado-net/authentication.md), [pooling](ado-net/pooling.md) |
| Model entities or scaffold an existing PostgreSQL schema | [EF Core](ef-core/README.md) |
| Work with PostgreSQL types and extension codecs | [Types](types/README.md), [extensions](extensions/README.md) |
| Bulk-load rows or inspect low-level replication | [COPY](ado-net/copy.md), [replication](replication/README.md) |

## React to changes and query connected data

| If you need to | Read |
| --- | --- |
| Consume committed transactions | [Streams](streams/README.md) |
| Deliver changes to another store or broker | [Sync](sync/README.md) |
| Push authorized query results to a browser or app | [Live](live/README.md) |
| Query a graph over existing tables | [Property graphs](graph/README.md), [bounded traversals](graph/traversals.md) |
| Maintain graph results as data changes | [Continuous Graph](continuous-graph/README.md) |
| Manage instances, replication slots, and registered pipelines | [Control Plane](control-plane/README.md) |

## Add a data service

Use the [data services guide](ecosystem/README.md) to choose among Documents,
Events, Projections, Jobs, Workflows, Schema, SQL, Search, Edge, and Studio.
Each product guide describes the package, the database objects it owns, and the
application responsibilities that affect recovery.

## Operate your application

Start with the [production checklist](operations/production-checklist.md).
Then configure [deployment](operations/deployment.md),
[security](security.md), and [observability](operations/observability.md).
Use [troubleshooting](operations/troubleshooting.md) for failures and the
[upgrade guide](operations/upgrade-guide.md) before changing dependencies or
database schema.
