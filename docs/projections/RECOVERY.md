# Explicit source recovery and controlled DDL

Ordinary cutover rejects changes to bound catalogue/timeline lineage, including when the Streams
source identity happens to be unchanged. `PromoteWithLineageAsync` permits a different slot only
within the same verified source history. Numeric LSNs cannot establish coverage across timelines,
restores or independent systems. A logical slot created after this fixture's base backup was not
present on the promoted physical standby; resuming its old checkpoint would not be a certified recovery.
Raw Streams transactions do not carry a timeline attestation. A source reconnect must recapture and
validate lineage before starting deliveries; the sample does this on every worker start. Arbitrary
application code that fabricates/reinterprets a raw source identity cannot be certified by the destination
store. This contract does not provide automatic source failover/reconnection.

Schema version 6 adds durable operator recovery tickets and maintenance fences. These APIs deliberately
make recovery explicit. They do **not** certify that the authoritative replacement source contains
every previously acknowledged business write. That requires infrastructure evidence and an operator
decision. The recorded reason should identify the incident/deployment and its authoritative source.

For failover/restore recovery:

1. Fence the former primary externally before promoting/routing to the replacement. A destination
   lease on one isolated primary cannot prevent writes to another split-brain primary. Confirm source
   durability and backup/replication coverage separately; this fixture uses synchronous `remote_apply`.
2. Capture actual target lineage, register a **new empty version** with `RegisterWithLineageAsync`,
   and capture `ProjectionCutoverEvidence` from the target. Its barrier is emitted only after verifying
   the connected system/database/timeline. Use a fresh slot on the authoritative history.
3. Call `BeginRecoveryAsync(candidate, expectedActiveVersion, evidence, stableRecoveryId, reason)`.
   One pending ticket per projection is permitted. Under the publication lock it durably fences the
   predecessor's worker and future lease acquisition. The old read model remains published and readable.
4. Use the existing Streams exported-snapshot source to build the candidate. Its consistent position
   must be at or after the ticket's source barrier. On interruption, reopen the ticket with
   `ReadRecoveryAsync` and resume retained WAL or use the existing bounded snapshot reset protocol.
   Do not create a new ticket or replace its immutable candidate/lineage intent.
5. Capture fresh target cutover evidence, consume ordered retained WAL through its verified barrier,
   then call `CompleteRecoveryAsync(candidateLease, recoveryId, evidence)`. It validates the ticket,
   immutable source binding, fresh snapshot, completed table coverage, candidate checkpoint and DB-clock
   lease before atomic publication. It compares coverage only on the replacement history.
6. The predecessor is permanently retired on cutover. An exact repeated completion confirms the
   durable result after an ambiguous target response; `ReadRecoveryAsync(...).IsComplete` records it.
   Live emits a `SchemaChanged` reset; publisher takeover independently emits `ServerRestart` while
   retaining monotonic replay sequences. Prune retired derived rows in bounded calls only after the
   application's retention decision. Recovery/maintenance, version and Events dedup identities persist.

For a controlled source schema change, call
`FenceForMaintenanceAsync(name, expectedActiveVersion, stableMaintenanceId, reason)` **before DDL**.
It irreversibly stops the published worker/reacquisition while its prior model remains readable.
Execute the source DDL transaction using the controlled deployment role (PostgreSQL's DDL table locks
drain conflicting source DML); deploy a definition compatible with the new contract, then follow the
new-version recovery steps above. Source DML may continue if it remains compatible with the deployed
schema/application. The new exported snapshot and its retained WAL cover those commits. Old slot
history is never reinterpreted through the replacement contract. The sample and native test exercise
adding a published column followed by a fresh snapshot, updates and deletes.

Maintenance is not a reversible pause. A failed migration still requires a fresh version and recovery;
neither the old writer nor an ordinary promotion can bypass its fence. A ticket's candidate identity
and target lineage are immutable. Candidate code/contract replacement after a failed rebuild currently
requires a separately designed operator migration; there is no automatic ticket abandonment/rebinding.
Snapshot restart under the same correctly defined candidate is resumable and bounded.
All destination workers must use the schema-6 recovery protocol before maintenance/recovery starts.
Stop earlier binaries during this preview's schema migration: an already initialized earlier process
does not know the new acquisition predicates. Current clients fail closed on unknown schema versions,
but mixed-binary rolling maintenance and arbitrary SQL written outside this protocol are not certified.

Restrict table/publication DDL ownership to the controlled deployment role, and keep source DDL
immutable from target-lineage capture through snapshot and retained WAL. Raw CDC/current catalogue
evidence cannot detect an uncoordinated change-and-revert in historical DDL. This sequencing reduces
the deployment gap; it does not provide a transactional source-DDL history ledger or certify arbitrary
online migrations. Endpoint routing, split-brain prevention, recovery authorization, signing-key
distribution, failover slots, physical backup/PITR and broad crash/endurance campaigns remain required.

## Verify physical promotion

Fence the old primary, promote an eligible standby, and reconnect through
the deployment’s normal endpoint. Check that a previously applied event
does not create a duplicate row and that a stale lease cannot publish
progress. Compare the projection with its source from the last durable
checkpoint before resuming traffic. Restore or rebuild from the source if
a checkpoint refers to data that was lost during failover.
