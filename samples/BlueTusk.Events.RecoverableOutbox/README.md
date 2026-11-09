# Recover a transactional outbox after an interrupted checkpoint

Use a disposable development PostgreSQL database with logical WAL enabled
and a role allowed to create tables, a publication, and a logical slot.
The sample uses one database for source and target, with only the outbox
published. Run setup once in an empty database before publishing events.

```powershell
$env:BLUETUSK_OUTBOX_CONNECTION_STRING = '<development connection string>'
$project = './samples/BlueTusk.Events.RecoverableOutbox'
$eventId = [guid]::NewGuid()
dotnet run --project $project -- init
dotnet run --project $project -- publish $eventId
dotnet run --project $project -- consume --fail-before-checkpoint
dotnet run --project $project -- consume
```

The first consumer exits with code 75 after the target transaction commits
and before the checkpoint is saved. The second consumer receives the event
again and reports one received event and zero newly committed effects.
Query `SELECT count(*) FROM outbox_recipe.order_effects` to verify that one
effect remains. Publish another event with a new GUID and run `consume`
again to verify forward progress from the saved checkpoint.

To exercise the restart contract automatically, build the recipe once and run
the verifier against a fresh disposable database. It launches a separate
process for each operation, verifies that interruption leaves the checkpoint
at zero, and checks that replay advances it without repeating the effect.

```powershell
pwsh ./samples/BlueTusk.Events.RecoverableOutbox/verify-restart.ps1 -RecipeDll '<path to the built recipe DLL>'
```

The publisher commits the order row and event together. The Events delivery
processor commits the durable inbox identity with its database effect, then
lets the Streams observer persist the checkpoint and send feedback to
PostgreSQL. Replay uses the same slot, publication, and consumer group. Keep
their identities and the inbox until the recovery window closes. A real
process death can leave the Streams lease occupied for up to one minute.

Use the supplied connection and transaction for handler effects. An HTTP call
cannot share the database transaction. For an external effect, record a
follow-up outbox entry in the handler transaction and use a stable idempotency
key at the external destination.

After the exercise, remove the disposable database and its
`outbox_recipe_slot` through your local PostgreSQL administration workflow.
Retained logical slots keep WAL until they advance or are removed.

The Events integration contracts cover handler rollback, target commit
failure, and acknowledgment failure followed by redelivery.

```powershell
dotnet test tests/BlueTusk.Events.Tests -c Release --filter FullyQualifiedName~EventStreamsDeliveryTests
```
