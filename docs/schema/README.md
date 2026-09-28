# BlueTusk Schema

`BlueTusk.Schema` is a new preview product for consistent PostgreSQL relation
contracts and consumer compatibility analysis. The caller owns the data source.

```csharp
var capture = new PostgreSqlSchemaCapture(dataSource, new SchemaCaptureOptions
{
    Schemas = ["app"],
    MaximumRelations = 10_000,
    MaximumColumns = 200_000,
    MaximumMetadataBytes = 64 * 1024 * 1024,
});
var before = await capture.CaptureAsync(cancellationToken);
// Capture again following a separately reviewed migration.
var after = await capture.CaptureAsync(cancellationToken);
var comparison = SchemaCompatibility.Compare(before, after);
var impacts = SchemaCompatibility.AnalyzeConsumers(comparison,
    [new SchemaConsumerContract("orders-live", new("app", "orders"), ["id", "status"])]);
```

Relation discovery uses one read-only repeatable-read transaction and an explicit
`pg_catalog` search path. Schema names are parameters. Row counts and total
metadata bytes are bounded; crossing a limit fails instead of returning a
silently partial snapshot. Command deadlines and cancellation apply to every
discovery operation. Snapshot inputs are copied into immutable collections.
PostgreSQL catalogue deparsers can consult newer caches during concurrent DDL;
relation format 1 alone does not attest that boundary. Use the expanded catalogue
capture below when validating a deployment contract under concurrent DDL.

The canonical SHA-256 fingerprint excludes transient OIDs. It includes relation
kind, column ordinals/types/nullability/defaults/generated and identity behavior,
collation, keys and constraints, index definitions/validity, RLS flags and
policies, and view/partition definitions. Collection order is normalized and
fields are length delimited. Schema metadata can contain sensitive default or
policy expressions; fingerprints do not encrypt it, and the library does not
log those expressions. Caller-created PostgreSQL type strings are metadata
labels; capture obtains authoritative type names from the database.

`SchemaSnapshotSerializer` exports source-generated, versioned JSON and verifies
the canonical fingerprint on import. It rejects future versions, malformed
Unicode/identifiers/catalogue flags, null members, excessive strings, collection
width and aggregate metadata before materializing an object graph. Constructors
bound enumeration before sorting. Optional `SchemaSnapshotLimits` adjusts the
document admission contract; serialized output remains capped at 64 MiB and can
be given a smaller exact byte bound. Identifier validation uses PostgreSQL's
standard 63-byte UTF-8 identifier limit. Bounded pooled writer slack is separate
from the exact output limit.

Compatibility reports are conservative. Type, nullability and removal changes
are incompatible; added non-null columns without a generated/default value can
break writes. Defaults, collations, policies, constraints and view definitions
require review. Consumer contracts filter unrelated column changes; a whole-row
contract observes every relation change. This classification does not prove an
arbitrary migration safe or replace testing the affected application.

Index replacement/retirement, Control Plane integration, broader PostgreSQL type and dependency coverage,
performance qualification and release evidence remain required expansion work. See the full
[implementation programme](../ecosystem/implementation-programme.md).

## Command-line snapshots and drift

The `BlueTusk.Schema.Tool` candidate installs as `bluetusk-schema`. Database
commands use `BLUETUSK_SCHEMA_CONNECTION_STRING`; credentials are not accepted
as command-line arguments or printed in errors.

```text
bluetusk-schema capture --schemas app,public --output schema.json
bluetusk-schema compare --before before.json --after after.json --output changes.json
bluetusk-schema check --baseline schema.json --schemas app,public --output drift.json
```

Capture writes bounded source-generated snapshots with atomic replacement.
Comparison emits change identities/categories without policy/default SQL.
`check` rejects any drift, including additive changes. Exit codes are 0 for
success, 1 for invalid input/operation, 2 for incompatibility or strict drift,
and 3 for a comparison requiring review. Snapshot reads and command deadlines
are bounded; a failed check preserves any prior report. The CLI never executes
migrations. Four tests include real PostgreSQL capture and additive drift,
tampered fingerprints, atomic report output and invalid option admission.

## Expanded catalogue contracts

