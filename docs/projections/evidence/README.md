# Local bounded workload evidence

`workload-pg18-60s.json` records a 60-second run of `ProjectionsSustainedWorkloadTests` against PostgreSQL
18.6 in Docker on the development Windows host. It measures one serial, bounded producer performing
a real SQL business write and Events outbox append in one transaction, real pgoutput projection apply,
transactional Events inbox/replay, and owned durable Live refresh every 32 commits. Lease release/recovery,
source redelivery and exact event retry occur during the run. Other compatibility tests and a sustained
storage campaign on another PostgreSQL container shared host resources during this capture; this is reproducible local evidence, not isolated
capacity, concurrency or production p99 certification.

The run completed 1,149 business transactions, 1,180 source deliveries, 49 lease recoveries, 31 duplicate
appends and 36 Live messages. Exact inbox count/sequence sum, checkpoint, published aggregate/document
and publication revision invariants passed. The report includes per-stage latency, process allocation,
working set, CPU, GC and commit-meter observations. Memory/CPU figures describe the test process;
PostgreSQL server and Docker resource usage are not included.

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = '<disposable PostgreSQL connection string>'
$env:BLUETUSK_WORKLOAD_SECONDS = '60' # supported range 1..120; ordinary suite default is 3
$env:BLUETUSK_WORKLOAD_REPORT = '<absolute path for this report>'
dotnet test tests/BlueTusk.Projections.Tests/BlueTusk.Projections.Tests.csproj -c Release -nr:false --filter FullyQualifiedName~ProjectionsSustainedWorkloadTests --logger 'console;verbosity=detailed'
```

The workload creates/drops only its own generated schemas/publication/slot. Outstanding source work and
client messages stay bounded. Test-process metric counts can include other tests when run concurrently;
run this filter alone when comparing reports. Larger fan-out/backlog, partition concurrency, multiple
independent sources, multi-node routing, disk pressure, crash/ambiguous commit and long endurance
campaigns remain separate release requirements.

`physical-promotion-pg18.json` and its matching TRX are a separate actual physical-recovery campaign,
not a throughput measurement. `run-physical-recovery.ps1` provisions and label-checks its own PG18 pair,
enables/validates synchronous `remote_apply`, hard-stops the primary, promotes the physical standby,
and invokes a dedicated fixture project. All 8 acknowledged business commits, 16 outbox events and
16 WAL inbox effects survived; replay completed 8 effects without duplicates and old Live/projection
owners were fenced. Timeline admission failed explicitly before two fresh-snapshot recovery cutovers.
Final tenant totals are 10/999. See [the recovery contract](../RECOVERY.md) for source-DDL sequencing,
logical-slot loss and nontransparent recovery boundaries. Reports contain no connection strings/passwords.
