# Sync release endurance

Sync stable release requires a completed 24-hour run of the same real-destination recovery
tests used by normal CI. The executable runner is
`eng/run-sync-endurance.ps1`; the confirmed self-hosted workflow is
`.github/workflows/sync-release-endurance.yml`.

Each cycle runs the core pipeline, in-process hosting, shared conformance kit,
and PostgreSQL, NATS JetStream, Redis, OpenSearch, Kafka, S3/Parquet, and signed-webhook
suites. The Kafka suite runs against a real broker; webhook protocol/failure
boundaries use a deterministic receiver. Those suites
exercise snapshot restart, transaction redelivery, destination-instance
restart, transform drift, durable quarantine, PostgreSQL rollback,
JetStream deduplication, Redis preflight failure, OpenSearch partial-bulk
recovery, reconciliation/repair, and zero-downtime alias cutover. Any project
failure stops the run and writes a failed evidence report.

The runner refuses to start unless all six service endpoints and the S3 test
credentials are explicit and
the launch repository has no tracked changes. It creates a detached Git
worktree at the recorded source commit, restores and builds all nine test
projects there, and runs every cycle only from that isolated workspace. Other
repository builds and commits therefore cannot replace the binaries under
test.

The format-4 JSON report records requested and actual test duration, completed
cycles, project runs, the slowest cycle, exact source commit and branch,
isolated start/end commits and cleanliness, combined SHA-256 start/end hashes
of every test artifact, artifact count, isolated-worktree cleanup, the launch
repository state at completion, .NET SDK, host OS/architecture, processor
count, exact project list, candidate package/provenance hashes, and the
digest-pinned PostgreSQL, Redis, NATS, Kafka, MinIO and OpenSearch images. A report is
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

The 1.2 core release workflow uses digest-pinned PostgreSQL 18 and an explicit
`-ReleaseTrack Core` reader scope. All nine named projects are required;
duplicates or replacement projects cannot satisfy coverage. PostgreSQL 19 and
Graph preview remain independent. Kubernetes core runs use `postgresql-core`
with separate storage and do not downgrade the historical Graph database.

The native report does not by itself claim the complete production disturbance
matrix. During the same 24-hour observation window, operators must also record
process death, network interruption, controlled storage exhaustion, credential
rotation, primary failover, backward/forward clock movement and a real
PostgreSQL minor upgrade. Those seven records are independently
content-addressed and verified with the Streams records by the
[endurance disturbance evidence contract](../operations/endurance-disturbance-evidence.md).

## Retained connector test results

The release workflow also captures one complete, unfiltered functional run of
all nine suites with `eng/run-core-test-evidence.ps1 -Kind SyncConnectors`.
`sync-connector-validation.json` binds the clean exact source commit, the
committed collector version, OS, execution interval, each test assembly's
version and hash, raw VSTest discovery, TRX and test log. Payloads are retained
under `connector-tests/tests/`. Existing payload paths are never overwritten;
failed discovery, restore, build or execution logs remain available.

The reader `eng/verify-sync-connector-evidence.ps1` requires every discovered
test to have exactly one passing result, matching definitions and counters,
with zero skips. Missing projects, filters, substituted tests and changed
payload bytes fail verification. This functional capture does not establish
24-hour endurance, authenticate fixture or remote execution identities, or
approve publication. It does not assert that every endurance cycle has a TRX.

For local Docker execution, configure all eight environment variables below
against actual fixtures, then use a fresh output subtree:

```powershell
./eng/run-core-test-evidence.ps1 -Kind SyncConnectors `
  -ExpectedCommit (git rev-parse HEAD) `
  -OutputRoot 'artifacts/test-results/sync-functional' -Build
./eng/verify-sync-connector-evidence.ps1 `
  -EvidencePath 'artifacts/test-results/sync-functional/sync-connector-validation.json' `
  -ExpectedCommit (git rev-parse HEAD)
```

`-SourceRoot` can select another clean checkout at `-ExpectedCommit`; outputs
remain beneath the collector checkout's `artifacts/` directory. Both source
and tools must be committed and clean throughout capture. This permits new
collector tools to measure a retained older source without relabelling it.
Keep the exact-source functional manifest and its `connector-tests/` subtree
beside the endurance report when assembling the `sync/` evidence folder.
The local record emitter verifies both captures and records their complete
combined execution interval; the 24-hour duration gate remains bound to the
original endurance report. An older report without raw results cannot supply
this additional functional evidence by itself.

## Local smoke

Start PostgreSQL 18, Redis 8, NATS JetStream, Kafka 4.1, MinIO, and OpenSearch 3.7 using the same
ports as normal CI, then run:

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = 'Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable'
$env:BLUETUSK_NATS_URL = 'nats://localhost:4222'
$env:BLUETUSK_KAFKA_BOOTSTRAP_SERVERS = 'localhost:9092'
$env:BLUETUSK_S3_ENDPOINT = 'http://127.0.0.1:9000'
$env:BLUETUSK_S3_ACCESS_KEY = 'bluetusk_endurance'
$env:BLUETUSK_S3_SECRET_KEY = 'bluetusk_endurance_secret'
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
  -ReleaseTrack Core `
  -ReportPath 'artifacts/test-results/sync-endurance-smoke/report.json' `
  -RequiredDuration '00:00:01' `
  -MinimumCycles 1 `
  -ExpectedCommit (git rev-parse HEAD)
```

## Release gate

Dispatch **Sync 24-hour release endurance** and enter the exact confirmation
`RUN-SYNC-24-HOUR-ENDURANCE`. The workflow fixes the duration at 24 hours and
requires at least 100 complete nine-project cycles. It targets a private runner
labelled `self-hosted`, `linux`, `x64`, and `bluetusk-endurance`, retains the
report for 90 days, and captures all service logs on failure.

Sync stays non-publishable until one successful format-4 report is reviewed and
archived and all seven Sync disturbance records occur inside its exact
observation window. A format-2 report from the shared-output runner is
diagnostic only and cannot satisfy the release gate.
