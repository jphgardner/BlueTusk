# Architecture

BlueTusk is split by responsibility so protocol correctness can be tested without ADO.NET or EF Core and so higher layers cannot leak their concepts downward.

```text
Application
    ├──→ BlueTusk.EntityFrameworkCore ─→ BlueTusk.Data ─┐
    ├──→ BlueTusk.Data ─────────────────────────────────┤
    └──→ BlueTusk.Replication.PgOutput                  │
                 ↓                                     │
          BlueTusk.Replication ────────────────────────┤
                                                      ↓
                                              BlueTusk.Client
    ├──────────────→ BlueTusk.TypeSystem
    ↓
BlueTusk.Protocol ─→ BlueTusk.Security
    ↓
BlueTusk.Transport
    ↓
PostgreSQL
```

## Layer rules

- **Transport** moves bytes. It does not know SQL, PostgreSQL types, authentication mechanisms, or ADO.NET.
- **Protocol** owns frontend/backend framing, connection and operation state machines, authentication negotiation, and cancellation protocol messages. It does not expose ADO.NET types.
- **Security** owns authentication primitives, secret handling, certificate policy, and diagnostic redaction.
- **TypeSystem** owns catalogue type identities and codecs. Unknown types are values, not fatal errors.
- **Client** coordinates sessions and PostgreSQL-native operations using protocol and type-system abstractions.
- **Data** exposes `System.Data.Common` APIs, pooling, connection strings, and data sources.
- **Replication** consumes `COPY BOTH` without introducing ADO.NET concepts into the protocol engine.
- **Replication.PgOutput** statefully decodes standard logical-replication messages while preserving their WAL envelopes.
- **EntityFrameworkCore** is the only layer allowed to depend on EF Core.
- **Extensions.Abstractions** is the public, stable plug-in seam. Extension packages must not access protocol internals.

Dependencies should form a directed acyclic graph. A feature that would create a reverse reference needs a new abstraction in the lower-level package or an orchestration implementation in a higher-level package.

An executable architecture conformance test reads each built assembly's direct references and rejects reverse BlueTusk dependencies, EF references outside the EF packages, and `System.Data.Common` leakage below Data.

## EF provider boundary

Entity Framework Core consumes a deliberately narrow Data-layer contract:

- data-source-backed logical connection creation without taking ownership of the data source;
- parameter store-type identity by stable built-in OID or schema-qualified runtime type name;
- runtime catalogue/type resolution and configured codecs owned by the data source;
- connection-scoped server capabilities; and
- diagnostics exposed through provider-level abstractions.

EF must not reference Client, Protocol, Transport, or wire codecs directly. New EF features that need wire behavior extend the Data contract or a lower neutral abstraction first.

## Ownership and buffers

Protocol parsers return views over caller-owned buffers. The view is valid only until the caller advances or releases that buffer. Public ADO.NET values must either own their memory or have an explicitly documented reader lifetime.

Wire lengths are validated before allocation. A configured upper bound is mandatory for any server-controlled length that can cause memory growth.

## Sync and async

Asynchronous APIs use actual asynchronous socket operations. Synchronous APIs will use dedicated synchronous paths; they will not call asynchronous methods and block on the result.

PostgreSQL pipeline mode is a protocol scheduling feature with multiple extended-query operations between explicit synchronization boundaries. `System.IO.Pipelines` is a .NET buffering API. They are independent architecture decisions: BlueTusk can implement PostgreSQL pipeline mode on its existing transport, and it will not adopt `System.IO.Pipelines` without representative sync/async benchmark evidence.