```csharp
var catalogueCapture = new PostgreSqlSchemaCatalogCapture(dataSource, new()
{
    Relations = new() { Schemas = ["app"] },
    Limits = new() { MaximumEntries = 100_000, MaximumMetadataBytes = 64 * 1024 * 1024 },
});
var catalogue = await catalogueCapture.CaptureAsync(cancellationToken);
var bytes = SchemaCatalogSerializer.Serialize(catalogue);
var restored = SchemaCatalogSerializer.Deserialize(bytes);
```

`SchemaCatalogSnapshot` is a separate format-1 envelope containing the existing
relation snapshot. It adds ordered enum labels (including empty labels/types),
domain base types/nullability/defaults/collations/check and not-null constraints,
overloaded function/procedure/window-function contracts, current object/column
ACL entries and ownership, extension versions and configured publication membership. Routine
identity uses argument types without parameter names. Enum additions require
review; removing or reordering existing labels is incompatible. Routine bodies,
domain expressions and publication filters are sensitive metadata and are never
included in CLI change reports.

Object ACLs expand PostgreSQL's default object ACL when no explicit ACL exists;
synthetic `OWNER` entries preserve schema, relation, routine, type and scoped
publication authority even when an explicit ACL is empty. They are not a complete
effective-permission calculation. Role attributes (including SUPERUSER/BYPASSRLS), inheritance,
database-level permissions, future-object default-privilege templates and
aggregate/composite/range type definitions are not represented yet. Selected
publication membership is read from MVCC catalogue rows, including table, schema
and all-table declarations and their flags. Explicit partition roots remain
roots; this contract does not expand cached runtime partition routing.

Capture materializes in one read-only repeatable-read snapshot, then checks
bounded global catalogue tuple versions and server identity using an independent
fresh connection. OIDs/tuple versions are transient verification data and do not
enter the canonical fingerprint. Existing-row changes/reverts, role changes,
source routing and server restart reject the capture. Raw MVCC publication joins
avoid the transient add/drop cache gap. See the precise
[attestation contract and narrow permissions](catalogue-attestation.md).

The pool must admit two simultaneous capture connections. The configured command
timeout also bounds the whole owned capture, including pool waits and up to three
fresh attempts after `SchemaConcurrentDdlException`. Permission, admission and
cancellation failures are not retried. A caller-owned transaction must already
be read-only, repeatable-read/serializable, use exactly `pg_catalog` as its search
path and have the same role/source as verification. It remains borrowed and
uncommitted. A mismatch requires its owner to start a fresh transaction. Global
catalogue bounds and unrelated DDL can conservatively reject a small selected
schema; this is not a universal DDL serialization barrier.

```text
bluetusk-schema capture-catalog --schemas app,public --output catalog.json
bluetusk-schema compare-catalog --before before.json --after after.json --output changes.json
bluetusk-schema check-catalog --baseline catalog.json --schemas app,public --output drift.json
```

These commands preserve the existing exit-code, bounded input/output and atomic
report replacement contracts. A changed enum/domain/routine/ACL/publication can
fail strict drift checking while the embedded relation fingerprint remains the
same. Unknown format versions, unknown fields, malformed members and tampered
fingerprints fail before a usable contract is returned.
Supplemental/domain/relation array widths and structural metadata charges are
checked before JSON constructs DTO graphs; nested relation metadata has its own
subtotal. Capture bounds text after explicit UTF-8 conversion inside PostgreSQL,
including databases with another server encoding. Relation discovery receives
the stricter nested model/string/metadata limits before reading catalogue rows.

## Deployment sequencing

```csharp
var plan = SchemaDeploymentPlan.Create(beforeCatalogue, afterCatalogue,
[
    new("orders-projection", [new("relation", new("app", "orders"))]),
    new("status-consumer", [new("type", new("app", "order_status"))]),
]);
var progress = plan.Begin();
var ready = progress.ReadySteps;
```

The deterministic dependency graph orders baseline verification, review
approval, compatible consumer deployment, delivery fencing, additive expansion,
reviewed changes, rebuilds, target verification, cutovers and old-version
retirement. Whole-relation consumers observe every relation change; named column
dependencies ignore unrelated column changes. Dependencies are explicit; the
library does not infer a complete consumer graph from arbitrary SQL.
Policy/constraint/index and object authority changes affect named column
consumers conservatively; schema authority changes affect dependencies in that
schema. Extension relocation preserves the old dependency identity as well as
the new one.

