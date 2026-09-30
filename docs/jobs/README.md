# BlueTusk.Jobs

BlueTusk.Jobs is a PostgreSQL durable execution library built on BlueTusk.Data.
The current package version is `0.1.0-preview.1`, targeting .NET 10. It is an
implemented preview; production qualification and performance leadership are
not established by the current test results.

## Usage

```csharp
await using var source = BlueTuskDataSource.Create(connectionString);
var store = new PostgreSqlJobStore(source);
await store.InitializeAsync(cancellationToken); // Deployment step, before workers.
var scope = new JobScope("customer-42", "fulfilment");

// OrderJsonContext is a JsonSerializerContext generated in the application.
var request = JobRequest.FromJson(scope, "ship-order.v1", order,
    OrderJsonContext.Default.ShipOrder) with
{
    DeduplicationKey = "ship:" + order.Id,
    MaximumAttempts = 8,
};

await using var connection = await source.OpenConnectionAsync(cancellationToken);
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
// Save the business row on this same connection and transaction.
Guid jobId = await store.EnqueueAsync(request, transaction, cancellationToken);
await transaction.CommitAsync(cancellationToken);

var handlers = new JobHandlerRegistry().Register("ship-order.v1",
    OrderJsonContext.Default.ShipOrder, async (payload, context, token) =>
    {
        // Send context.JobId as an external idempotency identity.
        await shippingService.ShipAsync(payload, context.JobId, token);
    });
var worker = new JobWorker(store, scope, "host-1/worker-1", handlers);
await worker.RunAsync(stoppingToken);
```

The caller owns the data source. Store methods borrow and return pooled
connections. Enqueue in a caller transaction borrows no second connection and
never commits that transaction. A duplicate key returns the original identity
only when type, bytes, and maximum-attempt contract match. A changed contract
fails explicitly. The first enqueue establishes the availability deadline;
repeated enqueue does not reschedule it. Keys are scoped by tenant and queue.

## Delivery and ownership

Jobs are at least once. A handler can perform an external effect, lose its
connection before acknowledging it, and execute again after recovery. External
systems must enforce idempotency, or enforce a fence keyed by job identity.
PostgreSQL acknowledgement fencing cannot undo an already performed external
effect. A business effect performed in PostgreSQL should use its own atomic
inbox/effect transaction.

ExecuteFencedAsync guards colocated database effects and downstream enqueue.
Its callback receives a borrowed BlueTusk connection and transaction, checks the
database deadline before entering and again before commit, and rolls everything
back on cancellation, failure or expiry. The typed handler context includes its
lease capability. Keep callbacks short: their locked job row prevents heartbeat
extension through the transaction. External effects cannot be rolled back and
do not belong in that callback.

Every claim increments a durable monotonic token. Heartbeat, success and failure
require the tenant, queue, job identity, owner and token to match, and the lease
deadline to remain in the future on the database clock. An expired owner cannot
revive its lease even before another worker claims it. Cancellation immediately
revokes ownership and increments the fence. Cooperative handlers receive
cancellation on the next heartbeat; handlers that ignore cancellation can delay
worker shutdown. RunAsync observes and awaits every handler and heartbeat task.

Expired running jobs are reclaimed by ordinary polling. If a worker crashes on
its final permitted attempt, a later claim marks the job failed without running
it again. `lease_expired` is retained as the stable failure code. Handler
exceptions retain only `handler_failed`, a stable classified code, or
`invalid_payload`; messages, stack traces, credentials and payloads are omitted
from history and telemetry.

Workers claim only types in their immutable registration snapshot. This allows
old and new versions to coexist without stealing unsupported job contracts.
Version type identities explicitly (for example, `ship-order.v2`), retain the
old handler while its jobs remain, and delete an old contract only after the
backlog is drained. Register generated JsonTypeInfo metadata for typed handlers.

## Bounds and scheduling

The defaults allow 1 MiB per payload, at most 128 rows and 8 MiB of payload per
claim, 16 MiB of in-flight payload per worker, 32 retained attempts per job,
eight active handlers, and a 30-second lease with five-second
heartbeats. Configured limits are validated; schema payload limits are also
database CHECK constraints. Payload/history limits are stored with schema
format 1 and incompatible stores are rejected at initialization. Store command
timeouts are bounded from one to 300 seconds. A lease must be comfortably longer
than expected connection acquisition, command latency and scheduler stalls.

Claims use separate indexed pending and expired paths, each bounded by the
claim batch, with SKIP LOCKED. Only those bounded candidates are merged for
availability ordering. Terminal rows do not enter the hot partial indexes.
FIFO is best effort among eligible unlocked candidates; SKIP LOCKED does not
provide strict global FIFO. A worker is explicitly tenant and queue scoped, so
large tenants cannot consume another tenant's worker allocation. Provision a
bounded allocation for each queue; cross-tenant scheduling policy belongs to
the host, rather than scanning every tenant in a global work queue.
Pending jobs enter the claim order at their availability deadline; expired
leases re-enter at their lease-expiry deadline. An old job's original enqueue
time does not repeatedly put it ahead of newly eligible work after lease loss.

