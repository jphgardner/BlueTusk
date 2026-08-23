# Core concepts

BlueTusk exposes several PostgreSQL data paths, but they are not interchangeable.
This guide defines the vocabulary used throughout the provider, EF Core and
real-time documentation.

## Data source, logical connection and physical session

A `BlueTuskDataSource` is the long-lived owner of configuration, type metadata
and the physical connection pool. A `BlueTuskConnection` is a logical ADO.NET
connection. Opening it leases or creates a physical PostgreSQL session;
disposing it returns a healthy session or destroys an unhealthy one.

This distinction matters because PostgreSQL session state is real:

- temporary tables;
- prepared statements;
- `SET` values;
- advisory locks;
- active transactions;
- `LISTEN` registrations; and
- replication or COPY modes.

Pool reset and multiplexing rules exist to prevent state from leaking between
unrelated logical connections.

## Commands, protocol groups and cancellation

A command becomes one or more PostgreSQL frontend messages. Extended-query
execution uses Parse, Bind, Describe and Execute messages terminated by an
appropriate Sync boundary. Pipeline and bounded-multiplexing modes can share a
physical session only when the command has no session-affine behavior.

Cancellation is out-of-band: PostgreSQL uses a separate cancellation request
identified by the backend process ID and secret key. Cancellation therefore
has different timing from closing a socket, and a canceled command must still
leave the protocol stream in a known state before a session can be reused.

## Type identity

PostgreSQL types are identified by server catalogue OIDs, not only by SQL type
names or CLR types. BlueTusk builds an immutable type-registry snapshot from
the authenticated server’s catalogue and composes optional extension
descriptors into it.

The same CLR shape can require different PostgreSQL identities. For example,
`string` may be `text`, `varchar`, `citext`, `json` or a domain. Specify
`DbType`, `PostgreSqlTypeOid` or `PostgreSqlTypeName` when inference would be
ambiguous.

## Capabilities

A major-version number is not sufficient proof that an optional feature exists.
BlueTusk uses authenticated capability discovery for features such as
extensions and PostgreSQL 19 SQL/PGQ. Capability-sensitive APIs either remain
unavailable or fail explicitly when the server does not provide the required
catalogue or grammar.

## Transactions and committed changes

An ADO.NET or EF transaction is application-owned database work. A Streams
transaction is a decoded, already committed WAL transaction. The latter cannot
be rolled back by its consumer.

Streams delivers a transaction with an acknowledgement operation. The consumer
must apply its side effect before acknowledging. A durable checkpoint records
progress only after the relevant delivery contract permits it.

BlueTusk does not label this arrangement “exactly once.” End-to-end effects
depend on the destination’s idempotency, atomicity and reconciliation behavior.

## Source identity and schema identity

A real-time source is identified by more than a connection string. Source
identity includes the PostgreSQL system identity and replication configuration
needed to prevent a checkpoint from being applied to a different cluster or
publication.

Typed Streams and Sync mappings also carry schema and mapping fingerprints.
Those fingerprints detect incompatible relation or CLR-binding changes before
an old checkpoint is reused.

## Snapshot then stream

Starting from “now” can miss existing rows; taking a snapshot and then starting
replication can miss changes committed between those operations. BlueTusk’s
snapshot bootstrap records a WAL fence, reads a consistent snapshot, persists
snapshot progress and then begins streaming from the fence.

The protocol is intentionally explicit about:

- the exported snapshot;
- the WAL position;
- restart behavior;
- checkpoint ownership; and
- failure before and after the handoff.

## Relay, destination and live delivery

A durable relay stores acknowledged source transactions for independent
consumer groups. It provides bounded retention and replay; it is not a general
message broker.

Sync transforms a source transaction into a versioned destination write. Each
connector defines its atomicity, idempotency, quarantine, reconciliation and
rebuild behavior.

Live turns authoritative server-side queries into authorized client
subscriptions. It re-evaluates access and result state rather than treating raw
CDC payloads as safe client messages.

## Product maturity versus release authorization

BlueTusk tracks three separate ideas:

1. **Implementation state** — the code and focused tests exist.
2. **Engineering evidence** — repeatable builds, matrices, budgets and
   candidate artifacts pass.
3. **Release authorization** — exact-candidate endurance, GA prerequisites,
   independent review and maintainer sign-off are complete.

The V1 implementation was published as `1.0.0` under a documented owner
exception before the third category was complete. See the
[publication record](../releases/1.0.0-publication-record.md). Later releases
remain subject to the normal fail-closed authorization policy.

## Where to continue

- [Architecture overview](../architecture/overview.md)
- [ADO.NET compatibility](../ado-net/compatibility.md)
- [Type system](../types/README.md)
- [Real-time contracts](../realtime-platform/contracts.md)
- [Observability](../observability.md)
- [V1 release readiness](../v1-release-readiness.md)
