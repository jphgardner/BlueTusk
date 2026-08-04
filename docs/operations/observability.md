# Production observability and SLOs

BlueTusk V1 exposes one OpenTelemetry meter per product area and treats
telemetry as a versioned operational contract. Metrics are low-overhead when no
listener is attached, exporter-neutral, and deliberately avoid SQL text,
parameter values, tenant security scopes, resume tokens, payloads and
deployment identifiers.

The authoritative inventory is
[`eng/telemetry-contract.json`](../../eng/telemetry-contract.json). The
repository verifier fails when a runtime instrument is missing from that
contract, a declared instrument does not exist, an instrument type changes, a
tag is not emitted, or a new metric has no cardinality review.

## Meter inventory

| Meter | Instruments | Operational boundary |
| --- | ---: | --- |
| `BlueTusk.Diagnostics` | 27 | Commands, connections, COPY, replication, pools, prepared statements and multiplexing |
| `BlueTusk.Streams` | 9 | Transaction volume, snapshot rows, spooling, active deliveries, settlement outcome and duration |
| `BlueTusk.Sync` | 6 | Transactions, errors, retries, throttling, snapshot rows and destination duration |
| `BlueTusk.Live` | 11 | Authoritative query and refresh duration, rows/events, connections, active clients, fan-out, replay, resume validation and slow clients |
| `BlueTusk.ContinuousGraph` | 4 | Active/prepared evaluations, settlement outcome, event count and lifecycle duration |
| `BlueTusk.ControlPlane` | 3 | Active operations, reconcile/delete outcomes and operation duration |

The 60-instrument count is a minimum gate, not an invitation to add arbitrary
metrics. Every new series consumes memory, storage and query cost.

## Application registration

Register every meter used by the deployed product families and export OTLP to
an internal Collector:

```csharp
builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "orders-api",
        serviceVersion: ThisAssembly.Version,
        serviceInstanceId: Environment.MachineName))
    .WithMetrics(metrics => metrics
        .AddMeter(
            "BlueTusk.Diagnostics",
            "BlueTusk.Streams",
            "BlueTusk.Sync",
            "BlueTusk.Live",
            "BlueTusk.ContinuousGraph",
            "BlueTusk.ControlPlane")
        .AddRuntimeInstrumentation()
        .AddOtlpExporter())
    .WithTracing(tracing => tracing
        .AddSource(
            "BlueTusk.Diagnostics",
            "BlueTusk.Streams",
            "BlueTusk.Sync",
            "BlueTusk.Live",
            "BlueTusk.ContinuousGraph",
            "BlueTusk.ControlPlane")
        .AddOtlpExporter());
```

Install and configure the OpenTelemetry hosting, runtime-instrumentation and
OTLP-exporter packages in the application. BlueTusk remains exporter-neutral
and does not add those dependencies to provider packages.

Set at least `service.name`, `service.version`, `service.instance.id`,
deployment environment, region/failure domain and the immutable application
commit or image digest. Do not put user IDs, raw tenant IDs, SQL, parameter
values, connection strings, tokens or exception messages in resource
attributes.

## Reference deployment pack

[`ops/observability`](../../ops/observability/README.md) contains:

- an OTLP Collector configuration with a memory limiter, batching, Prometheus
  export and a required TLS trace backend;
- Prometheus rules for all 14 primary V1 SLO alerts plus six ratio-SLO
  slow-burn alerts; and
- a Grafana dashboard for all six meters.

The Collector endpoints must live on an internal network. Configure TLS,
authentication, backend credentials, high availability, data retention and the
exact digest-pinned Collector image in the deployment repository. The checked-in
configuration is a reference topology, not a secret-bearing production
manifest.

OpenTelemetry's Prometheus translation normalizes dotted metric and tag names.
Before enabling alerts, compare the actual `/metrics` output with the rule
names. Keep that compatibility test in the deployment pipeline.

## Reference SLO profile

The machine-readable profile is
[`eng/v1-production-slos.json`](../../eng/v1-production-slos.json). It defines:

- command success of at least 99.9% over 30 days;
- pool checkout P95 at or below 50 ms;
- replication receive-lag P95 at or below five seconds;
- Streams acknowledgement success of at least 99.99% and settlement P95 at or
  below five seconds;
- Sync success of at least 99.9% and destination-application P95 at or below
  two seconds;
- Live connection success of at least 99%, refresh P95 at or below 250 ms, and
  no routine slow-client disconnects;
- Continuous Graph durable evaluation commit of at least 99.9% and evaluation
  P95 at or below one second; and
- Control Plane terminal success of at least 99.9% and operation P95 at or
  below 60 seconds.

These are reference application objectives, not universal package guarantees.
Database distance, query plans, payloads, destination APIs and hardware all
change achievable latency. The production owner must either accept this profile
or record a reviewed override with the same indicator, window, alert, error
budget and incident owner.

## Error-budget policy

The reference 30-day window uses multi-window burn alerts:

