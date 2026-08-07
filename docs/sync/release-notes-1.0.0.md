# BlueTusk Sync 1.0.0 release record

Status: release-prepared, not published.

Sync 1.0.0 stabilises the mutation pipeline, transforms, quarantine and replay,
reconciliation and repair, rebuild and cutover, hosting, telemetry, and the
PostgreSQL, Redis, NATS, and OpenSearch destinations. The public API and
durable formats are frozen by
[`eng/sync-api-freeze.json`](../../eng/sync-api-freeze.json) and
[`eng/sync-formats.json`](../../eng/sync-formats.json).

Stable publication requires Provider and Streams 1.0.0 and an exact-candidate
24-hour Sync endurance report against digest-pinned destination images,
including disconnect, cancellation, replay, checkpoint, reconciliation, and
corruption recovery evidence.

Support starts only after `sync-v1.0.0` is tagged from the immutable reviewed
`main` candidate and registry availability, hashes, provenance, smoke tests,
and dependency resolution pass. Published 1.0.0 artifacts are immutable;
defects use rollback or pinning and a new fixed version.

Order Fulfilment Operations exercises the PostgreSQL read-model projection,
reconciliation, rebuild, and cutover runbooks from exact `1.0.0-rc.1`
packages. RC staging does not replace the exact-candidate 24-hour
multi-destination endurance gate.
