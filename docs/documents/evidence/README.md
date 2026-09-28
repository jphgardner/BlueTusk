# Documents workload and process recovery

`eng/run-documents-load.ps1` provisions a dedicated labelled PostgreSQL 18 fixture on loopback port
55818 with four Docker CPUs, a 2 GiB memory limit, `track_io_timing=on` and 40 server connections.
Its container/volume names are unique. Cleanup checks both ownership labels before removing either
resource. It does not inject failures into the ordinary shared test databases. The runner restores
its process environment and retains reports under ignored `artifacts/documents-load` by default.

The [27 September PostgreSQL 18 baseline](2026-09-27-pg18-baseline/README.md) retains a
600-second completed campaign, actual hard-killed writer recovery, and its unresolved physical
TOAST growth. Shared-host contention and invalid baseline database activity fields are disclosed;
the raw baseline is not production qualification.
The [separate corrected-activity diagnostic](2026-09-27-activity-diagnostic/run-notes.json)
verified nonzero server client peaks equal to the provider's one/eight connection pool caps
in all sixteen scenarios, plus hard-killed writer recovery and owned fixture cleanup. It is
an instrumentation check; its short, shared-host timings are not an optimization comparison.
The [retained-binary allocation pair](2026-09-27-allocation-pair/README.md) measures six
BenchmarkDotNet staging and source-generated serialization cases on the original and optimized
Documents executables. It supports a lower staging allocation claim, not a database throughput
or latency improvement.
The [TOAST maintenance profiler diagnostics](2026-09-27-maintenance-diagnostics/README.md)
verify direct parent/TOAST observations, actual fixture-only vacuum settings, Docker filesystem
headroom sampling, intentional resource-stop evidence and cleanup. They are too short and
contended to establish whether tuning controls the sustained physical growth.
The [28 September source-frozen 600-second comparison](2026-09-28-maintenance-pair.md)
records successful default and fast-TOAST-vacuum fixtures with the same inputs and binaries.
Tuning reduced measured relation growth but increased WAL per transition and tail latency;
neither profile established a physical storage bound.

```powershell
# Build provider/Documents dependencies first when running from a fresh checkout.
dotnet build benchmarks/BlueTusk.Documents.LoadHarness/BlueTusk.Documents.LoadHarness.csproj -c Release -nr:false

./eng/run-documents-load.ps1 -NoBuild -CellSeconds 10 -SustainedSeconds 600 `
  -MaintenanceProfile PackageDefaults -MaximumDatabaseBytes 25769803776 `
  -MinimumFilesystemAvailableBytes 8589934592 -IdleDrainSeconds 120 `
  -ReferenceHost '<named hardware/OS/runtime environment>'
