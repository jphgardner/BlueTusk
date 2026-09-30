# Attached-content 600-second local capacity run

The clean checkout of commit `a837001a8d38cfca1817ee716d1dc7072ce68dd5` ran `eng/run-documents-load.ps1 -CellSeconds 10 -SustainedSeconds 600 -IdleDrainSeconds 120 -StorageMode AttachedContent -NoBuild` on 28 September 2026. The dedicated, digest-pinned PostgreSQL 18 fixture ran on a shared Windows Ryzen 7 5800X development host. The 15 inline-JSONB sweep cells and the attached-content sustained scenario all passed exact-state checks. The runner then verified hard-killed writer recovery, source/binary bindings and owned-resource cleanup. [`documents-load.json`](documents-load.json) is the full raw report; [`bindings.json`](bindings.json) records the unchanged source fingerprint and measured assembly hashes. The [before](candidate-inputs.sha256) and [after](candidate-inputs-final.sha256) candidate-input inventories, [workload log](workload.log) and [cleanup record](owned-resource-cleanup.json) are retained with it.

The sustained scenario kept 256 documents across eight tenants. Each had a distinct, stable 64 KiB content blob; the JSONB metadata changed through replacement, patch and delete/reinsert cycles. The verifier counted exactly 256 content rows and 256 links after the run, read and hashed every retained blob, checked revisions and hot-key increments, and found no orphan to collect.

| Measured signal | Result |
| --- | ---: |
| Committed transitions / measured duration | 1,326,904 / 600.102 s |
| Transitions per second / save p99 | 2,211.1 / 100.352 ms |
| Peak owned relations, including content TOAST and indexes | 19,038,208 bytes |
| Owned relations after writes / final-half endpoint growth | 18,939,904 bytes / -6,663 bytes per minute |
| Peak whole database / after 120-second idle drain | 27,719,359 / 27,317,951 bytes |
| Cluster WAL delta / bytes per transition | 375,238,632 / 282.8 |
| Database and owned-storage samples during writes / idle observations | 120 / 24 |
| Acknowledged recovery batches and documents / kill-to-verification | 16 and 32 / 143 ms |

`eng/verify-documents-capacity-report.ps1` passed this report at a minimum 600-second duration, 500 transitions/s, 500 ms save p99, 1 GiB peak owned relations, 16 MiB/minute late owned growth and 64 KiB WAL per transition. The runner also enforced a 24 GiB database stop and 8 GiB minimum filesystem headroom. The 1 GiB owned limit includes the detached content table; whole-database size is reported separately so sidecar storage cannot disappear from the measurement. `pg_total_relation_size` already includes each table's TOAST and indexes, so those bytes are counted once.

This establishes a finite local bound for **stable attached content with changing small metadata** on this workload. It does not qualify 30-minute repeats, an independent load host, mutable large content, long retention, failover during the measured window or production disk/WAL budgets. The earlier inline-JSONB 600-second runs reached 16.20 GB and 9.06 GB for a similar retained source-content scale; their payload distribution, code and host contention differed, so this run is not a controlled throughput comparison. Existing inline-body users still bear that physical-growth risk.