Retry backoff is exponential, capped, with bounded one-sided jitter. A job's
maximum attempts are durable. A classified failure can stop retry immediately.
Delayed enqueue, retry deadlines and lease deadlines use database time.

Recurring schedules are fixed intervals from 100 milliseconds to 365 days.
They persist an identity and the next UTC boundary. Dispatch atomically advances
the schedule and inserts one occurrence. Concurrent dispatchers cannot create
two copies of the same occurrence. Coalesce emits one overdue occurrence and
advances to the first future boundary. Skip emits nothing if at least one
complete interval was missed. Neither policy creates an unbounded catch-up
backlog. The `schedule:` deduplication namespace is reserved. Create returns
false for an existing identity without mutating its configuration. Delete
stops future occurrences without canceling jobs already enqueued. Calendar,
timezone/DST and cron scheduling are not offered by this interval contract.

## Operations

Initialize schemas during deployment using a privileged identity; concurrent
initializers serialize through a transaction advisory lock. Runtime identities
need SELECT/INSERT/UPDATE/DELETE on the tables, rather than DDL privileges.
Scope validation is an API boundary, not a substitute for database grants and
tenant authorization in the host. Use independent databases/schemas or database
policies when tenants need stronger isolation against direct SQL access.

ReadAsync gives lifecycle state without exposing payloads. ReadHistoryAsync
returns a bounded most-recent-first history. CancelAsync fences active work.
PruneAsync deletes a bounded batch of terminal jobs older than the selected
retention, cascades history, and releases deduplication identities. Retention is
an explicit idempotency window: an enqueue after its key is pruned creates a
new job. Keep it at least as long as upstream retry/redelivery windows. Run
bounded retention regularly for every tenant/queue; terminal storage is not
automatically unlimited. Monitor autovacuum, dead tuples, WAL, pool waits and
database storage as part of capacity planning.

The [storage maintenance contract](maintenance.md) covers page reuse, scoped
vacuum, WAL/disk budgets and the difference between logical retention and physical
growth. [Durable-format support](durable-format.md) documents format-one reopen
and configuration rollback rehearsals and the unsupported cross-format boundary.
The [physical promotion rehearsal](failover.md) retains acknowledgement,
active-owner fencing and recovery evidence, including failed lease policies.

Telemetry is exposed through ActivitySource/Meter `BlueTusk.Jobs`, with claim,
success, failed-attempt, fencing and store-failure counters and handler duration.
No high-cardinality tenant or payload tags are emitted. Rising store-failure or
fencing counters require investigation. A failed acknowledgement deliberately
leaves the lease to expire, rather than declaring an uncommitted result durable.

`InspectAsync` exposes capped pending/running/expired-lease counts and the oldest
ready age per tenant/queue. Counts report saturation explicitly and never
enumerate unbounded terminal history. Choose a bounded probe limit and poll at
an operator-appropriate interval. The [capacity and recovery harness](performance.md)
provides a reproducible workload matrix and local measurement limitations.

## Verification

Optional host adapters, trusted scopes, redacted readiness, generated JSON,
fixed-cardinality metrics and credential replacement are documented in
[hosting.md](hosting.md).

Run the unit and live suites with a disposable database:

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port=55418;Username=postgres;Password=postgres;Database=bluetusk_ecosystem;SSL Mode=Disable;Channel Binding=Disable'
dotnet test tests/BlueTusk.Jobs.Tests/BlueTusk.Jobs.Tests.csproj -c Release -nr:false
```

Live tests use a unique schema per test and remove it on disposal. They verify
transaction visibility/rollback, concurrent deduplication, tenant/queue/type
boundaries, disjoint claims, expired-owner rejection before/after reclaim,
crashed final attempts, payload constraints, retry deadlines, physically
bounded history, cancellation, retention, typed concurrency, heartbeats,
classified failure, redaction, invalid payloads and recurring dispatch races.
A backlog regression loads 8,000 eligible jobs beside 40,000 retained terminal
rows and drains claims through 16 workers with bounded batches of 64. This is
a regression scenario, not a throughput or latency guarantee.

Before production release, qualify sustained backlog/tenant/payload/concurrency
sweeps, P50/P95/P99 latency, allocation and memory, connection count, WAL/storage
growth, fair allocation under overload, abrupt process death, network partition,
database failover, clock movement, format upgrade/rollback, Linux and supported
PostgreSQL versions. Bind reports to an immutable candidate. The shared gates
in `../ecosystem/implementation-programme.md` remain required.