```

`ToastVacuumFast` is a separate experimental fixture profile that sets table and TOAST
autovacuum reloptions after creating only its generated schema. Run each profile in a fresh
owned fixture and retain both reports, including nonzero bounded resource-stop results.
The filesystem observer checks the actual Docker PGDATA mount once per second and the harness
rejects stale/missing samples. The database cap is checked after seed batches and every five
seconds during writes, so it is a finite stop policy rather than a filesystem quota with zero
overshoot. No profile modifies BlueTusk runtime defaults.

The executable sweeps synthetic 1 KiB, 64 KiB and 1 MiB payloads through tenant/writer pairs
`1/1`, `8/1`, `8/8`, `32/8` and `32/32`. Payloads use seeded random base64 to avoid compressing a
repeated-character fixture down to a few TOAST pages. Eight records per writer/visited tenant cap
logical retained data; the largest cell retains approximately 256 MiB of source content. Each
writer owns its record keys, while a separate counter phase exercises optimistic concurrent
revision retries. Every worker completes at least one rotation of its selected tenants, so a
short or overloaded cell can take longer than its requested minimum duration. The application
pool is capped at eight connections; an independent two-connection observer avoids collecting
activity through the saturated application pool.

The sustained fixture now gives each retained document a distinct, stable seeded payload, so
content-addressed storage cannot appear successful merely by deduplicating different documents.
Earlier retained reports used one shared payload per scenario; they remain valid evidence of
JSONB/TOAST growth but cannot qualify a future immutable-content design. The timed mixed
workload loads bounded batches and performs complete replacement, Count-only
JSONB patch, and explicit delete/reinsert cycles. The latter are two separate session commits,
with an expected absent interval; they are not an atomic update. After the measured phase,
bounded keyset pages verify every retained payload, tenant marker and expected mutation count,
with no repeated or missing ID. Hot-key verification checks every successful increment and
the noncycling revision fence across recreation. Session over-capacity staging is rejected and
its uncommitted rows remain absent. All scenarios drop only their generated schemas.

Reports contain measured save/load/whole-operation percentiles, documents and source bytes per
second, per-tenant successful progress, mutation/conflict/rejection counts, allocated bytes,
GC generations, client CPU and sampled working set/managed memory. The fixed-memory histogram
has 128 subdivisions per power-of-two microsecond interval; reported quantiles are upper bucket
bounds, not exact sorted raw timings. Save timing includes staging serialization; delete/reinsert
timing includes both commits. Throughput counts completed logical document transitions rather
than SQL commands, deleted rows or individual transaction statements.

The observer samples pool total/busy/waiters, the workload's PostgreSQL clients/lock waiters, and
database commit/rollback/row/block/temp counters. Before/after observations and bounded five-second
storage samples include WAL position/records/full-page images/bytes and owned table heap, indexes,
TOAST, estimated live/dead rows and vacuum/analyze counts. PostgreSQL counters can lag; WAL is
cluster-scoped and database counters include the observer. Client allocations/CPU likewise include
the harness and observer, not PostgreSQL server memory/CPU. Docker final statistics are a final
snapshot, not a time-series server CPU qualification. The hot-key phase is separately timed and
excluded from mixed-workload throughput, runtime and before/after WAL observations.

The process-recovery phase starts an owned `dotnet` child. The parent records sixteen successful
two-document save acknowledgements across four tenants. A trigger then takes a parent-held
advisory lock on the second document in another save, configured with one command per batch.
The parent observes that real PostgreSQL lock wait, hard-kills the child, releases only its barrier,
and waits for the killed client backend to drain. Reopening the store must retain all 32 acknowledged
payloads, reveal neither partially committed document, reject cross-tenant reads and reject a
stale revision after deleting/reinserting an acknowledged identity. This tests death before commit;
it does not cover an ambiguous successful COMMIT whose acknowledgement is lost, parent death,
power loss, storage pressure, physical failover or network partition.

For separate BenchmarkDotNet microallocation/CPU measurements, run:

```powershell
dotnet run --project benchmarks/BlueTusk.Documents.LoadHarness -c Release --no-build -- `
  --microbenchmarks --job short --filter '*DocumentStagingBenchmarks*' `
  --artifacts '<absolute output directory>'
```

These benchmarks measure source-generated JSON serialization/deserialization and staging eight
documents without database I/O. They do not measure PostgreSQL save throughput. BenchmarkDotNet
generates/builds its own executable; coordinate that provider dependency build with concurrent
repository work. The workload JSON records runtime/OS/hardware scope, server settings, image,
assembly SHA-256 identities and the runner's candidate input fingerprint. A fingerprint of a
dirty working tree is local evidence, not an immutable release candidate or production approval.
The runner retains runnable dependency binaries and scoped sources before measurement, verifies
their hashes against the raw report, and writes before/after candidate comparisons to `bindings.json`.
Repeat runs, Linux/other native architectures, representative application distributions,
explicit latency/throughput budgets, longer endurance and recovery campaigns remain required.

`eng/verify-documents-capacity-report.ps1` is an offline qualification check. It requires
explicit limits for peak bytes across all owned relations (each parent total already includes
its TOAST), late-window physical growth, WAL per committed transition, throughput, and save
p99. It also rejects missing maintenance samples, a shared-payload or inline-only sustained
fixture, incomplete logical checks, and failed hard-kill recovery. The old 600-second default
report exceeds an illustrative 512 MiB relation ceiling and 16 MiB/minute late-growth ceiling
and lacks the attached-content contract; that failure is expected. Limits must be chosen
for the intended reference hardware and workload, then met on a new, source-frozen, multi-hour
campaign before qualifying Documents capacity.
Pass `-StorageMode AttachedContent` to the guarded runner to keep only hot metadata in JSONB
for the sustained scenario. Its 15 sweep cells still exercise the legacy inline-body contract.
The attached-content run verifies one distinct blob and link per retained document, exact
content and revision on read, and no orphaned blob after delete/reinsert churn. The report
labels the storage mode and the verifier sums every owned relation, including content and
links, so detached bytes cannot disappear from the physical budget.
The guarded runner can request up to six hours of sustained writes. Its filesystem observer
publishes atomic samples once per second and retains the latest 64 files plus lifetime minimum
headroom and maximum use; the workload report retains five-second database and maintenance
time series. A campaign still stops at its database-size or filesystem-headroom guard.
