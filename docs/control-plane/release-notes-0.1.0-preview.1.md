# BlueTusk Control Plane 0.1.0-preview.1 release notes

This preview contains the independently versioned `BlueTusk.ControlPlane` and
`BlueTusk.Dashboard` packages. Its implementation and compatibility gates are
complete. The product-family manifest remains non-publishable until its Sync
dependency completes and archives the required 24-hour endurance report; no
package publication is implied by these notes.

## Included

- coherent source, slot, WAL, relay, snapshot, consumer-group, checkpoint,
  Sync, Live, and Continuous Graph operational projections;
- authorised and HTML-safe dashboard pages;
- separately authorised, explicitly confirmed operation requests with immutable
  audit-before-mutation records;
- redaction of credentials, payloads, result rows, parameters, security scopes,
  exception messages, and other operator-sensitive values;
- discoverable version compatibility at `/api/capabilities`;
- stable v1 read and mutation routes under `/api/v1`;
- compatibility aliases for the original preview API routes; and
- a transactionally migrated PostgreSQL audit schema with persisted schema and
  record-format versions.

## Compatibility and upgrade

The v1 JSON envelope contains `contractVersion` and `data`. Compatible v1
releases may add fields, but must not remove existing fields or change their
meaning or JSON type. An incompatible contract requires a new route and
envelope version. Agents should query capabilities at startup and fail closed
when no supported version overlaps.

Run `PostgreSqlControlPlaneAuditStore.InitializeAsync` with a migration owner
before starting workers. The migration lock prevents concurrent initializers
from interleaving. Schema version 2 imports the legacy pre-metadata audit table,
preserves its rows, backfills record format 1, and restores the immutable
trigger and lookup index idempotently. A package refuses to initialize a newer
schema and refuses audit appends unless the stored schema version exactly
matches its own.

## Completed candidate checks

- zero-warning Control Plane .NET 10 Release build and public API baseline;
- 10/10 Control Plane unit tests;
- PostgreSQL 15, 16, 17, 18, and 19 live acceptance of fresh initialization,
  legacy upgrade, row preservation, append immutability, and future-version
  rejection;
- repository formatting and documentation-link checks; and
- inspected `BlueTusk.ControlPlane.0.1.0-preview.1.nupkg` and
  `BlueTusk.Dashboard.0.1.0-preview.1.nupkg`.

The whole-repository zero-warning build, offline regression, allocation,
vulnerability, and final commit-bound package checks remain required before
publication. They must run without competing with a long-running release gate
that owns shared test binaries. The release manifest and
[release-readiness document](../release-readiness.md) are the authoritative
publication state.
