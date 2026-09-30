# Production checklist

This checklist turns a successful BlueTusk application build into an operable
deployment. It applies to stable packages and to production-like evaluation of
`1.1.0-rc.1`. A public RC is not a substitute for an application owner’s load,
security, recovery, and change-management evidence.

## 1. Record the deployment contract

Before provisioning, record the following in the application repository:

- exact BlueTusk, .NET, EF Core, PostgreSQL, server-extension, and browser-client
  versions;
- package lockfiles and container image digests;
- source commit and deployment manifest revision;
- supported regions, failure domains, database endpoints, and destination
  services;
- expected traffic, concurrency, payload, transaction, result-set, and fan-out
  envelopes;
- accepted SLOs, RTO, RPO, error-budget policy, and incident owner; and
- rollback binary, database/schema compatibility, and persisted-format limits.

Use one BlueTusk version across the coordinated family graph. Never mix stable
and RC packages in the same deployment.

## 2. Apply the security baseline

- Require TLS with normal certificate and hostname validation.
- Prefer SCRAM, managed cloud identity, GSSAPI/Kerberos, or SSPI according to
  the environment; avoid legacy cleartext/MD5 compatibility paths.
- Use distinct least-privilege roles for application queries, migrations,
  replication, operational control, and backup/restore.
- Keep row-level security and the original `LiveSecurityScope` active for Live
  and Continuous Graph authoritative queries.
- Store connection strings, tokens, client certificates, and exporter
  credentials in the deployment secret manager, never configuration files or
  telemetry.
- Set secret rotation ownership and test rotation without process-wide
  restarts where the configured credential provider supports it.
- Keep SQL text, parameter values, result rows, CDC payloads, resume tokens,
  tenant identifiers, and exception messages out of metrics and normal logs.

Run dependency advisory, SBOM, provenance, image, and secret-scanning checks on
the exact artifacts that will be deployed.

## 3. Bound every resource

Start from measured workload data and set an explicit ceiling for:

| Resource | Primary controls | Failure signal |
| --- | --- | --- |
| Provider connections | minimum/maximum pool size, connection lifetime, checkout timeout | pool waiters and checkout latency |
| Commands | command timeout, cancellation, batch/pipeline size | command failures and P95/P99 duration |
| Multiplexing | admission limit, pending commands, pipeline size | queue-wait duration and forced shutdowns |
| Streams | transaction memory, record size, spool bytes, active deliveries | WAL lag, spooling, settlement failures |
| Sync | batch size, destination concurrency, retry/backoff, quarantine storage | retries, throttle time, destination duration |
| Live | query rows, replay retention, subscriber queue, fan-out concurrency | refresh latency, replay bytes, slow-client disconnects |
| Control Plane | inventory page, instance concurrency, snapshot-cache lifetime | active operations and operation duration |
| Continuous Graph | affected keys, result size, top-N boundary, repair interval | tier, query count, affected keys, repair reason |

Raising a limit is a capacity decision, not an incident workaround. Confirm the
database or destination has useful headroom before allowing more client-side
concurrency.

## 4. Configure PostgreSQL deliberately

- Use a supported PostgreSQL major and patch version.
- Validate server time, DNS, TLS chain, authentication rules, connection
  limits, statement/lock timeouts, WAL retention, disk alerts, and failover.
- Install extension versions before starting an application that registers
  their codecs or migrations.
- Provision logical replication slots and publications with an explicit owner,
  retained-WAL ceiling, backup/rebuild plan, and monitoring.
- Test PgBouncer mode if it is present; transaction pooling cannot preserve
  arbitrary session-affine state.
- Treat PostgreSQL 19 SQL/PGQ as capability negotiated. Do not infer it from a
  version string or disable Continuous Graph authoritative repair.

Run migrations through a dedicated role and controlled job. Application
instances should not race to mutate production schema at startup.

## 5. Wire observability before traffic

Register the product meters and activity sources used by the deployment:

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(
        "BlueTusk.Diagnostics",
        "BlueTusk.Streams",
        "BlueTusk.Sync",
        "BlueTusk.Live",
        "BlueTusk.ControlPlane",
        "BlueTusk.ContinuousGraph"))
    .WithTracing(tracing => tracing.AddSource(
        "BlueTusk.Diagnostics",
        "BlueTusk.Streams",
        "BlueTusk.Sync",
        "BlueTusk.Live",
        "BlueTusk.ControlPlane",
        "BlueTusk.ContinuousGraph"));
