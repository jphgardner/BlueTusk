# Streams release endurance

Streams 1.0 requires a completed 72-hour fault-injected PostgreSQL relay run.
The executable runner is `eng/run-streams-endurance.ps1`; the dedicated
workflow is `.github/workflows/streams-release-endurance.yml`.

The harness continuously verifies:

- duplicate source append after a simulated feedback/process boundary returns `AlreadyPresent` with one retained transaction;
- delivery before group acknowledgement is replayed with the same sequence and stable transaction/change identities;
- stale compare-and-swap generations conflict without advancing a checkpoint;
- released group leases are fenced and newly acquired tokens increase;
- source leases turn over and relay instances are recreated from durable state;
- both independent groups observe every source transaction in order;
- retention keeps database storage under the configured hard limit; and
- final compaction reaches zero retained transactions and bytes after every group is caught up.

The runner refuses to start unless the PostgreSQL endpoint is explicit and the
launch repository has no tracked changes. It creates a detached Git worktree
at the recorded source commit, restores and builds the stress project there,
and fingerprints every Release test artifact before and after the run. Other
repository builds and commits therefore cannot replace the binaries under
test.

The format-1 JSON report contains the exact source commit and branch, isolated
start/end commits and cleanliness, combined SHA-256 start/end artifact hashes,
artifact count, exact candidate package hashes and provenance hash, the
digest-pinned PostgreSQL image, worktree cleanup, requested and actual duration,
transaction and injected-event counts, maximum relay rows/bytes, final storage
bytes, and host/runtime identity. Restore, build, test, harness-report,
source-integrity, artifact-integrity, and cleanup failures are distinguished by
`failedPhase`.

`eng/verify-streams-endurance-report.ps1` is the fail-closed evidence reader.
It checks the expected commit, format, duration, transaction count, all
fault-injection counters, the 64 MiB storage ceiling, zero final retained
storage, clean isolated source, immutable Release artifacts, and worktree
cleanup.

## Local smoke run

The endurance case is skipped unless `BLUETUSK_RELAY_ENDURANCE_DURATION` is explicitly set. A short run exercises the same code path without claiming the release gate:

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
./eng/run-streams-endurance.ps1 `
  -Duration '00:00:15' `
  -MinimumTransactions 260 `
  -IntervalMilliseconds 0 `
  -ReportPath 'artifacts/test-results/streams-relay-endurance-smoke/report.json'
```

Validate the smoke with the same fail-closed reader:

```powershell
./eng/verify-streams-endurance-report.ps1 `
  -ReportPath 'artifacts/test-results/streams-relay-endurance-smoke/report.json' `
  -RequiredDuration '00:00:15' `
  -MinimumTransactions 260 `
  -ExpectedCommit (git rev-parse HEAD)
```

The smoke report is diagnostic evidence only. It must not be described as the 72-hour release result.

## Release workflow

Dispatch **Streams 72-hour release endurance** and enter the exact confirmation `RUN-STREAMS-72-HOUR-ENDURANCE`. The job is fixed to `3.00:00:00`, a 250 ms pacing interval, and at least 100,000 transactions; changing those values invalidates the release gate.

The workflow targets a private runner carrying the labels `self-hosted`, `linux`, `x64`, and `bluetusk-endurance`. It needs the .NET 10 SDK, Docker with Compose, enough durable workspace for PostgreSQL WAL/logs, and monitoring outside the test process. GitHub-hosted jobs are limited to six hours, while self-hosted jobs can run for up to five days, so the 72-hour gate cannot truthfully run on the normal hosted CI pool; see GitHub's [Actions limits](https://docs.github.com/en/actions/reference/limits) and [self-hosted runner routing](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/use-in-a-workflow).

Do not mark the roadmap or release notes complete when the workflow is merely
queued, skipped, cancelled, or smoke-tested. Completion requires a successful
format-1 report, accepted by the verifier, that records at least 72 hours, at
least 100,000 transactions, non-zero duplicate/replay/conflict/fencing/restart
counters, bounded maximum storage, zero final storage bytes, the exact
unchanged source, immutable test artifacts, exact candidate package hashes, a
digest-pinned PostgreSQL image, and successful isolated-worktree cleanup.
