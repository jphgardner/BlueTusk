# Published outbox retention: required protocol

`ArchiveNextAsync` can already copy a published stream's immutable events into an external archive.
It never authorizes physical deletion. The current Events.Streams decoder rejects every outbox DELETE,
and a Projections source may include the same outbox table in its publication. A source slot position
only states how far that transport has acknowledged WAL; it does not prove that every target inbox,
projection version, independent subscription or recovery candidate has committed the event effects.
No source-only API can safely infer those remote obligations from the present contracts.

Published pruning needs a distinct, opt-in protocol, with the following durable order:

The Events schema v3 adds `published_retention_intents` and
`PostgreSqlEventStore.PublishRetentionIntentAsync` as a **preparation stage only**. The method
re-reads at most 256 archive segments (64 by default) and 64 MiB of total event payload while
holding the stream and relation locks, verifies their contiguous sequence ranges,
counts and immutable event digests, and hashes their manifest identities into a chained digest. It
then inserts an append-only row into the same PostgreSQL transaction log as the outbox. The row
records a unique epoch, source system identifier, database and OID, timeline, logical slot,
publication and OID, and exact tenant/stream range. A prior intent must end at the caller's expected
sequence and have the same lineage. The method checks the connected PostgreSQL control identity and
logical slot, and refuses to emit a marker unless **every** publication currently containing the
outbox also publishes complete, unfiltered inserts from the control relation. Deployment must add
that relation to explicit outbox publications before using the API and **freeze publication DDL**
throughout the protocol; a schema-wide or all-tables publication covers it automatically. Archive
read latency extends the lock hold, so operators should use small ranges and keep the archive read
service responsive. The SQL role needs
access to `pg_control_system()` and `pg_control_checkpoint()`; missing privilege fails closed.

Schema v4 adds append-only `published_retention_members` and
`RegisterPublishedRetentionConsumerAsync`. Register each protected consumer group and exact target
incarnation against a tenant/stream and fresh source system, database OID, timeline, logical slot,
publication OID. Registration and intent emission serialize on the stream row. Every new member
increments an irreversible membership revision; the intent records the revision it observed and
requires at least one registered member. Re-registering the same incarnation with different lineage
fails. A later member makes an older marker's revision stale for any future source coordinator. The
API has no member-removal or incarnation-replacement operation. A restored target must use a new
incarnation and register it at the source; previous acknowledgements cannot be reused.
The current stage accepts only one registered source/slot/publication lineage per stream; a second
lineage is rejected until a multi-lineage marker and acknowledgement contract exists.

`RegisterPublishedRetentionTargetAsync` binds that source registration to the target database's
current system identifier, database OID and timeline. An explicitly configured upgraded
`PostgreSqlEventDeliveryProcessor` reads the control insert from its ordered Streams transaction,
requires it to be the transaction's only raw change (including otherwise ignored published tables),
validates every proof field, and commits an exact epoch/range/digest/lineage/target-incarnation ACK with
its target source checkpoint and inbox effects in one target transaction. It then acknowledges the
Streams delivery. Protected delivery also requires evidence from the built-in consistent-snapshot
source that the actual `START_REPLICATION` publication set contained exactly the source registration's
publication. Generic or relayed deliveries without this binding cannot produce a retention ACK.
Redelivery verifies the existing ACK byte-for-byte; a conflicting epoch fails.
The protected processor rejects an older or conflicting source transaction position, and an
unconfigured new processor refuses a control row. Target DB timeline, database and system drift
block subsequent marker ACKs. Projections, independent subscriptions and recovery candidates do
not yet produce these ACKs and remain blocking obligations. A same-lineage target rewind is not
provably detectable from the current target tables; deployments must rotate incarnation on every
restore, and a future deletion coordinator must verify that operational fence independently.

This marker proves that the archived prefix was readable and matched committed manifests when the
marker was emitted. It does **not** prove continuing archive availability, delivery to a subscriber,
target transaction commit, target incarnation, projection candidate, or transport checkpoint. Nor
does PostgreSQL bind a logical slot to the publication named in an intent, and one slot/publication
lineage cannot represent all protected readers. No source coordinator consumes these target ACKs
as retention authorization, advances a published floor or authorizes a DELETE. Existing
`AssessLocalRetentionAsync`, `AdvanceLocalRetentionAsync`, and `PruneRetainedAsync` still block a
published outbox, and the existing Streams decoder still rejects outbox deletes, updates and
truncates. Old binaries may ignore the new control row, which is safe only while deletion stays
disabled. Mixed-binary rollout must not enable published deletion until every protected consumer
recognizes, persists and validates the control record. A source restore onto a different timeline
or a slot/publication replacement requires fresh proof; a prior intent is never a transferable
acknowledgement.
The current contracts cannot detect a logical slot dropped and recreated under the same name.
That missing slot-incarnation fence is a separate proof gap before published source deletion can
be considered, even when source and target ACK rows otherwise agree.

1. Register every protected consumer group, target incarnation, projection version/candidate and
   recovery source against an immutable source identity and publication/slot lineage. Registration
   must be fenced against concurrent retention, and unregistered readers must be explicitly outside
   the guarantee. A new consumer starting below the eventual floor must use archive replay or a
   complete fresh snapshot with an explicit bootstrap checkpoint.
2. Archive a contiguous tenant/stream prefix, verify each immutable object by readback, and retain
   the permanent `(tenant, event ID)` identity ledger. The archive manifest needs a source lineage,
   sequence range, count and digest. Archive availability must be revalidated before any destructive
   step and monitored afterward; a source database restore without its corresponding objects is not
   a usable historical restore.
3. Commit an append-only retention intent/control record **after** the archived prefix, on the same
   ordered logical source seen by all protected consumers. It must identify the tenant/stream, exact
   inclusive sequence floor, archive manifest digest, source incarnation and unique retention epoch.
   The control relation must be present in every relevant publication from the outset. Merely writing
   a marker into the source database does not imply anyone received it.
4. Each Events.Streams or Projections target consumes that control record in source order and commits
   an acknowledgement in the same target transaction as its effects/checkpoint. The acknowledgement
   binds the exact source/epoch/range and target incarnation. Redelivery must be idempotent; ambiguous
   acknowledgement must not advance source retention. Targets that were rebuilt or restored must
   re-establish proof from their current incarnation instead of reusing a pre-restore acknowledgement.
5. The source coordinator waits for every registered obligation. It verifies durable target
   acknowledgements and a source transport checkpoint covering the marker's commit position, freezes
   membership while advancing a per-stream published floor, and refuses stale source timelines,
   changed publication membership, incomplete archive proof or lagging recovery candidates. A target
   that cannot certify the range blocks pruning rather than being silently dropped from the set.
6. Only upgraded decoders may interpret bounded outbox DELETE changes at or below their previously
   acknowledged floor as maintenance. The old decoder must keep rejecting deletes, updates and
   truncates. The new decoder must validate tenant, stream, sequence, source identity and retention
   epoch, and reject a delete above the floor or without the ordered control record. Source deletion
   itself is bounded by row count and commits in restartable batches; it never deletes identity,
   inbox, replay or retention-control tombstones. Rollout must upgrade and prove all protected
   consumers before enabling the first delete.

This protocol needs coordinated changes to Streams delivery, Projections definitions/recovery,
consumer registration, restore procedures and mixed-binary rollout gates. Those contracts do not
exist yet. `AssessLocalRetentionAsync` therefore reports a published outbox as a blocker, regardless
of archive progress or a local replay checkpoint. Dropping a publication solely to pass that check
would abandon the protected consumers and is not a supported retention procedure.
