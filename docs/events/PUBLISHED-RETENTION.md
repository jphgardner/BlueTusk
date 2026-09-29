# Published outbox retention: required protocol

`ArchiveNextAsync` can already copy a published stream's immutable events into an external archive.
It never authorizes physical deletion. The current Events.Streams decoder rejects every outbox DELETE,
and a Projections source may include the same outbox table in its publication. A source slot position
only states how far that transport has acknowledged WAL; it does not prove that every target inbox,
projection version, independent subscription or recovery candidate has committed the event effects.
No source-only API can safely infer those remote obligations from the present contracts.

Published pruning needs a distinct, opt-in protocol, with the following durable order:

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
