# Upgrade guide

BlueTusk product families are independently versioned. Upgrade the lowest
dependency first and keep package versions consistent with the family manifest.

## Before upgrading

Record:

- current and target BlueTusk versions;
- .NET and EF Core versions;
- PostgreSQL major/minor version;
- installed extension versions;
- persisted Streams/Sync/Live formats;
- database migrations to apply; and
- rollback target.

Read the relevant release notes and API/format compatibility document. Preview
versions may contain intentional breaking changes that a stable line would
forbid.

## Dependency order

The release/dependency order is:

1. Provider
2. Streams
3. Sync and Live
4. Control Plane
5. Continuous Graph preview

EF Core and extension packages must use compatible Provider-family versions.
Do not mix an arbitrary set of locally built packages because they share a
similar version string.

## Provider upgrade

Review:

- connection-string option changes;
- public API baseline changes;
- type inference and catalogue behavior;
- pooling/reset behavior;
- multiplexing or pipeline compatibility;
- authentication defaults; and
- NativeAOT/trimming annotations.

Run focused compatibility tests against every PostgreSQL major used by the
application.

## EF Core upgrade

Upgrade the matching EF Core runtime/design packages together. Before applying
migrations:

```powershell
dotnet ef migrations script --idempotent
```

Inspect changes to PostgreSQL-specific annotations, generated DDL, extension
requirements and transaction-suppressed operations. Re-run representative LINQ
queries and compiled queries.

## Extension upgrade

Extension packages combine client codecs/translations with a server extension.
Check both:

- BlueTusk package compatibility; and
- server extension upgrade requirements.

Do not assume an application package upgrade installs or upgrades the server
extension.

## Real-time format upgrade

Streams, Sync and Live publish format compatibility records. Before changing a
format or mapping:

1. identify every persisted checkpoint, relay segment and destination version;
2. confirm old and new readers/writers can coexist during rollout;
3. deploy readers before writers when the compatibility plan requires it;
4. retain rollback-compatible state; and
5. use a controlled snapshot/rebuild when no safe in-place transition exists.

Any change to source identity, schema fingerprint or mapping fingerprint must be
treated as a data migration, not merely a code deployment.

## PostgreSQL upgrade

For a major upgrade:

- run the BlueTusk live matrix against the target major;
- verify extensions are available at compatible versions;
- test authentication and TLS behavior;
- test query plans and migrations;
- rehearse logical replication/slot handling; and
- validate capability guards.

PostgreSQL 19 SQL/PGQ remains pre-GA-sensitive until the recorded GA programme
passes. Raw SQL remains the escape hatch for server syntax outside the
documented typed subset.

For a minor upgrade, still rehearse the production topology and failover path.
The endurance contracts include PostgreSQL minor-upgrade fault scenarios.

## Rolling upgrade

Use expand/migrate/contract:

1. **Expand** database and formats so old and new application versions can both
   operate.
2. **Migrate** traffic and persisted state while observing compatibility
   metrics.
3. **Contract** old schema/format support only after rollback is no longer
   required.

Do not change the exact release candidate after endurance or independent-review
evidence has been recorded; a code change creates a new candidate.

## Rollback

Rollback must account for:

- irreversible database migrations;
- newly written format versions;
- checkpoints advanced by new code;
- destination writes that old code cannot interpret; and
- package dependency downgrades.

When binary rollback cannot safely read new state, prefer a forward fix or a
documented rebuild from the authoritative source.

## Verification checklist

- Release build has zero warnings.
- Public API and format checks pass.
- Provider/EF/extension tests pass against the deployed PostgreSQL versions.
- Candidate packages contain the expected repository commit.
- Vulnerability audit and SBOM/provenance checks pass.
- Operational dashboards and alerts recognize the new version.
- Backup, restore and rollback were rehearsed.

See [API compatibility](../api-compatibility.md),
[release readiness](../v1-release-readiness.md), and
[release process](../release-process.md).
