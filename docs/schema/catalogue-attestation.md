# Catalogue consistency attestation

PostgreSQL catalogue rows in a held read-only repeatable-read transaction have a stable MVCC snapshot. Some metadata helpers also consult current catalogue caches. A live regression demonstrates that `pg_get_functiondef` can return a concurrently replaced body even though the held `pg_proc` row remains unchanged. Capture must not describe that mixture as a consistent snapshot.

The internal `PostgreSqlCatalogConsistencyAttestation` complements snapshot-derived metadata. Before materialization it reads catalogue tuple-version tokens in the held transaction. After every result has been materialized, it opens a distinct connection and a fresh read-only repeatable-read transaction, then compares the tokens and server identity. An internal mismatch becomes the public `SchemaConcurrentDdlException` at the capture boundary. `CaptureAsync` makes at most three fresh capture attempts (the initial attempt plus two retries), with 75 ms and 150 ms retry delays, under one overall configured deadline. It retries only consistency mismatches. Permission failures, timeouts, cancellation, unsupported servers and configured admission limits remain distinct and are not retried. `CaptureInTransactionAsync` does not retry: the caller-owned transaction remains unchanged and uncommitted, and its owner must start a fresh transaction after a mismatch.

The transient tokens contain catalogue kind, OID or composite row key, and tuple `xmin`. They never enter serialized contracts or canonical fingerprints. The conservative scope includes every row in `pg_class`, `pg_type`, `pg_enum`, `pg_constraint`, `pg_attribute`, `pg_index`, `pg_policy`, `pg_proc`, `pg_namespace`, `pg_publication`, `pg_publication_rel`, `pg_publication_namespace`, `pg_extension`, `pg_attrdef`, `pg_collation`, `pg_language`, `pg_rewrite`, `pg_inherits`, `pg_operator`, `pg_opclass`, `pg_opfamily`, `pg_am`, `pg_cast`, `pg_transform`, `pg_ts_config`, `pg_ts_dict`, `pg_database` and `pg_authid`. Deparsers can resolve dependencies outside the selected schemas, so unrelated catalogue changes may also reject capture.

Tuple versions detect an existing function or role changed and then reverted, even when its final textual definition or name matches the original. Matching token sets cannot detect an entirely new row inserted and deleted between the two observations. In particular, an ephemeral `pg_publication_rel` row can affect current-cache publication expansion without changing an existing `pg_publication` row. Publication membership therefore requires direct MVCC catalogue joins, not reliance on `pg_publication_tables` plus this attestation. Those joins capture configured explicit-table, schema or all-table membership; they do not claim cached runtime partition expansion. This helper is not a universal DDL serialization barrier: any additional cache-sensitive expansion must independently account for ephemeral dependencies or use snapshot-derived data.

Server identity includes PostgreSQL system identifier, database OID/name, backend address/port, postmaster start time, recovery state, server version and session/current role. A verification connection routed to another database, cluster, restarted server or different role fails closed. Postmaster start is compared as an epoch value, independent of caller time-zone/date formatting. PostgreSQL 15 or later is required. Lack of permission to read the identity also fails closed; there is no privileged fallback.

The attestation admits at most two million fixed-width tokens and charges 256 metadata bytes per token across the two retained observations and managed list growth, plus a fixed 1024-byte identity budget. Its working bound is the smaller configured relation/catalogue metadata bound. Each query has a SQL row limit that includes one overflow sentinel; overflow is rejected before appending the sentinel. A large database may exceed this conservative global catalogue bound even when the selected application schema is small.

For owned capture, one linked cancellation deadline starts before the first connection acquisition and covers all three possible attempts. The helper also links its token-read/materialization/verification deadline to that effective token. All intervening materialization must use `attestation.CancellationToken`, and verification uses that token for the independent pool wait, transaction, queries and completion. Per-command timeouts also apply. The pool must admit the held connection and one independent verification connection; a pool with maximum size one fails by the deadline. Borrowed connections and transactions are never committed, reset or disposed by the helper.

## Narrow permissions

The helper queries only `oid` and `xmin` from `pg_authid`; it never selects a credential column. Safe `pg_roles` names alone are not tuple-version evidence. An ordinary capture role needs the following grant **in each database being captured**:

```sql
GRANT SELECT (oid, xmin) ON pg_catalog.pg_authid TO schema_capture_role;
```

Although role data are shared, the catalogue relation/column ACL metadata for this grant are database-local. Do not replace this with a whole-table grant. Integration tests verify that this grant permits the two version columns while `has_column_privilege(..., 'rolpassword', 'SELECT')` and whole-table `has_table_privilege(..., 'SELECT')` remain false. The helper fails if the grant is missing.

The source identity also requires:

```sql
GRANT EXECUTE ON FUNCTION pg_catalog.pg_control_system() TO schema_capture_role;
```

The tested PostgreSQL 15–18 fixtures already allow this function through the default PUBLIC permission. An operator who revoked that permission must grant the function explicitly to the capture role. The helper does not install functions, grant privileges or modify catalogue settings. Its other reads require SELECT on the listed catalogue key/version columns and EXECUTE on the built-in identity functions; the tested fixtures expose those ordinary catalogue reads publicly.

## Evidence

`PostgreSqlCatalogAttestationTests` exercises actual PostgreSQL connections in uniquely named disposable databases. It covers current-cache routine drift, function replace/revert, shared-role cache rename/revert, narrow ordinary-role permissions, borrowed transaction settings/ownership, wrong-database verification, metadata admission, cancellation and a saturated pool deadline. Selected measured TRX artifacts are kept under `docs/schema/evidence/attestation`; fresh run output belongs under ignored `artifacts/schema-attestation`.

These checks establish the exercised consistency boundaries. They do not qualify every PostgreSQL deparser dependency, every failover/router topology or production endurance.
