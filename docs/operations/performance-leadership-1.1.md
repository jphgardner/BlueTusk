# BlueTusk 1.1 performance leadership report

**Candidate basis:** `ac702d7` plus the coordinated 1.1 implementation

**Release:** 1.1.0 across Provider, Streams, Sync, Live, Control Plane, and
Continuous Graph

**Publication state:** disabled until every exact-candidate gate passes,
including digest-pinned PostgreSQL 19 GA

## Current verdict

Provider's retained Npgsql 10.0.3 evidence passes all 16 latency/allocation
pairs and four saturated-pool shapes. The complete 1.1 leadership claim is
**not yet earned**: final-SHA Windows/Linux comparisons, confidence intervals,
and endurance evidence remain mandatory. Missing evidence and ties fail.

## Implemented hot paths

| Family | 1.1 implementation |
|---|---|
| Provider | Integrates the `ac702d7` command, pooling, COPY, EF, protocol, and allocation work while retaining the 16-feature Npgsql 10.0.3 matrix. |
| Streams | Reuses bounded transaction-assembly collections, preserves owned tuple memory, avoids the envelope decode copy, and retains segmented pooled spool writes and zero-copy memory-mapped replay. |
| Sync | Builds NATS envelopes directly into one exact-sized integrity-protected buffer, streams OpenSearch NDJSON to HTTP without a monolithic bulk array, reuses PostgreSQL binary payload memory, and retains Redis atomic batch ordering. |
| Live | Mutates explicitly affected rows while sharing the immutable key index, reuses one serialized replay payload for fan-out, and coalesces Angular/React reducer notifications. |
| Control Plane | Uses set-based inventory reads, bounded cross-instance concurrency, a short-lived single-flight immutable cache, and source-generated API JSON metadata. |
| Continuous Graph | Uses immutable compiler impact plans, trusted CDC projection only behind a complete explicit trust contract, automatic key-scoped authoritative queries, ordered affected-candidate merges, and fail-closed full repair. |

## Required measurement matrix

The machine-readable authority is
[`eng/performance-leadership-contract.json`](../../eng/performance-leadership-contract.json).
It requires identical datasets, payloads, durability boundaries, warm-up, and
observation windows on dedicated Windows x64 and Linux x64 runners.
The manual-only
[`performance-leadership.yml`](../../.github/workflows/performance-leadership.yml)
checks out one full SHA on both runner classes, starts the digest-pinned current
PostgreSQL 19 development milestone, executes the complete raw capture, and
archives each environment independently. The same-SHA ratio/confidence and
external-reference gates remain mandatory.
`verify-performance-leadership-evidence.ps1` expands this contract into 886
exact environment/workload comparisons, rejects missing or duplicate cases,
and evaluates both the observed ratio and the conservative 95% confidence
bound. Its mutation self-test proves that a weak ratio and an incomplete matrix
both fail closed.

- Provider: 16 features at concurrency 1, 64, and 256, including TLS and
  constrained-network variants, against Npgsql 10.0.3.
- Streams: 1/1,000-change transactions, 4 MiB spill, snapshot/catch-up, and
  commit-to-delivery against digest-pinned Debezium Server 3.6.1.Final.
- Sync: 1/100/1,000 mutations to NATS, Redis, OpenSearch, and PostgreSQL against
  Debezium plus each native destination client.
- Live: 10/1,000/100,000 rows and 1/64/1,000/10,000 subscribers, with churn and
  slow clients, against ASP.NET Core SignalR 10.
- Control Plane: 1/100/1,000 sources with 32/256 clients, compared with 1.0 and
  absolute scale budgets.
- Continuous Graph: 1K/100K/1M edges, top-N 10/100/1,000, all three tiers, and
  insert/update/delete/rank/truncate/two-phase/schema-drift scenarios, against
  prepared raw `GRAPH_TABLE` and 1.0 full requery.

## Non-negotiable gates

Same-runtime mean, P95, P99, and allocation ratios must each be at most 0.98.
Cross-runtime throughput must be at least 1.05x and P95, P99, CPU/event, and
peak RSS ratios at most 0.95. The 95% confidence interval must establish the
win. Unique workloads may regress no more than 2% from 1.0 and each family’s
primary hot path must improve P95 and allocation by at least 20%.

Trusted CDC graph deltas must use at most 10% of full-requery P95 and allocation;
authoritative scoped deltas at most 35%. Every result set must retain raw
samples, commit SHA, environment and image manifests, allocation/CPU/RSS/GC
counters, verifier self-tests, and this readable consolidation.

## Remaining evidence before release

1. Capture exact-final-SHA Windows and Linux benchmark evidence and run the
   ratio/confidence verifier.
2. Archive Streams 72-hour, then Sync 24-hour, Live/Control Plane 24-hour, and,
   after PostgreSQL 19 GA, Continuous Graph 24-hour endurance evidence.
3. Run PostgreSQL 15–19, TLS, trimming, NativeAOT, package-consumer, Angular,
   supply-chain, provenance, SBOM, install, and smoke gates.
4. Obtain the independent coverage-guided CI handoff after the final branch
   update. This implementation does not trigger or iterate that workflow.
5. Enable publication only after all evidence resolves to the same immutable
   commit and PostgreSQL 19 GA image digest.

Until those items pass, 1.1 is a performance-engineered candidate—not a blanket
“faster everywhere” release claim.
