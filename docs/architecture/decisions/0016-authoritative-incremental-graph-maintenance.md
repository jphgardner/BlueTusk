# ADR 0016: Use bounded incremental graph maintenance with authoritative repair

- Status: Accepted
- Date: 2026-08-04

## Context

Rerunning every registered `GRAPH_TABLE` query for every affected transaction is
correct but can waste database work when a transaction touches a small, known
set of result keys. Replication row images cannot safely replace query results:
they may omit columns and old values, do not carry the caller's security
context, and cannot reveal which hidden row should enter a bounded top-N result
when a visible row disappears or becomes less competitive.

An incremental engine must also preserve Streams' transaction ordering and
at-least-once contract. Publishing a graph event after acknowledging its source
transaction would create an unrecoverable loss window.

## Decision

A compiled graph plan may create an incremental session with a trusted
`IContinuousGraphIncrementalEvaluator<TResult,TKey>`. For each Streams
transaction, the evaluator derives a complete bounded affected-key set and
executes the registered, authorised key-scoped database query. It returns rows
from that query, never client-visible values projected directly from CDC
tuples.

The session can update its materialised result without a full query only when
the evaluator declares exact coverage and all of these conditions hold:

- every returned row belongs to the declared affected-key set;
- the affected-key count stays within its configured bound;
- an affected visible row remains visible; and
- an affected visible row keeps or improves its deterministic rank.

A new candidate can safely enter the bounded result and displace its current
tail. An existing row can safely change in place or improve its rank. The
session performs an authoritative full query when a visible row disappears,
leaves the predicate, or worsens in rank because an unobserved row may need to
enter the result. It also repairs when the evaluator reports uncertainty, the
affected-key budget is exceeded, a committed two-phase transaction arrives, or
the transaction/time repair interval expires.

Prepared and rolled-back two-phase lifecycle records do not change the visible
result. Commit-prepared forces a full repair. Every proposal holds the session
gate and changes committed state only after the caller explicitly commits it;
disposing an uncommitted proposal rolls it back.

`ContinuousGraphIncrementalConsumer` serializes Live replay events before
committing the session proposal and acknowledging the Streams delivery:

```text
authorised query/evaluation -> durable replay append -> session commit -> Streams acknowledge
```

The replay append is sequence-checked and byte-identical retries are accepted.
A restarted consumer appends a new authoritative initial result at the next
replay sequence before it resumes source deliveries.

## Consequences

Small, exact changes avoid a full graph query while retaining authoritative
database reads and security-scoped subscription identity. Operators can bound
affected keys and force periodic repair. Status counters distinguish
incremental transactions, unrelated transactions, duplicates, authoritative
repairs, and safety fallbacks.

An evaluator that cannot prove complete affected-key coverage must request
repair. This contract deliberately favors a full query over an incorrect
incremental result. Incremental evaluation does not weaken ADR 0011: PostgreSQL
and the registered authorised query remain the source of client-visible truth.
