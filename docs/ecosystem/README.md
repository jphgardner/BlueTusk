# Choose a data service

BlueTusk’s data services use PostgreSQL for application state. Each service has
its own storage or execution contract, so start with the one that matches the
workload. Check the package version and availability in your feed before
adding it to an application.

## Store and project data

| Need | Product | Main integration |
| --- | --- | --- |
| Store JSON documents and stream large attachments | [Documents](../documents/README.md) | `BlueTusk.Documents` with optional ASP.NET Core, Streams, or Live integration |
| Append business events in the same transaction as a write | [Events](../events/README.md) | `BlueTusk.Events` with optional EF Core and Streams integration |
| Build a rebuildable read model from events | [Projections](../projections/README.md) | `BlueTusk.Projections` with durable checkpoints and optional Live delivery |
| Index content and run a search query | [Search](../search/README.md) | `BlueTusk.Search` with a PostgreSQL, vector, or external search integration |

## Execute durable work

Use [Jobs](../jobs/README.md) for independent work items with leases, retries,
and bounded concurrency. Use [Workflows](../workflows/README.md) to coordinate
several durable steps, waits, and signals. Configure handler identities,
idempotency, lease expiry, and recovery before running multiple workers.

## Manage schema and query access

- [Schema](../schema/README.md) compares catalogue state and helps prepare reviewed database changes.
- [SQL](../sql/README.md) defines typed SQL catalogs with declared parameters and result shapes.
- [Studio](../studio/README.md) exposes registered query capabilities with explicit authorization.
- [Edge](../edge/README.md) synchronizes a scoped local cache and queued changes with an authorized server.

## Provision and verify

Initialize each product’s database schema through the controlled deployment
path documented in its guide. Give application roles the permissions they need
to use the schema. Run a write, a read, and a restart against an isolated
database before connecting shared data. Then follow the
[production checklist](../operations/production-checklist.md) for backup,
recovery, resource limits, and telemetry.
