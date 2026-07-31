# ADR 0005: Separate PostgreSQL pipeline mode from transport pipelines

- Status: Accepted evaluation plan; transport decision pending measurements
- Date: 2026-07-31

## Context

PostgreSQL pipeline mode batches extended-query operations between explicit `Sync` boundaries and returns ordered result groups without waiting after every operation. `System.IO.Pipelines` is a .NET buffering and asynchronous I/O abstraction. A similarly named API does not implement PostgreSQL's protocol semantics, and adopting it would affect BlueTusk's genuine synchronous transport promise.

## Decision

Implement PostgreSQL pipeline mode in the Client layer over the existing protocol and transport abstractions. Its public contract must define operation ordering, explicit synchronization boundaries, aborted-group errors, cancellation, early disposal, and recovery to `ReadyForQuery` before a session can be reused. Capability flags alone do not constitute support.

Keep the current ArrayPool/Span/Memory transport while the pipeline API is developed. Evaluate a bounded `System.IO.Pipelines` prototype separately; do not mechanically replace the transport.

## Measurement gate

The comparison must use checked-in, reproducible benchmarks for:

- backend frames fragmented at representative and adversarial boundaries;
- many small rows and large fields;
- COPY streaming and cancellation recovery;
- plain TCP and TLS;
- synchronous and asynchronous commands; and
- throughput, tail latency, allocated bytes, retained buffers, and implementation complexity.

Adoption requires a meaningful measured benefit without regressing synchronous behavior, bounded-memory guarantees, cancellation safety, or protocol-test clarity. Otherwise the current transport remains the accepted implementation.

## Consequences

PostgreSQL pipeline mode ships independently of the transport evaluation. Its Client-layer implementation has fake-server, conformance, stress, and live PostgreSQL coverage for explicit synchronization boundaries, ordered group errors, cancellation draining, and safe session reuse. Documentation and capability detection describe pipeline semantics separately from transport buffering. The transport benchmark and adoption gates remain unchecked until reproducible results exist.
