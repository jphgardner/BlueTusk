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

The runner refuses to start unless all four service endpoints are explicit. Its
versioned JSON report records requested and actual duration, completed cycles,
project runs, the slowest cycle, the .NET SDK, and exact project list. A report
is successful only when `completed` is true, the requested duration elapsed,
and the configured minimum cycle count passed.

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

The one-cycle smoke validates orchestration and report production. It is not
24-hour release evidence.

## Release gate

Dispatch **Sync 24-hour release endurance** and enter the exact confirmation
`RUN-SYNC-24-HOUR-ENDURANCE`. The workflow fixes the duration at 24 hours and
requires at least 100 complete six-project cycles. It targets a private runner
labelled `self-hosted`, `linux`, `x64`, and `bluetusk-endurance`, retains the
report for 90 days, and captures all service logs on failure.

Sync stays non-publishable until one successful report is reviewed and archived.
