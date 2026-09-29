# Search mixed ingestion and retrieval capacity campaign

`eng/run-search-capacity.ps1` runs a dedicated PostgreSQL 18 Search campaign from a **clean, full commit SHA**. It provisions a digest-pinned, labelled Docker volume and container on loopback, with four CPUs and 2 GiB of memory, then removes only its own fixture. The runner takes before/after whole-source fingerprints, snapshots the built executable and dependencies, checks the executable after each run, and hashes every archived raw artifact. It never publishes a package or marks the product production-qualified.

The workload seeds 128 distinct 4 KiB documents in each of eight tenants. Content is deterministic high-entropy text with a searchable marker. Documents include public, `readers`-authorized and `restricted`-only ACLs. One writer per tenant offers an update every 200 ms while four readers each offer a scoped full-text query every 100 ms: nominally 40 writes and 40 reads per second. The schedule uses a monotonic clock and skips missed slots instead of building an unbounded client queue. Every planned slot is counted as offered or skipped; every offered call is counted as accepted or explicitly rejected by bounded Search admission. Accepted and rejected operations retain separate nearest-rank p50/p95/p99 timing samples. Five-second physical samples include owned relation bytes, whole-database bytes and inserted WAL position. WAL is PostgreSQL-cluster scoped, so the owned fixture must remain isolated.

At the end, the harness checks every persisted document's exact version and tenant, rejects a stale write and a conflicting same-version write, accepts an identical replay, verifies tenant/ACL isolation under concurrent reads, and waits for query snapshots to expire and prune. A failed invariant stops the campaign and leaves bounded failure evidence. The fixed corpus measures update churn and query retention; it does not model unbounded corpus growth.

The first run should be a **diagnostic** on a committed candidate:

```powershell
./eng/run-search-capacity.ps1 -ExpectedCommit '<full 40-character SHA>' -Seconds 60 -Repetitions 1
```

That run is too short for qualification and its manifest stays `QualifiedLocalCapacity=false`. After reviewing its raw evidence, omit `-Seconds` and `-Repetitions` for two 30-minute runs in separate fresh owned PostgreSQL fixtures:

```powershell
./eng/run-search-capacity.ps1 -ExpectedCommit '<full 40-character SHA>'
./eng/verify-search-capacity.ps1 -ExpectedCommit '<same SHA>' -EvidenceDirectory 'artifacts/search-capacity/<campaign>'
```

The offline verifier requires both runs to pass every budget in [`eng/search-capacity-budgets.json`](../../eng/search-capacity-budgets.json): at least 20 accepted writes/s and reads/s against the fixed 40/s offered schedules, at most 10% missed schedule slots or explicit admission rejections on either path, at most 2% rejected maintenance-prune admissions, accepted-operation p99 at most 5 s and rejected-admission p99 at most 500 ms, at most 1 GiB peak owned relations and 8 GiB whole database, at most 32 MiB/min late owned-relation growth, and at most 8 MiB inserted WAL per accepted write. It also checks complete five-second time-series coverage, per-tenant progress, final logical correctness, the full candidate/source and binary hashes, and the SHA-256 of every archived file. The harness writes a current progress snapshot every five seconds so a failed run retains its last accepted/rejected counts and physical observation. These are **proposed thresholds**, not achieved results; a local capacity pass exists only after the full campaign executes and verifies.

This campaign covers PostgreSQL full-text Search only. It does not qualify vector or hybrid ranking, ANN recall, OpenSearch, external embedding providers, Search.Jobs admission/retries, multi-host serving, failover, near-limit documents, sustained corpus growth, production ACL/RLS policy, or fleet-wide disk/WAL budgets. Run those as separate isolated campaigns before a production capacity claim.

## Current diagnostic findings

The first 1,800-second default-index run on `b1ed438e3417ebe4efaac9b85cc7507e493e376b` failed on its first repetition with `SearchCursorExpiredException`; late read throughput had also fallen behind the offered schedule. Its final progress observation showed about 615 MiB of owned relations and 12.6 GiB of inserted cluster WAL. A separate 900-second run with the full-text GIN index removed completed, but that schema cannot stand in for indexed Search.

A 900-second run on `d0b339faea7eb39a25394f6814cfe33dbfed96a3` disabled GIN `fastupdate` after initialization and cleaned its pending list. It completed with 35,034 accepted writes, 35,473 accepted reads, 235 ms write p99, 178 ms read p99 and 338 MiB peak owned relations. It rejected 9 of 179 maintenance admissions, above the proposed 2% maximum. More seriously, its reader scheduler counted 37,619 offered-plus-skipped slots against a 36,000-slot schedule because an early wake-up could subtract an interval. The harness and offline verifier now enforce exact schedule accounting; the old report remains diagnostic only. Neither altered-index run qualifies a product configuration, and the default indexed path still needs a full exact-candidate rerun.
