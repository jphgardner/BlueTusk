# Jobs and Workflows durable formats

Jobs and Workflows use database storage format 1. A workflow definition
version is separate from that storage format. `MigrateAsync` changes an
eligible workflow definition and does not change the database schema format.

## Initialize the store

Run `InitializeAsync` during controlled deployment. Initialization serializes
schema setup, checks the installed format and durable admission limits, and
rejects an incompatible store. Start workers only after initialization
succeeds.

Jobs persists payload and history limits. Workflows persists definition,
input, result, signal, node, and history limits. You can adjust operational
batch sizes, claim limits and command deadlines within the durable
configuration. Rehearse changes on a restored backup before starting the
updated worker.

## Migrate a workflow definition

Quiesce the instance before migrating. Any completed activity must keep its
completed contract and result, and active work cannot be replaced. Pass the
expected instance revision so a stale update cannot overwrite new work.
After migration, check that replay returns the same completed history and
that the new definition handles subsequent signals and timers.

## Recover from a format mismatch

Keep a backup and a binary compatible with the written format. Avoid editing
database format markers to force a downgrade. For incompatible changes,
restore a compatible backup or use the migration procedure supplied for that
format. See [upgrade Jobs](upgrade.md) for the worker rollout procedure.
