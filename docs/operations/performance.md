# Performance engineering

BlueTusk performance work is evidence-driven. A benchmark result describes one
named workload, machine, runtime, PostgreSQL image and source commit; it is not
a universal claim about every application.

## Start with the workload

Record:

- query and result shape;
- concurrency and request rate;
- connection/pool topology;
- transaction and session-affinity requirements;
- network distance;
- PostgreSQL plan and server time;
- payload sizes;
- latency percentiles; and
- allocation/GC behavior.

Optimizing a loopback scalar query does not prove improvement for a remote
analytical workload.

## Data-source and pool sizing

Create long-lived data sources and size the pool against database capacity.
More connections can reduce queueing until PostgreSQL begins spending more time
on process, memory, lock and cache contention.

Measure:

- pool wait duration;
- active versus idle sessions;
- connection creation rate;
- command latency excluding and including pool wait; and
- PostgreSQL backend saturation.

## Command preparation and reuse

Parameterize SQL. Reuse stable command/query shapes when the application
lifecycle permits it. Preparation can reduce repeated parse/plan overhead, but
session-level prepared state affects PgBouncer and multiplexing compatibility.

Compare cold and warm behavior separately.

## Multiplexing

Bounded statement multiplexing can reduce physical-session demand for
independent, non-session-affine commands. It is not appropriate for:

- explicit transactions;
- COPY;
- replication;
- notifications;
- temporary objects;
- session settings or advisory locks; and
- commands whose correctness depends on one server session.

Measure fairness, queueing and tail latency, not only mean throughput.

## Readers and large values

Default readers may buffer result data for convenient random access.
`SequentialAccess` uses the incremental portal reader for bounded processing of
large values. Choose it when payload size makes buffering undesirable, and
follow its forward-only access rules.

For bulk movement, prefer binary COPY over per-row commands when the workload
fits COPY’s contract.

## Type codecs and allocations

BlueTusk’s allocation budgets cover representative command, protocol, COPY,
replication, EF and graph paths. The implementation uses spans, memory,
ArrayPool ownership and bounded protocol windows where measurements justify
them.

Do not retain pooled buffers after their owner advances or disposes. Do not
replace a clear bounded allocation with pooling without measuring lifetime,
contention and retained-memory cost.

## Real-time throughput

For Streams and Sync, observe:

- decoded transactions per second;
- transaction size and spool behavior;
- acknowledgement/checkpoint latency;
- relay append/read latency;
- destination batch size;
- reconciliation and quarantine rate; and
- WAL lag and retained bytes.

Large transactions are correctness events as well as performance events.
Bounded spooling prevents untrusted or unusually large transactions from
turning into unbounded memory growth.

## Benchmark discipline

1. Build Release binaries.
2. Pin source commit, runtime and database/container identity.
3. Warm up the measured path.
4. Separate setup from the operation under test.
5. Report mean and tail latency plus allocation.
6. Preserve full machine-readable output and its hash.
7. Compare against the previous BlueTusk baseline and a relevant external
   implementation where useful.
8. Reject results with environmental instability or changed workloads.

The multiplexing record follows this discipline and is explained in
[multiplexing compatibility](../ado-net/multiplexing-compatibility.md).
The complete direct-provider result is published in the
[BlueTusk versus Npgsql V1 performance report](npgsql-performance-comparison.md).

## Production investigation

Correlate client and server evidence:

- OpenTelemetry command spans;
- pool and transport metrics;
- PostgreSQL `pg_stat_activity`;
- `pg_stat_statements`;
- query plans;
- lock waits;
- checkpoint/WAL metrics; and
- host CPU, memory, network and storage.

Avoid logging parameter values simply to diagnose performance. Prefer query
shape identifiers, operation names and redacted structured dimensions.

## Performance gates

Release gates include:

- zero-warning optimized build;
- allocation-budget verification;
- provider NativeAOT/trimming measurements;
- commit-bound multiplexing evidence;
- full tests against selected PostgreSQL versions; and
- exact-duration Streams/Sync endurance reports.

See [allocation discipline](../architecture/allocation-discipline.md) and the
[release process](../release-process.md).
