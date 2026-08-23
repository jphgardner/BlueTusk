# BlueTusk Sync 1.0.0 release record

Status: published on 2026-08-23 from `sync-v1.0.0` at release commit
`7380d7b028c72b2aae348b778711d104d022a3f8`.

The complete release evidence, registry inventory, and explicit owner-accepted
exceptions are recorded in the
[V1 publication record](../releases/1.0.0-publication-record.md).

Sync 1.0.0 stabilises the mutation pipeline, transforms, quarantine and replay,
reconciliation and repair, rebuild and cutover, hosting, telemetry, and the
PostgreSQL, Redis, NATS, and OpenSearch destinations. The public API and
durable formats are frozen by
[`eng/sync-api-freeze.json`](../../eng/sync-api-freeze.json) and
[`eng/sync-formats.json`](../../eng/sync-formats.json).

The planned standard publication gate required Provider and Streams 1.0.0 plus
an exact-candidate 24-hour Sync endurance report. The package dependencies were
published first; the owner exception records the deferred endurance evidence.

Support starts with the immutable `sync-v1.0.0` packages. Registry availability,
contents, hashes, SBOMs, provenance, tests, and dependency resolution passed in
the recorded release workflow. Defects use rollback or pinning and a new fixed
version.

Order Fulfilment Operations exercises the PostgreSQL read-model projection,
reconciliation, rebuild, and cutover runbooks from exact `1.0.0-rc.1`
packages. RC staging does not replace the exact-candidate 24-hour
multi-destination endurance gate.
