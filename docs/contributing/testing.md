# Testing

## Test levels

- Unit tests cover deterministic framing, codecs, state transitions, and configuration.
- Conformance tests use a scriptable fake server to force network and protocol edge cases.
- Integration tests run against every supported PostgreSQL major version.
- Compatibility tests compare selected outcomes with libpq or other providers, then resolve differences against PostgreSQL behaviour.
- Stress tests cover cancellation, pool churn, concurrent readers, pipeline recovery,
  and dedicated replication cancellation/disposal. The replication durability
  matrix exercises persistent-slot feedback and resume across independent sessions.

Tests requiring a server read `BLUETUSK_TEST_CONNECTION_STRING` and must skip with a clear reason when it is absent. Credentials must never be printed, including in failed test output.

The checked-in compose `pg_hba.conf` reserves `bluetusk_md5_test` and
`bluetusk_cleartext_test` for authentication compatibility tests before the
default SCRAM rule. The tests create those roles only for their own lifetime;
normal matrix users continue to authenticate with SCRAM-SHA-256.

PostgreSQL 18 native OAUTHBEARER uses a separate `security-tests` compose
profile. Its image compiles a deliberately fixed-token test validator, creates
a short-lived self-signed TLS certificate, and exposes only the isolated test
role on port 5618. It is conformance infrastructure, not an example production
validator:

```powershell
docker compose -f eng/compose/postgres.yml --profile security-tests up -d --build --wait oauth18
$env:BLUETUSK_OAUTH_TEST_CONNECTION_STRING = "Host=localhost;Port=5618;Username=bluetusk_oauth_test;Database=bluetusk_tests;SSL Mode=Require;Channel Binding=Disable"
dotnet test tests/BlueTusk.IntegrationTests --filter FullyQualifiedName~Native_OAUTHBEARER
```

The compatibility project carries a test-only Npgsql dependency and runs equivalent value, parameter, transaction-error, cancellation, reuse, and schema-metadata operations through both providers. PostgreSQL internal type names (`int4`, `bool`) and SQL aliases (`integer`, `boolean`) are normalized before comparison; any other difference fails the suite and must be resolved against PostgreSQL behavior.

The default stress scale runs bounded concurrent pool churn, cancellation storms, preparation, batches, and partially consumed sequential streams. Set `BLUETUSK_STRESS_SCALE` to a positive integer to multiply the worker count for longer soak runs.

Set `BLUETUSK_REPLICATION_DURABILITY_EPOCHS` to a positive integer to extend
the logical replication reconnect/resume test. Each epoch opens a new dedicated
session, validates the persisted checkpoint and slot state, commits a fresh
transaction, persists its exact pgoutput transaction-end LSN, sends monotonic
feedback, and disconnects cleanly.

The scheduled and manually dispatched `replication-endurance` CI job runs this
path for 1,000 PostgreSQL 19 reconnect epochs. Pull requests retain the fast
default while still running the three-epoch test on every PostgreSQL major.

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests"
$env:BLUETUSK_REPLICATION_DURABILITY_EPOCHS = "250"
dotnet test tests/BlueTusk.CompatibilityTests --no-restore
dotnet test tests/BlueTusk.StressTests --no-restore
dotnet test tests/BlueTusk.IntegrationTests --no-restore --filter FullyQualifiedName~Logical_replication_validates_and_resumes
```

BenchmarkDotNet reports are written below `artifacts/benchmarks` by default. The named reference environment under `benchmarks/baselines` checks in human-readable GitHub Markdown and brief JSON reports. Refresh a short baseline with:

```powershell
$env:BLUETUSK_BENCHMARK_ARTIFACTS = "benchmarks/baselines/windows-ryzen7-5800x-dotnet10"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short --filter '*DataReaderBenchmarks*' '*ProtocolStreamingBenchmarks*'
```

The checked-in command, reader, streaming, and protocol-write reports have explicit managed-allocation budgets. After regenerating a named baseline, verify it before committing:

```powershell
pwsh -File eng/verify-allocation-budgets.ps1
```
