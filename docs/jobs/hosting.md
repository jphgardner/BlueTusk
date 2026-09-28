# Scoped Jobs and Workflows host readiness

`BlueTusk.Jobs.DependencyInjection` and `BlueTusk.Workflows.DependencyInjection`
are optional host adapters. The core stores remain independent of Microsoft
hosting. Install the adapter for each product used by your host and register a
trusted, fixed tenant and queue:

```csharp
services.AddJobsReadiness("orders-jobs", trustedScope,
    provider => provider.GetRequiredService<PostgreSqlJobStore>(),
    new JobHealthCheckOptions
    {
        MaximumObserved = 2000,
        MaximumPending = 500,
        MaximumExpiredLeases = 0,
        MaximumReadyAge = TimeSpan.FromSeconds(30),
        Timeout = TimeSpan.FromSeconds(2),
    });
services.AddWorkflowsReadiness("orders-workflows", trustedScope,
    provider => provider.GetRequiredService<PostgreSqlWorkflowStore>(),
    new WorkflowHealthCheckOptions
    {
        MaximumObserved = 2000,
        MaximumRunning = 1000,
        MaximumCompensating = 20,
        MaximumPendingJobs = 500,
        Timeout = TimeSpan.FromSeconds(2),
    });
```

The host supplies logging as usual. Factories resolve caller-owned singleton
stores and must perform no I/O. Registration does not initialize schemas, start
workers, or dispose the supplied source. Each registration resolves one keyed
singleton check per host service provider. Distinct providers have independent
checks. Names must be unique across the host, at most 64 characters; each
adapter permits at most 128 registrations. The factory and options are explicit
to make the operational tenant boundary reviewable.

Never derive a scope, check name or store factory from an untrusted request.
Authenticate health endpoints and authorize access to the configured scope.
Returned counts and ages are operational data even though identity, payload,
schema and connection settings are omitted. A public liveness endpoint should
not expose these reports. Scope predicates enforce query separation; they are
not a substitute for PostgreSQL grants or row-level security. A read-only health
role needs schema `USAGE` and `SELECT` on Jobs `jobs` and Workflows `instances`.
Neither check needs write permissions, definition reads, history scans or DDL.
Configure row-level policies separately when the database role must also enforce
a tenant boundary. Superusers and owners can bypass RLS.

Counts are observed with a ceiling of 1–100,000 and carry saturation flags.
Pressure thresholds may be zero but cannot exceed the observation ceiling.
Saturation is `Degraded`, even when the reported count equals a threshold,
because the true count may be larger. Jobs checks pending count, expired leases
and database-clock age of ready work. Workflows checks running/compensating
instances and the same scope's Jobs dispatch. Future timers and signal waits can
legitimately remain active for a long time: `MaximumActiveAge` is disabled by
default and should only be enabled for a documented business deadline. A
workflow dispatch probe includes all Jobs in that tenant/queue; use dedicated
queues when separate pressure policies are needed. These snapshots do not
prove that a worker is registered or that a downstream dependency is healthy.

One probe per check may be in flight. An overlap immediately returns `Degraded`
with `*.health.probe_busy`; it does not enqueue another database request. A
linked cancellation deadline of 10 ms–30 s bounds each probe, including both
workflow queries. The framework registration timeout is one second longer so
the adapter can produce its stable deadline report. Caller cancellation is
propagated. Cooperative provider cancellation is required; process scheduling,
network and database behavior still need deployment qualification.

| Code suffix | Status | Meaning |
| --- | --- | --- |
| `ready` | Healthy | Observed values are inside the selected policy |
| `pressure` | Degraded | Threshold exceeded or count saturated |
| `probe_busy` | Degraded | This check already has a probe in flight |
| `deadline` | Unhealthy | The configured probe deadline expired |
| `access_denied` | Unhealthy | Provider exposes PostgreSQL SQLSTATE 42501, 28P01 or 28000 |
| `store_unavailable` | Unhealthy | Another store, transport or authentication failure |

