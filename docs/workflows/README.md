# BlueTusk.Workflows

> **Preview.** This family is `0.1.0-preview.1` and is not published to a
> package feed yet. It is not part of the 1.1.0 release and its API may change.
> Build it from source to evaluate it. See [product status](../getting-started/install.md#product-status).

Use the shared [storage maintenance contract](../jobs/maintenance.md) for
workflow state and its dispatch Jobs. [Durable-format support](../jobs/durable-format.md)
documents same-format restart/configuration rollback and the unsupported
cross-format migration boundary.

The optional `BlueTusk.Workflows.DependencyInjection` adapter provides trusted
scope host readiness, redacted results, generated JSON and bounded telemetry.
See the shared [hosting and credential rotation contract](../jobs/hosting.md).

BlueTusk.Workflows executes immutable, versioned directed acyclic graphs in
PostgreSQL. Activities, joins, timers, buffered signals, results, compensation,
history and dispatch state survive worker restarts. Jobs supplies transactional
dispatch, bounded workers, database-clock leases, retries and fencing. The
package targets .NET 10 and is `0.1.0-preview.1`. Current implementation evidence
does not establish qualification for massive production workloads.

## Define and execute

```csharp
await using var source = BlueTuskDataSource.Create(connectionString);
var store = new PostgreSqlWorkflowStore(source);
await store.InitializeAsync(cancellationToken); // Deployment, before workers.
var scope = new JobScope("customer-42", "onboarding");
await store.RegisterDefinitionAsync(scope, new WorkflowDefinition
{
    Name = "onboard", Version = 1,
    Nodes =
    [
        new() { Id = "reserve", Kind = WorkflowNodeKind.Activity,
            Activity = "reserve.v1", Compensation = "release.v1" },
        new() { Id = "approve", Kind = WorkflowNodeKind.Signal,
            Signal = "approved", DependsOn = ["reserve"] },
        new() { Id = "wait", Kind = WorkflowNodeKind.Timer,
            Delay = TimeSpan.FromMinutes(5), DependsOn = ["approve"] },
        new() { Id = "activate", Kind = WorkflowNodeKind.Activity,
            Activity = "activate.v1", DependsOn = ["wait"] },
    ],
}, cancellationToken);

var key = await store.StartAsync(new WorkflowStartRequest
{
    Scope = scope, Definition = "onboard", Version = 1,
    Input = JsonSerializer.SerializeToUtf8Bytes(request, RequestJsonContext.Default.Request),
    DeduplicationKey = "onboard:" + request.Id,
}, cancellationToken);

var activities = new WorkflowActivityRegistry()
    .Register("reserve.v1", RequestJsonContext.Default.Request,
        RequestJsonContext.Default.Reservation, async (input, context, token) =>
        {
            return await reservationService.ReserveAsync(input, context.IdempotencyKey, token);
        })
    .Register("release.v1", async (context, token) =>
    {
        await reservationService.ReleaseAsync(context.ActivityResult,
            context.IdempotencyKey, token);
        return ReadOnlyMemory<byte>.Empty;
    })
    .Register("activate.v1", async (context, token) =>
    {
        await activationService.ActivateAsync(context.InitialInput,
            context.IdempotencyKey, token);
        return ReadOnlyMemory<byte>.Empty;
    });
await new WorkflowWorker(store, scope, "host-1", activities).RunAsync(stoppingToken);

// Another authorized request may signal the wait, even before it is ready.
await store.SignalAsync(key, "approved", "approval-event-42",
    approvalPayload, cancellationToken);
```

The caller owns the data source. Workflow and Jobs tables must live in the same
database. StartAsync also accepts a caller BlueTuskTransaction and an explicit
cancellation token: instance, nodes, history and dispatch commit or roll back
with business writes. A duplicate start key returns the original workflow only
when the original definition version and input match. Compatible migration
preserves that original identity.

## Durable execution contract

Definitions are scoped by tenant, queue, name and integer version. Registration
canonicalizes node/dependency ordering, checks immutable serialized bytes and
SHA-256, and rejects duplicate/missing identities, cycles, invalid node fields
and limits. Activity names should themselves be versioned. Changed contracts
cannot replace a registered version.

Ready branches dispatch independently. Joins require all dependencies complete.
Dependency results are fetched in one query only after their aggregate byte
budget passes. Timers use durable delayed Jobs and the database clock. Signals
are buffered per workflow/name with an explicit idempotent identity; changed
bytes under that identity fail. Ready nodes consume the earliest unconsumed
message. Signal arrival and node readiness take the same instance lock, avoiding
a lost wakeup.

Workers claim hashed job types only for registered activity names, plus the
built-in timer type. Older workers leave unsupported activity versions queued
for capable workers. A worker supports up to 127 versioned activity handlers;
allocate additional bounded workers for larger contract sets. Operators must
ensure each queued activity/compensation has a capable worker.

External activities are at least once: a crash after an effect but before its
result can repeat the activity. Context.IdempotencyKey is a stable SHA-256 over
tenant, queue, workflow, node and execution/compensation direction. Enforce it
downstream. It remains stable across retries. Expired/canceled Jobs owners
cannot persist a result, including before reclaim. Fenced transitions validate
the database deadline and atomically store results, history and child dispatch.

RegisterTransactional runs database effects with the durable result and child
jobs in the same fenced transaction:

```csharp
activities.RegisterTransactional("reserve-db.v1",
    async (connection, transaction, context, token) =>
    {
        await SaveReservationAsync(connection, transaction, context, token);
        return reservationResultBytes;
    });
```

Exceptions, cancellation and callback deadline expiry roll everything back.
Use only the supplied connection/transaction and do not commit or dispose them.
Keep callbacks short: the locked job row prevents heartbeats extending that
lease through the transaction. External effects cannot be rolled back and do
not belong in this callback.

## Failure, cancellation and recovery

Classified JobHandlerException codes can stop retry immediately. Unclassified
exceptions become `workflow_activity_failed`; messages/stack traces are never
persisted. Final failure stops new forward nodes. Running external activities
can resolve; durably completed nodes with declared compensation run sequentially
in reverse completion order. This respects dependency order and defines the
order for parallel branches.

Compensation has its own durable job and stable identity. Bounded Jobs retries
apply. Permanent/exhausted compensation leaves `compensation_failed`, retaining
the node's actual code for operator repair. A completed node without a declared
compensation remains completed. Context.ActivityResult contains its original
committed result when compensation executes.

CancelAsync defaults to compensation. It commits the instance transition before
revoking Jobs leases, avoiding opposite-order instance/job locks. Cooperative
handlers stop on heartbeat revocation; recovery closes their nodes and schedules
compensation. Passing compensate:false stops immediately. Cancellation cannot
undo an external effect whose result was never durably recorded; that case
needs downstream idempotency and application repair.

Shutdown awaits cooperative handlers and heartbeats. Recovery uses bounded
keyset pages, so long-running front entries cannot hide failed dispatches.
Jobs terminalizes a crashed final attempt, then workflows reconciles it into
failure/compensation. Missing dispatch rows produce `dispatch_missing`. Do not
prune dispatch history earlier than workflows that rely on it.

## History, replay and migration

ReadHistoryAsync provides sequence-keyset pages, database timestamps and stable
codes for starts/versions, migration, scheduling, attempts, retry, completion,
joins, signals, timers, skipped nodes, compensation and recovery. ReadNodesAsync
is the first bounded result page; ReadNodesPageAsync continues after the last
node identity. Both row and aggregate result-byte caps apply.

ReplayAsync reconstructs lifecycle/node states in a repeatable-read snapshot,
checks sequence continuity and compares them with execution state. It reports
drift and executes no external activity. Restart resumes persisted dependency
states/results and does not run committed activities again. The graph is
explicit data; replay does not replay arbitrary application control flow.
Re-executing with fresh external effects requires a new identified start.

MigrateAsync requires the expected revision and a quiescent running instance.
Scheduled/running activities or timers prevent migration. Completed nodes must
remain with identical kind, handlers, dependencies, signal/delay and attempt
contract. Pending nodes can change and new work can be appended. Version,
history and dispatch update atomically. The original definition stays registered;
operator policies must account for buffered signals when changing pending waits.

## Bounds and operations

Defaults admit 128 nodes, 32 dependencies per node, 256 KiB definitions/input,
64 KiB results/signals, 4 MiB aggregate activity input and node-read responses,
128 lifetime signal identities, 8,192 history entries, and up to 100 attempts
per node. History reserves room for a complete transition and fails explicitly
with `history_limit` before exhaustion, preserving bounded replay history.
Payload sizes are also database constraints. Durable limits and format 1 are
fingerprinted; incompatible stores fail initialization.

Jobs concurrency, claim/byte caps and lease settings apply. Budget memory for
concurrency multiplied by activity-input and definition limits in addition to
dispatch payloads. Lease settings must cover callback/database latency and pool
waits. Long external handlers need cooperative cancellation.

ReadAsync, node/history pages, ReplayAsync and ReconcileAsync support inspection.
PruneAsync deletes a bounded batch of terminal instances, cascading nodes,
signals/history and releasing start keys. Prune Jobs separately using compatible
retention. Keep both at least as long as upstream redelivery and operator
recovery windows. Runtime database grants and host tenant authorization remain
necessary; API scope does not isolate against direct SQL. Deploy schema DDL
before restricted runtime users.

ActivitySource/Meter `BlueTusk.Workflows` emits duration and recovery counts/
failures without tenant names, payloads or exception text. Monitor Jobs fencing
and store failures, queue lag, pool waits, autovacuum, WAL and retained storage.
Do not edit node state or reinterpret immutable definitions directly.

`InspectAsync` reports capped running/compensating counts and oldest active age
for one tenant/queue. Saturation flags prevent presenting an approximate count
as an exact one. See the [Jobs and Workflows capacity and recovery harness](../jobs/performance.md)
for reproducible local load, pool pressure and real process/COMMIT fault scenarios.
The [Jobs/Workflows physical promotion rehearsal](../jobs/failover.md) covers
persisted timers, signals, activity replay and compensation on synchronous
standby promotion, with retained passing and failing exploratory results.

## Evidence and qualification

The live suite covers branches/joins, timer deadlines, buffered signals,
rollback/deduplication, tenant/signal caps, retry identities, stale owners before/
after reclaim, final crash and cursor recovery, store/worker restart, ordered
compensation/failure, active cancellation, retention, migration, immutable/
corrupt definitions, fan-in/read byte budgets, history limits and replay/drift.

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port=55418;Username=postgres;Password=postgres;Database=bluetusk_ecosystem;SSL Mode=Disable;Channel Binding=Disable'
dotnet test tests/BlueTusk.Workflows.Tests/BlueTusk.Workflows.Tests.csproj -c Release -nr:false
dotnet publish tests/BlueTusk.Workflows.AotSmoke/BlueTusk.Workflows.AotSmoke.csproj -c Release -r win-x64 -nr:false
& tests/BlueTusk.Workflows.AotSmoke/bin/Release/net10.0/win-x64/publish/BlueTusk.Workflows.AotSmoke.exe
```

The Windows x64 NativeAOT executable has been published without warnings and
passed against live PostgreSQL: generated typed Jobs/workflow handlers, fenced
effect, source/worker restart, signal, timer, compensation and replay. It uses
unique disposable schemas. This is one AOT scenario, not universal deployment
qualification.

Still required: sustained backlog/tenant/fan-in/payload/concurrency sweeps,
P50/P95/P99/allocation/memory/WAL evidence, overload fairness, abrupt process
death, partitions, failover, clock movement, format upgrade/rollback, Linux,
supported PostgreSQL releases, security and operator release gates in
`../ecosystem/implementation-programme.md`.
