# Durable-format support and rehearsal

Both products currently support durable schema format **1** only. There is no
previous released Jobs/Workflows format, format-two migration or downgrade
implementation. Definition versions inside a workflow are separate from the
store format; `MigrateAsync` does not migrate database storage formats.

`InitializeAsync` is deployment work. It serializes schema setup, checks the
persisted format and durable admission fingerprint/limits, and rejects a
mismatch. Do not start workers when initialization fails. Jobs persists payload
and history limits; Workflows persists its definition/input/result/signal/node/
history admission bounds. Operational batch, aggregate-read/claim and command
deadline tuning may change within the same durable limits. An incompatible
initializer does not automatically widen constraints or rewrite existing work.

The live `JobDurableFormatTests` and `WorkflowDurableFormatTests` rehearse a
format-one store reopening with different operational tuning, then restoring its
initial configuration. They preserve queued identity/deduplication, failed-attempt
history, fencing and completion. A workflow pauses after one committed activity,
reopens, receives a signal, rolls its process configuration back, and resumes its
timer/final activity without repeating the earlier activity; replay must match.

A separate live rehearsal exercises an actual supported definition boundary:
version one commits an activity and waits for a signal, migrates quiescently to
version two with an additional blocked node, reopens the store, then migrates
back to version one at the current revision. The completed activity contract,
result and original start/deduplication identity remain intact, the removed
blocked node never executes, and the signal finishes the original definition.
Stale-revision rollback is rejected and replay matches before and after rollback.
This definition-version rollback is only permitted while every node is blocked
or completed and every completed contract remains unchanged; it is distinct
from durable database-format or binary downgrade.

The rejection tests alter only a disposable schema's format marker to an
unsupported value, assert that initialization rejects it without rewriting
pending instances, restore the original marker, and continue normal processing.
They also reject changed durable admission bounds. Restoring a marker in a test
is **not** a valid downgrade procedure for a real migrated schema. The tests use
the same candidate binary and do not establish cross-release compatibility.

Run both full live suites through `eng/jobs-postgresql-compatibility.ps1` against
the owned disposable PostgreSQL fixtures. Retain the generated per-run TRX files
and summary, recording the actual server patch versions and zero skipped tests.
These tests establish same-format configuration/restart behavior and rejection
boundaries. A future format change still needs versioned migration tooling,
upgrade from an actual prior binary/database snapshot, old/new worker overlap
rules, interruption recovery, supported rollback decisions and immutable artifact
provenance. Until those exist, an operator should reject a mismatched deployment
and retain its compatible binary and database backup rather than edit headers.
