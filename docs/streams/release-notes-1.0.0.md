# BlueTusk Streams 1.0.0 release record

Status: release-prepared, not published.

Streams 1.0.0 stabilises transaction-preserving CDC, typed and dynamic change
rows, snapshot-to-stream handoff, direct and durable-relay delivery, state
stores, CloudEvents, hosted integration, Aspire, testing helpers, and tooling.
The public API and durable formats are frozen by
[`eng/streams-api-freeze.json`](../../eng/streams-api-freeze.json) and
[`eng/streams-formats.json`](../../eng/streams-formats.json).

Stable publication requires Provider 1.0.0 and the exact-candidate 72-hour
Streams endurance report, including replay, duplicate, lease, relay-restart,
retention, cancellation, and corruption recovery evidence.

Support starts only after `streams-v1.0.0` is tagged from the immutable
reviewed `main` candidate and its packages, hashes, provenance, smoke tests,
and Provider dependency have been verified. Candidate artifacts remain
test-only. Published 1.0.0 artifacts are immutable; defects use rollback or
pinning and a new fixed version.