Prefixes are `jobs.health` and `workflows.health`. The public provider may wrap
an authentication failure without a SQLSTATE; it then falls into
`store_unavailable`. The adapters never parse raw exception text or inspect wire
internals. `HealthCheckResult.Exception` is always null, descriptions are fixed
codes, and its data dictionary has only a fixed set of primitive count/flag
fields. Failed probes have no data. Direct `ReadAsync` returns immutable typed
reports. Use the supplied `JobQueueHealthCheck.JsonTypeInfo` or
`WorkflowScopeHealthCheck.JsonTypeInfo` when serializing them; their metadata is
generated and exercised by the Windows x64 NativeAOT executable.

Each adapter exports its own `ActivitySource` and `Meter`, named
`BlueTusk.Jobs.DependencyInjection` or `BlueTusk.Workflows.DependencyInjection`.
Readiness activities have fixed names and no identity tags. Instruments are
`bluetusk.{jobs|workflows}.health.probes`, `.duration` (seconds), and
`.pending_observed` or `.running_observed`. Only the counter uses a tag, `status`,
with the three values `healthy`, `degraded`, `unhealthy`. There are no tenant,
queue, handler, exception or user-input labels. Histograms intentionally
aggregate scopes; trusted hosts needing a per-scope report should use the typed
check result, keeping their exporter cardinality policy explicit.

These thresholds are application policy, not throughput guarantees. Derive
them from the accepted workload and operator budgets in [performance.md](performance.md)
and [maintenance.md](maintenance.md), and monitor physical bytes separately.

## Credential rotation rehearsal

`CredentialRotationTests` creates and drops its own login role and random
passwords. It uses a typed asynchronous password provider, changes only that
role's password, replaces the provider value, clears that source's pool, and
terminates only sessions whose PostgreSQL `usename` is the owned role. It proves
that the old credential cannot open a new connection, the provider is called
again, an interrupted fenced job retries with a newer token and one committed
database effect, and a paused workflow resumes through a signal/timer without
repeating its completed transactional activity. The role owns no fixture-wide
credentials or data. This is credential replacement plus forced session drain,
not database failover, silent network loss, or a claim about exactly-once
external effects. Coordinate pool draining and credential overlap with the
actual credential issuer in production.

Run the repeatable local suite with `eng/jobs-hosting-compatibility.ps1`. It keeps
per-version TRX files and rejects skipped tests. PostgreSQL roles are disposable
test fixtures; the suite requires an administrator solely to create/grant/drop
the uniquely owned role and terminate its sessions.

## Local verification evidence

The six tests passed with zero skips on PostgreSQL 15.19, 16.15, 17.11 and 18.6
(24 passes). They cover saturated observations and generated JSON; fixed meter
names and status labels; read-only RLS tenant separation, denied writes and
redacted permission failures; Jobs and Workflows table-lock deadlines and
overlapping probes; caller cancellation and gate recovery; explicit signal-wait
age policy; per-host typed DI/source ownership and registration bounds; and the
owned credential/session-drain rehearsal. The final Release build reported zero
warnings and errors. Raw per-version TRX paths and source hashes are retained in
[postgresql-hosting-15-18-tests.json](performance-reports/postgresql-hosting-15-18-tests.json).

The updated Windows x64 executable was published with zero observed warnings
and passed on all four versions. It exercises keyed DI registration,
`HealthCheckService`, generated readiness JSON, typed Jobs/workflow handlers,
fenced effects, source/worker restart, signals, timers, compensation and replay.
Executable and listed source hashes are retained in
[native-hosting-pg15-18.json](performance-reports/native-hosting-pg15-18.json).
These source bindings do not cover all transitive provider dependencies or an
immutable release candidate. Linux, failover and the shared operator/release
qualification gates remain separate work.
