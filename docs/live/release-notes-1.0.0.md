# BlueTusk Live 1.0.0 release record

Status: published on 2026-08-23 from `live-v1.0.0` at release commit
`7380d7b028c72b2aae348b778711d104d022a3f8`.

The complete release evidence, NuGet/npm inventory, and explicit owner-accepted
exceptions are recorded in the
[V1 publication record](../releases/1.0.0-publication-record.md).

Live 1.0.0 stabilises registered authorised query plans, gap-free initial
delivery, keyed diffs, replay and signed resume, security-scoped fan-out,
quotas, SignalR, server-sent events, gRPC, and the framework-neutral, Angular,
and React clients. The public API and durable formats are frozen by
[`eng/live-api-freeze.json`](../../eng/live-api-freeze.json) and
[`eng/live-formats.json`](../../eng/live-formats.json).

The planned standard publication gate required Provider and Streams 1.0.0,
exact-candidate performance and recovery evidence, and the two-application
pilot set. The package dependencies were published first; the owner exception
records the deferred performance, recovery, and pilot evidence.

Support starts with the immutable `live-v1.0.0` packages. NuGet/npm availability,
contents, hashes, SBOMs, provenance, tests, and dependency resolution passed in
the recorded release workflow. Defects use rollback or pinning and a new fixed
version.

The V1 RC applications cover all three browser surfaces: Orders uses the React
adapter, Service Topology uses the Angular adapter, and Fraud uses the
framework-neutral client directly. Their BFF sessions, replay/resume, fan-out,
and browser journeys are staging evidence, not either formal stable pilot.