`CompleteStep` rejects missing prerequisites and incorrect baseline/target/review
fingerprints. Independent consumer steps can complete in either order; repeated
completion is idempotent. State is immutable, with persistent completion sets;
planning indexes changed members rather than repeatedly scanning all changes.
Admission bounds consumers to 10,000, total declared dependencies to 100,000 and
aggregate input metadata to 64 MiB. Affected delivery is fenced before any source
DDL, including additive expansion. Null wildcard members and empty zero-argument
routine members have distinct fingerprint encodings.

The planner does not authorize or execute an operation. A PostgreSQL coordinator
can bind an exact reviewed action manifest to this graph and journal its
execution:

```csharp
var definition = new SchemaDeploymentDefinition(plan, plan.Steps.Select(step =>
    new SchemaDeploymentAction(step.Id,
        step.Phase == SchemaDeploymentPhase.Expand
            ? SchemaDeploymentActionKind.TransactionalSql
            : SchemaDeploymentActionKind.External,
        step.Phase == SchemaDeploymentPhase.Expand
            ? "ALTER TABLE app.orders ADD COLUMN source text"
            : "host operation and attestation: " + step.Id)));
var coordinator = new PostgreSqlSchemaDeploymentCoordinator(dataSource);
await coordinator.InitializeAsync(cancellationToken);
await coordinator.RegisterAsync("release_2026_09", definition, cancellationToken);
var lease = await coordinator.AcquireAsync("release_2026_09", definition,
    "schema_worker_a", TimeSpan.FromMinutes(2), cancellationToken);
```

The definition requires exactly one bounded action per step. Its SHA-256
fingerprint covers the plan fingerprint, action kind, step identity and exact
action text digest. Registration persists the plan and definition fingerprints;
reusing a deployment ID with different input fails. The caller reviews the SQL
and supplies an authorization policy. SQL and journal tables are accessible to
the configured database role, so deploy using a dedicated role with only the
required target privileges. Credentials, SQL text and operation descriptions are
not stored in the journal; only fingerprints and evidence digests are stored.

`AcquireAsync` uses the PostgreSQL clock and increments a persistent fencing
token. A second owner cannot acquire an unexpired lease. `RenewAsync` extends
only the current token. Every step mutation locks and validates that row. Lease
expiry or a newer token rejects the old owner. The lease is capped at five
minutes and each method has a bounded command/pool-wait deadline. A transaction
holding the lease row blocks takeover until it commits or rolls back; SQL
execution checks expiry again immediately before journaling completion.

For an external operation, `BeginExternalStepAsync` writes a pending attempt
after checking journaled prerequisites. **Only `IsNew == true` authorizes the
host to start that attempt.** A returned pending attempt with `IsNew == false`
has an unknown outcome: inspect the external target and either call
`CompleteExternalStepAsync` with a lowercase SHA-256 evidence digest, or call
`ReconcileNotAppliedAsync` with evidence that no effect occurred. The latter
preserves the prior attempt and permits a new numbered attempt. A new worker can
resolve a pending attempt after obtaining a newer lease, but a stale worker
cannot. Baseline, review and target steps also require the exact observed
catalogue/plan fingerprint. The host must capture and retain those observations;
this API validates the supplied fingerprint, not an independently captured
catalogue. External targets must enforce the fencing token or an equivalent
idempotency rule. The journal alone cannot make a cutover or service deployment
exactly once.

`ExecuteTransactionalSqlStepAsync` accepts only Expand or ApplyReviewedChanges
actions. It runs the exact bound command through a coordinator-owned BlueTusk
connection and transaction, prepares it with PostgreSQL's extended protocol so
multiple statements are rejected, and commits DDL and journal completion
together. It sets `search_path` to `pg_catalog`; object names in reviewed SQL
must be qualified. SQL must begin with a DDL verb. It is still privileged SQL:
review extensions, functions, grants and destructive changes before binding
the manifest. A retry after an unknown commit reads the durable completed
record; a rolled-back transaction leaves no completed record. SQL that cannot
run in a transaction, especially `CREATE INDEX CONCURRENTLY` and `DROP INDEX
CONCURRENTLY`, belongs in an external action with explicit physical index
verification and reconciliation. Concurrent index build failures can leave an
invalid index that must be inspected before retrying.

