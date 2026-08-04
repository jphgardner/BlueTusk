# Sync release endurance

Sync 1.0 requires a completed 24-hour run of the same real-destination recovery
tests used by normal CI. The executable runner is
`eng/run-sync-endurance.ps1`; the confirmed self-hosted workflow is
`.github/workflows/sync-release-endurance.yml`.

Each cycle runs the core pipeline, in-process hosting, shared conformance kit,
and real PostgreSQL, NATS JetStream, Redis, and OpenSearch suites. Those suites
exercise snapshot restart, transaction redelivery, destination-instance
restart, transform drift, durable quarantine, PostgreSQL rollback,
JetStream deduplication, Redis preflight failure, OpenSearch partial-bulk
recovery, reconciliation/repair, and zero-downtime alias cutover. Any project
failure stops the run and writes a failed evidence report.

The runner refuses to start unless all four service endpoints are explicit and
the launch repository has no tracked changes. It creates a detached Git
worktree at the recorded source commit, restores and builds all six test
projects there, and runs every cycle only from that isolated workspace. Other
repository builds and commits therefore cannot replace the binaries under
test.

The format-3 JSON report records requested and actual test duration, completed
cycles, project runs, the slowest cycle, exact source commit and branch,
isolated start/end commits and cleanliness, combined SHA-256 start/end hashes
of every test artifact, artifact count, isolated-worktree cleanup, the launch
repository state at completion, .NET SDK, host OS/architecture, processor
count, exact project list, candidate package/provenance hashes, and the
digest-pinned PostgreSQL, Redis, NATS and OpenSearch images. A report is
successful only when `completed` is true, the requested duration and minimum
cycle count pass, the detached source is unchanged, every test artifact and
candidate-package hash is unchanged, all service images are pinned, and the
isolated worktree is removed. Restore, build, test, source-integrity,
artifact-integrity, and cleanup failures are distinguished by `failedPhase`.

`eng/verify-sync-endurance-report.ps1` is the fail-closed evidence reader. It
checks the exact expected commit, format, duration, cycle and project-run
counts, clean isolated source, identical artifact fingerprints, Release
configuration, absence of failure metadata, and worktree cleanup. The release
workflow runs this verifier before uploading evidence.

## Local smoke

Start PostgreSQL 19, Redis 8, NATS JetStream, and OpenSearch 3.7 using the same
ports as normal CI, then run:

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = 'Host=localhost;Port=5419;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable'
$env:BLUETUSK_NATS_URL = 'nats://localhost:4222'
$env:BLUETUSK_TEST_REDIS_CONNECTION_STRING = 'localhost:6379,abortConnect=false'
$env:BLUETUSK_OPENSEARCH_URL = 'http://127.0.0.1:9200'
./eng/run-sync-endurance.ps1 `
  -Duration '00:00:01' `
  -MinimumCycles 1 `
  -ReportPath 'artifacts/test-results/sync-endurance-smoke/report.json'
```

The one-cycle smoke validates isolated checkout, restore/build, orchestration,
artifact integrity, cleanup, and report production. It is not 24-hour release
evidence.

Validate its report with the same reader:

```powershell
./eng/verify-sync-endurance-report.ps1 `
  -ReportPath 'artifacts/test-results/sync-endurance-smoke/report.json' `
  -RequiredDuration '00:00:01' `
  -MinimumCycles 1 `
  -ExpectedCommit (git rev-parse HEAD)
```

## Release gate

Dispatch **Sync 24-hour release endurance** and enter the exact confirmation
`RUN-SYNC-24-HOUR-ENDURANCE`. The workflow fixes the duration at 24 hours and
requires at least 100 complete six-project cycles. It targets a private runner
labelled `self-hosted`, `linux`, `x64`, and `bluetusk-endurance`, retains the
report for 90 days, and captures all service logs on failure.

Sync stays non-publishable until one successful format-3 report is reviewed and
archived. A format-2 report from the shared-output runner is diagnostic only
and cannot satisfy the release gate.
