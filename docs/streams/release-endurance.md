# Streams release endurance

Streams 1.0 requires a completed 72-hour fault-injected PostgreSQL relay run. The executable gate is `StreamsRelayEnduranceTests.Relay_survives_fault_injected_endurance_without_loss_or_unbounded_storage`; the dedicated workflow is `.github/workflows/streams-release-endurance.yml`.

The harness continuously verifies:

- duplicate source append after a simulated feedback/process boundary returns `AlreadyPresent` with one retained transaction;
- delivery before group acknowledgement is replayed with the same sequence and stable transaction/change identities;
- stale compare-and-swap generations conflict without advancing a checkpoint;
- released group leases are fenced and newly acquired tokens increase;
- source leases turn over and relay instances are recreated from durable state;
- both independent groups observe every source transaction in order;
- retention keeps database storage under the configured hard limit; and
- final compaction reaches zero retained transactions and bytes after every group is caught up.

Successful execution writes a JSON evidence report containing requested and actual duration, transaction count, injected-event counts, maximum relay rows/bytes, and final storage bytes. The release evidence is the test result plus that report and PostgreSQL logs for any failure.

## Local smoke run

The endurance case is skipped unless `BLUETUSK_RELAY_ENDURANCE_DURATION` is explicitly set. A short run exercises the same code path without claiming the release gate:

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
$env:BLUETUSK_RELAY_ENDURANCE_DURATION = "00:00:15"
$env:BLUETUSK_RELAY_ENDURANCE_INTERVAL_MS = "0"
$env:BLUETUSK_RELAY_ENDURANCE_MIN_TRANSACTIONS = "260"
$env:BLUETUSK_RELAY_ENDURANCE_REPORT = "artifacts/test-results/streams-relay-endurance-smoke/report.json"
dotnet test tests/BlueTusk.StressTests -c Release --filter FullyQualifiedName~StreamsRelayEnduranceTests
```

The smoke report is diagnostic evidence only. It must not be described as the 72-hour release result.

## Release workflow

Dispatch **Streams 72-hour release endurance** and enter the exact confirmation `RUN-STREAMS-72-HOUR-ENDURANCE`. The job is fixed to `3.00:00:00`, a 250 ms pacing interval, and at least 100,000 transactions; changing those values invalidates the release gate.

The workflow targets a private runner carrying the labels `self-hosted`, `linux`, `x64`, and `bluetusk-endurance`. It needs the .NET 10 SDK, Docker with Compose, enough durable workspace for PostgreSQL WAL/logs, and monitoring outside the test process. GitHub-hosted jobs are limited to six hours, while self-hosted jobs can run for up to five days, so the 72-hour gate cannot truthfully run on the normal hosted CI pool; see GitHub's [Actions limits](https://docs.github.com/en/actions/reference/limits) and [self-hosted runner routing](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/use-in-a-workflow).

Do not mark the roadmap or release notes complete when the workflow is merely queued, skipped, cancelled, or smoke-tested. Completion requires a successful workflow result whose JSON report records at least 72 hours, at least 100,000 transactions, non-zero duplicate/replay/conflict/fencing/restart counters, bounded maximum storage, and zero final storage bytes.
