# BlueTusk Live 1.0.0 release record

Status: release-prepared, not published.

Live 1.0.0 stabilises registered authorised query plans, gap-free initial
delivery, keyed diffs, replay and signed resume, security-scoped fan-out,
quotas, SignalR, server-sent events, gRPC, and the framework-neutral, Angular,
and React clients. The public API and durable formats are frozen by
[`eng/live-api-freeze.json`](../../eng/live-api-freeze.json) and
[`eng/live-formats.json`](../../eng/live-formats.json).

Stable publication requires Provider and Streams 1.0.0, exact-candidate
performance and recovery evidence, and the two-application pilot set. The
pilots collectively cover all six product families.

Support starts only after `live-v1.0.0` is tagged from the immutable reviewed
`main` candidate and NuGet/npm availability, hashes, provenance, smoke tests,
and dependency resolution pass. Published 1.0.0 artifacts are immutable;
defects use rollback or pinning and a new fixed version.
