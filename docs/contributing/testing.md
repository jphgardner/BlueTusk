# Testing

## Test levels

- Unit tests cover deterministic framing, codecs, state transitions, and configuration.
- Conformance tests use a scriptable fake server to force network and protocol edge cases.
- Integration tests run against every supported PostgreSQL major version.
- Compatibility tests compare selected outcomes with libpq or other providers, then resolve differences against PostgreSQL behaviour.
- Stress tests cover cancellation, pool churn, concurrent readers, and long-running replication.

Tests requiring a server read `BLUETUSK_TEST_CONNECTION_STRING` and must skip with a clear reason when it is absent. Credentials must never be printed, including in failed test output.

The compatibility project carries a test-only Npgsql dependency and runs equivalent value, parameter, transaction-error, cancellation, reuse, and schema-metadata operations through both providers. PostgreSQL internal type names (`int4`, `bool`) and SQL aliases (`integer`, `boolean`) are normalized before comparison; any other difference fails the suite and must be resolved against PostgreSQL behavior.

The default stress scale runs bounded concurrent pool churn, cancellation storms, preparation, batches, and partially consumed sequential streams. Set `BLUETUSK_STRESS_SCALE` to a positive integer to multiply the worker count for longer soak runs.

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests"
dotnet test tests/BlueTusk.CompatibilityTests --no-restore
dotnet test tests/BlueTusk.StressTests --no-restore
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
