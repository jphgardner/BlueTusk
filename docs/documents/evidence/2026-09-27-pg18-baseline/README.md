# Exploratory PostgreSQL 18 baseline, 27 September 2026

This bounded campaign verified all fifteen payload/tenant/concurrency cells, a 600-second
mixed-write phase, and actual child-process recovery. It exposed substantial physical TOAST
growth and long latency tails. It does **not** establish production qualification, stable
physical storage, an immutable release candidate, or an isolated hardware performance result.

The run used Windows 11 build 26200, a Ryzen 7 5800X with sixteen logical processors,
.NET 10.0.12, and the pinned PostgreSQL 18.6 Alpine image recorded in the raw report. The
dedicated loopback Docker fixture had four CPUs, 2 GiB memory, 128 MiB shared buffers,
synchronous commit, full-page writes, I/O timing, and forty maximum connections. Application
connections were capped at eight, with a separate two-connection observer. All payloads were
synthetic seeded random base64. No customer payloads or credentials appear in these reports.

Fixture setup began at 21:32:07 UTC; the sustained phase began at approximately 21:36:12 UTC;
the final report completed at 21:46:17 UTC. A root solution compilation overlapped around
21:37 UTC and a short diagnostic Projections fixture may have overlapped early setup.
Other agents changed twenty-three global candidate paths, while the recorded Documents,
Data, TypeSystem, harness, and common build inputs had zero before/after changes. These
conditions make CPU, allocation, memory and latency observations exploratory shared-host
evidence. Scoped correctness, owned relation sizes and dedicated-fixture WAL observations
remain useful within their stated measurement limits.

[documents-load.json](documents-load.json) is unchanged raw output, SHA-256
`3AE336F6A42F8654C021246F8732E5D3FECDF65432087819D84789B8C6CC7D09`.
[bindings.json](bindings.json) retains the global/scoped inventories and dependency hashes;
[run-notes.json](run-notes.json) records contention and the invalid activity measurements.
The runnable old dependency set and scoped sources remain under ignored
`artifacts/documents-load/final-pg18-600s/baseline-bin` and `baseline-source` for the separate
allocation comparison. The raw report captured harness/Documents/Data binary hashes at
measurement start; TypeSystem's original in-memory hash was not separately captured. Its
unchanged scoped source and final dependency snapshot are retained without a stronger claim.

## Measurement defect retained in the raw report

`Runtime.PeakDatabaseClients` and `Runtime.PeakDatabaseLockWaiters` are invalid for every
baseline cell: the application name was 64 ASCII bytes, PostgreSQL truncated it to 63 bytes,
and the observer's exact-name predicate matched no backend. Their zeros prove neither zero
connections nor zero lock waits. Provider pool total/busy/waiter statistics use a separate
measurement path and remain valid. A separate harness correction uses a unique 54-byte
name, verifies `SHOW application_name`, and requires at least one observed database client.
It does not rewrite this report or retroactively qualify its activity counters.

## Ten-second requested sweep cells

Each worker completes a full assigned tenant rotation, so some cells take longer than ten
seconds. Save timing includes staging and both commits of delete/reinsert cycles. Quantiles
are histogram upper-bin bounds. These rows should not be compared as controlled isolated
benchmarks, especially the long 1 MiB / thirty-two-tenant cell.

| Source payload | Tenants / writers | Transitions / s | Save p99 ms | Owned relation bytes after |
| --- | --- | ---: | ---: | ---: |
| 1024 B | 1 / 1 | 442.3 | 8.320 | 3121152 |
| 1024 B | 8 / 1 | 480.6 | 6.976 | 3153920 |
| 1024 B | 8 / 8 | 2991.8 | 10.432 | 13017088 |
| 1024 B | 32 / 8 | 2654.6 | 15.040 | 16777216 |
| 1024 B | 32 / 32 | 2728.1 | 29.696 | 17416192 |
| 65536 B | 1 / 1 | 208.6 | 39.168 | 143360000 |
| 65536 B | 8 / 1 | 161.4 | 46.592 | 114966528 |
| 65536 B | 8 / 8 | 823.7 | 60.416 | 568926208 |
| 65536 B | 32 / 8 | 855.6 | 70.144 | 604225536 |
| 65536 B | 32 / 32 | 895.7 | 103.936 | 637034496 |
| 1048576 B | 1 / 1 | 20.6 | 96.768 | 235388928 |
| 1048576 B | 8 / 1 | 20.9 | 101.376 | 298606592 |
| 1048576 B | 8 / 8 | 50.2 | 1753.088 | 642113536 |
| 1048576 B | 32 / 8 | 3.9 | 14811.136 | 349052928 |
| 1048576 B | 32 / 32 | 58.9 | 925.696 | 974389248 |

## Sustained mixed writes and storage finding

Eight tenants and thirty-two writers retained 256 fixed 64 KiB records plus eight small
hot-key counters. The measured phase completed 263,796 logical document transitions in
600.093 seconds: 439.59 transitions/second. All tenants progressed, all final typed pages
matched the exact expected payload/count ledger, and the separate contention phase retained
every successful counter increment. Save p50/p95/p99 were 38.912/317.440/1163.264 ms;
whole-operation p50/p95/p99 were 131.072/798.720/3112.960 ms. The observed maximum whole
operation was 36,568.302 ms, so this run establishes no application latency budget.

The client/harness allocated 221,549,841,272 bytes; sampled peak process working set was
2,313,252,864 bytes and peak managed memory was 1,391,314,432 bytes. These measurements
include the observer and client staging. They are not PostgreSQL server memory. Provider
pool peaks were eight total/eight busy/twenty-four waiting; it ended with eight idle, zero
busy and zero waiting connections, with zero discarded connections.

Owned relation size rose from 17,686,528 to 16,147,537,920 bytes. The final documents TOAST
size was 16,147,243,008 bytes. Between the 570- and 600-second samples, total owned relation
size grew by 1,076,412,416 bytes, about 35.88 MB/second. WAL statistics increased by
21,716,609,166 bytes; counters are cluster-scoped, can lag, and include the dedicated observer.
Fixed logical cardinality did not bound physical storage or WAL. The baseline was not tuned.

The [mid-run TOAST observation](toast-midrun.json) and
[actual vacuum progress](vacuum-midrun.json) retain evidence of accumulated dead TOAST rows
and a vacuum already working through its heap. The documents table's completed autovacuum
counter reached ten; this does not imply ten completed TOAST vacuums. No disk-pressure
threshold was reached: Docker and host disk headroom were inspected during the run.
Qualification needs a separate named maintenance/TOAST campaign with retained failure
evidence, explicit physical-byte and WAL budgets, a stationary long-run size/reuse slope,
vacuum duration/backlog observations, and workload/recovery results under maintenance.

## Process recovery and cleanup

The parent observed a real advisory-lock wait on the second insert of an uncommitted save
and hard-killed its child. Reopening preserved sixteen acknowledged two-document batches
across four tenants, exposed neither half of the blocked batch, rejected cross-tenant reads,
and rejected a stale revision after delete/reinsert. Kill-to-verified time was 138.877 ms.
This proves the exercised death-before-commit seam; it does not cover ambiguous COMMIT
acknowledgements, power loss, parent death, network partitions or physical failover.

The owned-schema count after the campaign was [zero](owned-schema-cleanup.txt). The labelled
container and volume were removed. Separate serialization allocation optimization and a
corrected-activity diagnostic follow this preserved baseline; neither changes its outcome.
