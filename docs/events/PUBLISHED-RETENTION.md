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
publication and the replication connection's `IDENTIFY_SYSTEM` timeline matches the marker. Generic
or relayed deliveries without this binding cannot produce a retention ACK.
The built-in single-publication snapshot source also reads the database and publication OIDs on
that **same logical WAL-sender session before slot creation**, then rechecks both OIDs and the
slot's `datoid`, plugin and timeline after every exported-snapshot reader finishes and before
`START_REPLICATION`. A command between exported-slot creation and snapshot import would invalidate
the exported snapshot. Those immutable session values travel with each delivery; Events and
Projections refuse a marker ACK when its database or publication OID differs. The operational
publication-DDL freeze remains necessary between the final check and replication start and while
the stream runs. Missing catalogue privilege or provenance fails closed.
Redelivery verifies the existing ACK byte-for-byte; a conflicting epoch fails.
The protected processor rejects an older or conflicting source transaction position, and an
unconfigured new processor refuses a control row. Target DB timeline, database and system drift
block subsequent marker ACKs. A same-lineage target rewind is not
provably detectable from the current target tables; deployments must rotate incarnation on every
restore, and a future deletion coordinator must verify that operational fence independently.

Projection schema v7 adds a separate **target-local preparation stage**. Capture the actual
single-publication source lineage and bind it before the projection snapshot. Supply that lineage
and a source membership registration to
`PostgreSqlProjectionStore.RegisterPublishedRetentionTargetAsync` for each tenant/stream and
projection version. An explicitly configured `StreamsProjectionConsumer.CreateProtected` requires
the built-in Streams delivery's verified single `START_REPLICATION` publication and the
replication connection's `IDENTIFY_SYSTEM` timeline. The store
intercepts the marker before application code, requires it to be the only raw change, and commits
the exact marker ACK with its projection checkpoint in one transaction under the same head/state
locks as promotion. The ACK records the version, definition fingerprint, bound source lineage,
snapshot epoch, target database incarnation and whether this commit was active, candidate or a
recovery candidate with its recovery ticket. Former recovery writers are fenced. Duplicate marker
delivery requires an exact existing ACK; a fresh snapshot whose checkpoint passes a marker cannot
manufacture one. A candidate ACK can be replayed after promotion for transport progress, but
retains its historical candidate role rather than becoming an active ACK. A subsequent
active marker must be processed under the active role. Unconfigured projections refuse a control
row. Source/target control-function privilege and frozen publication DDL are required.

These rows are **not source deletion authorization**. Source membership still supports only one
slot/publication lineage per stream, so independently slotted projection versions cannot yet join
the same protected set. `IDENTIFY_SYSTEM` does not return the source database OID; the same-session
catalogue read above binds it for the built-in single-publication snapshot source. The target
registration currently consumes a caller-supplied source
registration record; the observation API checks the actual source membership and target ACK across
databases, including current projection head, recovery ticket, snapshot epoch, target incarnation
and slot transport position. Any future deletion coordinator must reverify these mutable facts and
establish a target restore fence. A marker already included in a fresh snapshot
has no ordered target ACK, and recovery after a pruned prefix needs an explicit archive/bootstrap
proof or a newer marker. Independent subscriptions remain blocking obligations. Slot recreation
under the same name and a same-lineage target rewind remain unresolved fences.

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

Schema v5 adds append-only `published_retention_observations` and
`ObservePublishedRetentionIntentAsync` as a **non-authoritative, historical proof ledger**. A
trusted operator configuration supplies at most 64 remote target database connections and exact
member keys, schema, target kind, and projection version. The method accepts no caller-supplied
ACK contents. It requires the configured set to match every immutable source member, reads each
target's current system/database OID/timeline, registration, exact marker ACK and durable
effect checkpoint **and the separate Streams `stream_state` checkpoint** in a repeatable-read
remote snapshot. The transport state must live in that same target database, with its control
schema supplied explicitly, so the proof is read consistently. The method also checks the current projection head, state,
snapshot epoch and recovery ticket against the recorded active/candidate role. All targets must
agree on the marker commit-end LSN. It then locks the source stream row, rechecks the intent and
complete membership revision, publication coverage, source OIDs, slot `datoid`, a present
`restart_lsn`, and `confirmed_flush_lsn` at or beyond the marker and no further than each target
checkpoint. Only
then does it insert an append-only JSON evidence record and digest. Remote reads happen outside
the source lock. An absent endpoint, ACK, checkpoint or slot transport position fails closed.
The operator must treat endpoint keys and database credentials as trusted configuration. A remote promotion,
restore, failover, independent reader, archive outage or slot recreation can occur after the
snapshot and invalidate the observation. No code interprets this table as authorization; the
published-outbox deletion guard and old decoder refusal remain in force. Even a currently matching
transport row does not prove the slot will retain its incarnation or that a restored target will
honor its prior checkpoint at deletion time.

Schema v6 adds append-only `published_retention_endpoint_bindings`. The first successful remote
observation binds each immutable source member to the configured endpoint key, target kind,
schema, projection version (when applicable), and observed target system/database OID/timeline.
Later observations require an exact match; switching a connection to another target or silently
renaming an endpoint fails closed. Binding is written under the source stream lock in the same
transaction as the observation and rolls back if any member fails. The initial endpoint selection
remains operator-trusted, and a physical rewind that preserves target identity can still make an
old ACK appear current. These bindings are audit evidence, not authorization to prune.

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

This protocol still needs remote proof coordination, multi-lineage registration, independent
subscription coverage, restore procedures and mixed-binary rollout gates.
`AssessLocalRetentionAsync` therefore reports a published outbox as a blocker, regardless
of archive progress or a local replay checkpoint. Dropping a publication solely to pass that check
would abandon the protected consumers and is not a supported retention procedure.