```

At minimum, dashboard and alert on:

- command success and P95/P99 duration;
- pool connections, leases, waiters, and checkout P95/P99;
- connection failures, retries, and failovers;
- replication receive/WAL lag and retained-slot disk;
- Streams active deliveries, settlement outcome, duration, and spooling;
- Sync errors, retries, throttle time, and transaction duration;
- Live connection outcome, active clients, refresh duration, replay, and slow
  clients;
- Control Plane operation outcome and duration; and
- Continuous Graph maintenance tier, evaluation outcome/duration, affected
  keys, authoritative query count, and fallback reason.

Use bounded names for queries and pipelines. Request IDs, user IDs, arbitrary
tenant IDs, SQL, and payload-derived values are not valid metric dimensions.
The exact instrument inventory and reference SLOs are in
[production observability](observability.md).

## 6. Prove delivery semantics

For every real-time path, write down where durability occurs and who owns the
checkpoint:

```text
PostgreSQL commit
  → WAL decode
  → Streams delivery
  → destination/replay/graph durable commit
  → Streams acknowledgement
  → durable checkpoint advance
```

Exercise duplicates, retry, cancellation, process death, network interruption,
destination unavailability, schema drift, and restart. Verify:

- an unacknowledged transaction is replayed with stable identity;
- a destination mutation is idempotent or reconciled;
- checkpoints never advance ahead of durable application state;
- poison work is quarantined with bounded retention;
- rebuilds read an authoritative source and use a controlled cutover; and
- Continuous Graph falls back to authoritative repair whenever an incremental
  result cannot be proven correct.

BlueTusk does not make a blanket end-to-end “exactly once” promise. The
application’s destination contract completes the guarantee.

## 7. Separate liveness, readiness, and startup

- **Liveness** answers whether the process can make progress and should not
  perform a database query on every probe.
- **Readiness** performs a bounded dependency check and removes an instance
  from traffic when it cannot safely serve.
- **Startup** allows schema validation, catalogue load, snapshot handoff, or
  replay recovery to complete without repeated restarts.

For workers, readiness must include source identity, slot/checkpoint
compatibility, durable store availability, and destination preflight. A green
HTTP process is not proof that a replication or Sync pipeline is healthy.

## 8. Rehearse rollout and rollback

Use an expand/migrate/contract rollout:

1. deploy backward-compatible schema and format readers;
2. canary one instance with production observability;
3. verify connection/pool, query, WAL, checkpoint, destination, replay, and
   graph signals;
4. increase traffic in measured stages;
5. retain the previous binary and compatible state until the error-budget
   window passes; and
6. remove old schema/format support only after rollback is intentionally
   closed.

Rollback criteria should be mechanical: error-rate, latency, allocation/RSS,
WAL lag, checkpoint age, reconciliation mismatch, authorization failure, or
recovery-time thresholds. Rehearse database restore, relay restore, destination
rebuild, replay reset, and graph authoritative repair.

## 9. Run the release acceptance suite

Before declaring the application ready:

- clean restore and Release build pass with warnings treated as errors;
- package and lockfile inventory matches the approved version;
- representative PostgreSQL integration and migration tests pass;
- load tests cover steady state, burst, saturation, slow dependencies, and
  cancellation;
- memory, GC, CPU/event, peak RSS, throughput, P95, and P99 remain within the
  application budget;
- fault injection proves reconnect, replay, retry, fencing, and rollback;
- backup/restore meets recorded RPO/RTO;
- dashboards, alerts, runbook links, and escalation owners are live; and
- the deployment record contains exact artifact digests and approval.

For `1.1.0-rc.1`, explicitly record that the application is on a prerelease
channel and define the exit criteria for stable `1.1.0` or rollback to `1.0.0`.

## 10. Keep an operator handoff

The on-call handoff must include:

- service topology and dependency ownership;
- dashboards and alerts;
- connection, replication-slot, checkpoint, destination, replay, and graph
  identifiers;
- safe pause/resume, drain, replay, reconcile, rebuild, and rollback commands;
- secret rotation and certificate renewal procedures;
- backup location and tested restore procedure; and
- the exact point at which the database, platform, or BlueTusk maintainer is
  engaged.

Continue with [deployment](deployment.md),
[troubleshooting](troubleshooting.md),
[upgrade and rollback](upgrade-guide.md), and the
[1.1.0-rc.1 release record](../releases/1.1.0-rc.1.md).