- page when a 14.4-times burn is sustained across both the one-hour and
  five-minute windows;
- create an owned ticket when a six-times burn is sustained across both the
  six-hour and 30-minute windows;
- freeze rollout when the long-window burn confirms the loss;
- stop non-remediation deployments after the 30-day budget is exhausted; and
- resume only after the failing release is rolled back or a measured fix has
  restored the budget trajectory.

Low request volume needs special treatment. A denominator near zero can make a
single expected rejection look catastrophic; the Prometheus rules use
`clamp_min`, but the operator must also correlate absolute event counts.

## Cardinality policy

The contract has three classes:

- `none`: no metric attributes;
- `bounded`: values come from a closed enum or fixed operation set; and
- `deployment-bounded`: configured hosts, databases, source fingerprints,
  slots, query names or pipeline IDs.

Bound every registry before production. Do not dynamically derive query names,
pipeline IDs or source names from requests. If a deployment-bounded dimension
can grow without an administrative action, it is not actually bounded and must
be removed or aggregated before release.

## Alert runbooks

### Provider command failures

1. Split failures by database operation, host and error type.
2. Check PostgreSQL availability, saturation, locks and deployment events.
3. Compare failures with connection retries/failovers and pool waiters.
4. If a new release introduced the change, halt rollout and execute rollback.
5. Never enable SQL or parameter-value capture as an emergency shortcut.

### Pool saturation

1. Compare checkout P95, current connections, leases and waiters.
2. Inspect PostgreSQL backend count, CPU, memory and lock pressure.
3. Distinguish a pool that is too small from a database beyond useful
   concurrency.
4. Drain traffic or reduce concurrency before raising pool limits.

### Replication and Streams lag

1. Check receive lag, WAL-byte lag, active deliveries and delivery duration.
2. Inspect replication-slot retained WAL and disk capacity immediately.
3. Identify a blocked consumer, large transaction, network interruption or
   downstream throttle.
4. Preserve the slot and checkpoint until recovery ownership is clear; do not
   drop a slot to silence an alert.

### Streams delivery failures

1. Separate acknowledged, nacked and disposed settlements.
2. Inspect settlement-failure counts and whether the transaction was spooled.
3. Verify durable checkpoint movement and retained transaction identity.
4. Quarantine or retry through policy; never manually advance a checkpoint.

### Sync failures and throttling

1. Group errors, retries, throttle duration and transaction duration by the
   bounded pipeline ID.
2. Check destination quotas, credentials, schema/version drift and circuit
   state.
3. Confirm relay backlog and destination idempotency before replay.
4. Use reconciliation and rebuild/cutover controls instead of ad-hoc writes.

### Live replay and slow clients

1. Split connection outcomes into quota, replay, token and not-started causes.
2. Check replay-store availability and append bytes/events.
3. Compare active clients, fan-out rate and slow-client policy.
4. Increase a subscriber buffer only after measuring retained memory and
   recovery time; bounded reset/disconnect is the safety invariant.

### Continuous Graph repair

1. Split evaluation outcomes and modes.
2. A rise in abandoned or failed settlements is a durability incident.
3. A rise in authoritative repair may indicate affected-key budget pressure,
   ranking changes, query drift or scheduled repair.
4. Preserve authoritative repair; do not disable it merely to improve latency.

### Control Plane reconciliation

1. Split outcomes into failed, lease unavailable/lost, canceled and accepted
   terminal states.
2. Confirm the desired generation, observed revision and fencing token.
3. Check provider API health and tenant quotas.
4. Never bypass delete protection or fencing during incident response.

## Recovery objectives

The reference profile requires rehearsed evidence, not a written intention:

| State | RTO | RPO | Required rehearsal |
| --- | --- | --- | --- |
| Provider application | 15 minutes | Application-owned | Failover and connection recovery against the real topology |
| Streams relay | 30 minutes | Zero acknowledged transactions | Encrypted backup, empty-schema restore, checkpoint comparison and reconciliation |
| Sync destinations | 60 minutes | Zero beyond durable relay checkpoint | Rebuild and cutover for every configured destination |
| Live replay | 30 minutes | Durable replay-store commit | Store restore plus signed resume and forced reset |
| Control Plane | 60 minutes | 15 minutes desired state; audit exported externally | Database restore, convergence, audit export and delete-protection check |

Record date, candidate commit, operators, timings, object counts, hashes,
failure observations and follow-up actions in the release evidence directory.

## Telemetry failure

Loss of telemetry must not stop database commands, but it must be visible:

- monitor Collector health and export failures separately;
- alert when traffic exists but expected BlueTusk series are absent;
- bound Collector queues and memory;
- treat dropped spans or metrics as an observability incident; and
- never let an unbounded exporter queue threaten the application process.

The reference Collector follows OpenTelemetry guidance to use a memory limiter
and batching. Prometheus rules use `histogram_quantile` over rate-normalized
buckets, and the Grafana dashboard is stored as versioned JSON so deployment
changes remain reviewable.
