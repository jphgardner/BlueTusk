# Deployment and configuration

This guide describes the application-level deployment contract for BlueTusk.
It does not enable package publication or replace the product-family release
process.

## Deployment topology

Typical request/response application:

```text
.NET service
  └─ long-lived BlueTuskDataSource
      └─ bounded physical pool
          └─ PostgreSQL primary or selected multi-host target
```

Real-time application:

```text
PostgreSQL publication + replication slot
  └─ Streams source
      ├─ checkpoint/state store
      ├─ optional durable relay
      ├─ Sync destinations
      └─ Live / Control Plane / Continuous Graph consumers
```

Keep each ownership boundary independently observable and recoverable.

## Connection configuration

Provide connection strings through the host’s secret/configuration mechanism,
not source control. At minimum, decide:

- host list and ports;
- database and role;
- TLS mode and certificate validation;
- channel binding;
- pool minimum/maximum size;
- command and connection timeouts;
- target session attributes for multi-host routing; and
- application name for PostgreSQL observability.

Production deployments should not disable TLS or channel binding merely because
local test containers do.

## Data-source lifetime

Create one data source per distinct connection and type configuration. Register
it as a singleton in the application host. Logical connections are cheap,
short-lived leases and should be disposed deterministically.

Multiple data sources are appropriate when:

- applications use separate security principals;
- read and write traffic have intentionally different topologies;
- extension/type registries differ; or
- isolation policy requires separate pools.

Do not use multiple data sources as an ad hoc substitute for pool sizing.

## Database roles

Use least-privilege roles:

- application query/write role;
- migration role with schema-change permission;
- replication role with only the required publication/slot rights;
- operational read role for dashboards where applicable.

Avoid running ordinary requests as the database owner or a superuser. Row-level
security, default privileges and `search_path` should be deliberate deployment
decisions.

## Migrations

Run EF Core migrations as a controlled deployment step or a single elected
startup task. Do not let every horizontally scaled instance race to apply
migrations.

Before applying:

1. back up the database or confirm point-in-time recovery;
2. inspect generated SQL;
3. identify table rewrites and lock duration;
4. confirm PostgreSQL/extension capability requirements; and
5. prepare the rollback or forward-fix plan.

PostgreSQL operations such as concurrent index creation may suppress an outer
transaction; the migration SQL and tests document those boundaries.

## Pool and timeout policy

Pool capacity is a database-wide resource. Sum maximum pool sizes across all
instances and data sources, then leave PostgreSQL capacity for migrations,
operations, replication and emergency access.

Set timeouts in descending order:

```text
request deadline
  > command timeout
    > pool-acquisition / connection timeout
      > individual network operation allowance
```

The exact values depend on workload latency and failure policy. The ordering
prevents lower layers from outliving a canceled request.

## Multi-host and failover

Use target-session attributes to express whether a workload needs a primary,
read-write server, standby or read-only target. Preferred modes may fall back;
strict modes must reject role-incompatible servers.

Failover changes server identity. Real-time components must validate source
identity and replication state before resuming from a checkpoint. A reachable
new primary is not automatically the same logical source.

## Real-time persistence

For Streams/Sync:

- put checkpoints and relay data on durable storage;
- monitor disk capacity and retention;
- back up the state required to correlate checkpoints and source identity;
- preserve idempotency/version metadata at destinations;
- isolate dead-letter/quarantine records from normal progress; and
- document replay and rebuild procedures.

Read [real-time operations](../realtime-platform/operations.md) for restart and
incident behavior.

## Health and observability

Expose:

- process liveness;
- PostgreSQL readiness;
- pool active/idle/waiting counts;
- command latency and failures;
- replication WAL lag;
- checkpoint age;
- relay storage/retention;
- destination apply/reconcile failures; and
- deployment reconciliation/audit events.

Use OpenTelemetry traces and metrics described in
[observability](../observability.md). Never attach SQL parameter secrets or raw
credentials to telemetry.

## Rolling deployment

Before rolling out a new version:

1. check public API and persisted-format compatibility;
2. apply backward-compatible database changes first;
3. deploy a small cohort;
4. watch pool pressure, errors, latency, WAL/checkpoint lag and destination
   reconciliation;
5. expand gradually; and
6. remove compatibility scaffolding only after every old instance is gone.

For a real-time mapping or format change, use the documented rebuild/version
transition. Do not make old checkpoints silently mean something new.

## Release boundary

The repository can produce candidate artifacts while publication is disabled.
Only the [release process](../release-process.md) can authorize stable package
publication, and only after all required workflow evidence refers to the same
immutable commit.