`ReadStepAsync` exposes the last numbered attempt for operational recovery.
Attempts are append-only, bounded to 1,000 per step. The coordinator does not
serialize arbitrary DDL by other roles, check the live baseline/target itself,
or replace Projections recovery tickets, physical database durability policy,
application authorization, rollback rehearsal or a human migration review.

## Concurrent index additions

`SchemaConcurrentIndexPlan.Create(beforeCatalogue, afterCatalogue)` accepts up
to 1,024 added, valid indexes on ordinary tables and leaf partitions, with a
32 MiB aggregate declaration bound. It rejects changed or removed indexes,
invalid target indexes, partitioned index parents, duplicate names within a
schema and definitions that are not captured `CREATE INDEX` statements. For an
index-only change, the deployment graph now includes review approval and an
Expand step. Bind that step as an external action using the index plan's exact
`ActionContent`; all other steps still need their own reviewed actions.

```csharp
var indexPlan = SchemaConcurrentIndexPlan.Create(beforeCatalogue, afterCatalogue);
var definition = new SchemaDeploymentDefinition(plan, plan.Steps.Select(step =>
    new SchemaDeploymentAction(step.Id, SchemaDeploymentActionKind.External,
        step.Phase == SchemaDeploymentPhase.Expand
            ? indexPlan.ActionContent : "host operation: " + step.Id)));
// After registering and acquiring a lease, complete baseline and review approval.
var pending = await coordinator.BeginExternalStepAsync(lease, definition,
    "expand", cancellationToken);
var deployer = new PostgreSqlConcurrentIndexDeployer(dataSource, coordinator,
    maximumBuildSeconds: 3600);
var evidenceSha256 = await deployer.ReconcileAndApplyAsync(indexPlan, lease,
    definition, "expand", pending, TimeSpan.FromMinutes(2), cancellationToken);
```

The deployer and coordinator require the same `BlueTuskDataSource` instance so
that index inspection/building and the durable journal use the same configured
database endpoint.

The deployer verifies the exact index schema/name, owning table,
`pg_get_indexdef` output and `pg_index` valid/ready/live flags before and after
each build. Existing valid, matching indexes are skipped; absent names are
built with a standalone, prepared `CREATE INDEX CONCURRENTLY` statement. It
never uses `IF NOT EXISTS` as a substitute for verification. The explicit
`ReconcileAndApplyAsync` entry point can resume a pending attempt after a
process death: each named index is physically inspected, and only absent names
are built. It records a digest of the verified physical state through the
coordinator after all declared indexes validate. No concurrent index build and
journal write can share a PostgreSQL transaction, so an unknown command result
remains pending until this physical reconciliation succeeds. The maximum build
duration is explicit (1 second to 24 hours); the deployer renews the
database-clock lease while a build is running. Provision at least two pool
connections for the build and renewal, in addition to application load.

An invalid or unfinished same-name index raises
`SchemaConcurrentIndexInvalidException`; a different table or definition
raises `SchemaConcurrentIndexDriftException`. The deployer does not drop or
replace either object. An operator must inspect the catalog, decide whether
to drop/rebuild, and retry under a live fenced lease. A failed concurrent
unique build can leave an invalid index that still affects writes. Changed or
removed indexes, constraint attachment, partitioned parent indexes and
rolling application cutovers require separate reviewed actions. Long-running
snapshots may delay index validation, and the build increases CPU and I/O even
while ordinary writers can continue. See PostgreSQL's
[concurrent build and invalid-index behavior](https://www.postgresql.org/docs/18/sql-createindex.html)
and [index validity flags](https://www.postgresql.org/docs/18/catalog-pg-index.html).

On initialization, the coordinator checks a persistent format marker, exact
journal column types/nullability and primary-key shape. Missing or partially
recreated journal tables fail closed. This guards accidental format drift; a
database superuser can still alter both data and format marker. The journal
does not supersede independent target-catalogue verification or source routing
and physical durability policy.
