# BlueTusk Control Plane 1.0.0 release record

Status: published on 2026-08-23 from `control-plane-v1.0.0` at release commit
`7380d7b028c72b2aae348b778711d104d022a3f8`.

The complete release evidence, registry inventory, and explicit owner-accepted
exceptions are recorded in the
[V1 publication record](../releases/1.0.0-publication-record.md).

Control Plane 1.0.0 stabilises the versioned agent contract, authorised
inventory and operation endpoints, immutable audit storage, PostgreSQL
migrations, dashboard views, and production health and telemetry contracts.
Its public API and durable formats are frozen by
[`eng/control-plane-api-freeze.json`](../../eng/control-plane-api-freeze.json)
and
[`eng/control-plane-formats.json`](../../eng/control-plane-formats.json).

The planned standard publication gate required Provider, Streams, Sync, and
Live 1.0.0 plus exact-candidate governance, security, operational-rehearsal,
website, and approval evidence. The package dependencies were published first;
the owner exception records the incomplete external evidence.

Support starts with the immutable `control-plane-v1.0.0` packages. Registry
availability, contents, hashes, SBOMs, provenance, tests, and dependency
resolution passed in the recorded release workflow. Defects use rollback or
pinning and a new fixed version.

All three RC applications include a protected Control Plane surface and
operator runbook. RC dashboard and audit observations remain non-formal until
the stable governance, operational rehearsal, and approval evidence is bound
to the immutable candidate SHA.
