# BlueTusk Control Plane 1.0.0 release record

Status: release-prepared, not published.

Control Plane 1.0.0 stabilises the versioned agent contract, authorised
inventory and operation endpoints, immutable audit storage, PostgreSQL
migrations, dashboard views, and production health and telemetry contracts.
Its public API and durable formats are frozen by
[`eng/control-plane-api-freeze.json`](../../eng/control-plane-api-freeze.json)
and
[`eng/control-plane-formats.json`](../../eng/control-plane-formats.json).

Stable publication requires Provider, Streams, Sync, and Live 1.0.0 plus the
exact-candidate governance, security, operational-rehearsal, website, and
approval evidence.

Support starts only after `control-plane-v1.0.0` is tagged from the immutable
reviewed `main` candidate and registry availability, hashes, provenance, smoke
tests, and dependency resolution pass. Published 1.0.0 artifacts are
immutable; defects use rollback or pinning and a new fixed version.

All three RC applications include a protected Control Plane surface and
operator runbook. RC dashboard and audit observations remain non-formal until
the stable governance, operational rehearsal, and approval evidence is bound
to the immutable candidate SHA.
