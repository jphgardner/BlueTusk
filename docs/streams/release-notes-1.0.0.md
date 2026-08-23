# BlueTusk Streams 1.0.0 release record

Status: published on 2026-08-23 from `streams-v1.0.0` at release commit
`7380d7b028c72b2aae348b778711d104d022a3f8`.

The complete release evidence, registry inventory, and explicit owner-accepted
exceptions are recorded in the
[V1 publication record](../releases/1.0.0-publication-record.md).

Streams 1.0.0 stabilises transaction-preserving CDC, typed and dynamic change
rows, snapshot-to-stream handoff, direct and durable-relay delivery, state
stores, CloudEvents, hosted integration, Aspire, testing helpers, and tooling.
The public API and durable formats are frozen by
[`eng/streams-api-freeze.json`](../../eng/streams-api-freeze.json) and
[`eng/streams-formats.json`](../../eng/streams-formats.json).

The planned standard publication gate required Provider 1.0.0 and the
exact-candidate 72-hour Streams endurance report. The Provider dependency was
published first; the owner exception records the deferred endurance evidence.

Support starts with the immutable `streams-v1.0.0` packages. Registry
availability, contents, hashes, SBOMs, provenance, tests, and Provider
dependency resolution passed in the recorded release workflow. Defects use
rollback or pinning and a new fixed version.

The Orders, Topology, and Fraud RC applications exercise durable relay,
replay/invalidation, worker restart, cancellation, and corruption/recovery
contracts from exact `1.0.0-rc.1` packages. These staging observations do not
replace the exact-candidate 72-hour gate.
