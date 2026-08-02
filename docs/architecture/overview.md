# Architecture

BlueTusk is split by responsibility so protocol correctness can be tested without ADO.NET or EF Core and so higher layers cannot leak their concepts downward.

```text
Application
    ├──→ BlueTusk.EntityFrameworkCore ─→ BlueTusk.Data ─┐
    ├──→ BlueTusk.Identity.* ──────────→ BlueTusk.Data ─┤
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

- **Transport** moves bytes. It owns endpoint resolution, ordered address attempts, socket/TLS
  I/O, connect deadlines, cancellation, and stable connection-failure classification, but does
  not know SQL, PostgreSQL types, authentication mechanisms, or ADO.NET. See the
  [transport contract](transport.md).
- **Protocol** owns frontend/backend framing, connection and operation state machines, authentication negotiation, and cancellation protocol messages. It does not expose ADO.NET types.
- **Security** owns authentication primitives, secret handling, certificate policy, and diagnostic redaction.
- **Diagnostics** owns dependency-free `ActivitySource`, `Meter`, and redacted
  event contracts. Client, Data, and Replication may publish through it; it has
  no references back into provider layers.
- **TypeSystem** owns catalogue type identities and codecs. Unknown types are values, not fatal errors.
- **Client** coordinates sessions and PostgreSQL-native operations using protocol and type-system abstractions.
- **Data** exposes `System.Data.Common` APIs, pooling, connection strings, and data sources.
- **Identity packages** adapt vendor credential SDKs to Data's access-token callback.
  Vendor dependencies do not enter Data, Client, Security, or lower layers.
- **Replication** consumes `COPY BOTH` without introducing ADO.NET concepts into the protocol engine.
- **Replication.PgOutput** statefully decodes standard logical-replication messages while preserving their WAL envelopes.
- **EntityFrameworkCore** is the only layer allowed to depend on EF Core.
- **Extensions.Abstractions** is the public preview plug-in seam. Built data sources carry immutable type and feature snapshots; extension packages must not access protocol internals.
- **Extensions.Testing** is an ADO.NET-level test utility that validates feature and runtime-codec contracts; production packages never depend on it.

Dependencies should form a directed acyclic graph. A feature that would create a reverse reference needs a new abstraction in the lower-level package or an orchestration implementation in a higher-level package.

An executable architecture conformance test reads each built assembly's direct references and rejects reverse BlueTusk dependencies, EF references outside the EF packages, and `System.Data.Common` leakage below Data.

Protocol's operation state machine validates legal wire-state transitions and owns
message framing. Client owns the operation-specific loops for queries, COPY,
notifications, and replication because those loops coordinate type decoding and
session behavior. Keeping that orchestration above Protocol avoids importing
higher-level operation semantics into the framing layer.

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

Authentication messages use a sensitive protocol-write path that overwrites reusable framing
storage after the transport flushes it. Security-layer password derivations clear temporary
writable byte buffers; immutable caller-supplied .NET strings remain owned by the caller and
cannot be zeroed by the provider.

Credential callbacks and TLS client identity are immutable data-source configuration, not
connection-string state. Data sources propagate that configuration to pools, unpooled paths,
notification listeners, EF admin-database connections, and dedicated replication-option
snapshots. Credential resolution is lazy and belongs to Client authentication; password-file
parsing, SCRAM/MD5 derivation, RFC 7628 OAUTHBEARER response construction, and the
operating-system-backed GSSAPI/SSPI security context remain in Security, while Transport only
receives the final TLS certificate policy. Client owns the PostgreSQL code 7/8/9 negotiation
sequence and clears each opaque GSS token after the sensitive write path flushes it.
Cloud identity packages configure those public callbacks and a TLS-before-token policy;
they do not add provider-specific branches to the authentication state machine.

## Sync and async

Asynchronous APIs use actual asynchronous socket operations. Synchronous APIs will use dedicated synchronous paths; they will not call asynchronous methods and block on the result.

PostgreSQL pipeline mode is a protocol scheduling feature with multiple extended-query operations between explicit synchronization boundaries. `System.IO.Pipelines` is a .NET buffering API. They are independent architecture decisions: BlueTusk implements PostgreSQL pipeline mode on its existing transport. Representative sync/async, fragmented-frame, large-field, COPY, cancellation, and TLS benchmarks did not justify a production transport rewrite, so the benchmark-only prototype remains isolated from this dependency graph. See [ADR 0005](decisions/0005-postgresql-pipeline-mode-and-transport-pipelines.md).
