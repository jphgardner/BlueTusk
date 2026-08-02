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

Test cases within a project may execute concurrently against the configured
database. Live tests must therefore use unique object names or otherwise scope
database-wide effects to their own schema. In particular, event-trigger
fixtures filter `pg_event_trigger_ddl_commands()` by schema so unrelated
concurrent DDL cannot change their assertions.

The normal local release gate mirrors CI:

```powershell
dotnet restore BlueTusk.slnx
dotnet format BlueTusk.slnx --verify-no-changes --no-restore
./eng/verify-documentation.ps1
dotnet build BlueTusk.slnx -c Release --no-restore
dotnet test BlueTusk.slnx -c Release --no-build --no-restore
./eng/verify-allocation-budgets.ps1
dotnet pack BlueTusk.slnx -c Release --no-build --no-restore --output artifacts/packages
```

The documentation check validates every repository-local link in every tracked
Markdown file on both Windows and Linux. External links remain a release-review
responsibility because network availability must not make the normal build
nondeterministic.

Each live PostgreSQL-version matrix entry has its own CI runner. When reproducing
the complete matrix on one development machine, run `dotnet test BlueTusk.slnx`
serially for each connection string. Multiple simultaneous copies of the full
EF relational specification suite can exhaust host memory and turn database
setup into misleading timeout failures.

## Optional extension images

The extension profile supplies the three images that are not available in a
plain PostgreSQL distribution:

```powershell
docker compose -f eng/compose/postgres.yml --profile extension-tests up -d --wait
```

Run both the ADO.NET and EF package projects against the corresponding port:

| Extension | Port | Test projects |
| --- | ---: | --- |
| pgvector | 5518 | `BlueTusk.Extensions.PgVector.Tests`, `BlueTusk.Extensions.PgVector.EntityFrameworkCore.Tests` |
| PostGIS | 5519 | `BlueTusk.Extensions.PostGIS.Tests`, `BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Tests` |
| TimescaleDB | 5520 | `BlueTusk.Extensions.TimescaleDB.Tests`, `BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Tests` |

Set `BLUETUSK_TEST_CONNECTION_STRING` to the selected port before each pair.
The live case must pass on its dedicated image. On a plain matrix image it
dynamically skips only when `pg_available_extensions` confirms that the
optional server extension is unavailable.

Every restore audits direct and transitive dependencies at every advisory
severity. To produce an explicit machine-readable review on .NET 10, run:

```powershell
dotnet package list --project BlueTusk.slnx --vulnerable --include-transitive --format json
```

An empty project-only result means no advisory matched. Any `NU1901` through
`NU1904` restore diagnostic is an error; advisory suppressions require a
documented security-review update and expiry decision.

Normal Release builds also enforce the checked-in public API contracts for the
ADO.NET stack, replication packages, and extension-authoring seam. A missing,
changed, or incorrectly ordered declaration fails the build. After an approved
additive change, place the analyzer's canonical signature in that project's
`PublicAPI.Unshipped.txt`; do not edit a shipped line merely to make a breaking
change pass. The compatibility policy and covered assemblies are in
[API compatibility](../api-compatibility.md).

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

PostgreSQL 18 GSSAPI/Kerberos acceptance uses another isolated service in the
same profile. The image runs an MIT KDC, creates only test realm principals and
a PostgreSQL keytab, and exposes the KDC on port 5688 and PostgreSQL on port
5718. On a Linux host with `krb5-user` installed:

```powershell
docker compose -f eng/compose/postgres.yml --profile security-tests up -d --build --wait gss18
$env:KRB5_CONFIG = "$PWD/eng/compose/gssapi/krb5-client.conf"
"bluetusk-gss-password" | kinit bluetusk_gss_test@BLUETUSK.TEST
$env:BLUETUSK_GSS_TEST_CONNECTION_STRING = "Host=localhost;Port=5718;Username=bluetusk_gss_test;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable;Kerberos Service Name=postgres"
dotnet test tests/BlueTusk.IntegrationTests --filter FullyQualifiedName~GSSAPI_Kerberos
```

The fixed realm password, KDC database, service keytab, and ticket cache are
test-only ephemeral infrastructure. They must not be copied into a deployment.

Cloud identity adapter tests are deterministic by default. Optional
external-account acceptance uses the default AWS, Azure, or Google SDK identity
chain and one complete connection string per provider:

```powershell
$env:BLUETUSK_AWS_RDS_TEST_CONNECTION_STRING = "Host=...;Database=...;Username=...;SSL Mode=VerifyFull"
$env:BLUETUSK_AZURE_POSTGRESQL_TEST_CONNECTION_STRING = "Host=...;Database=...;Username=...;SSL Mode=VerifyFull"
$env:BLUETUSK_GOOGLE_CLOUD_SQL_TEST_CONNECTION_STRING = "Host=...;Database=...;Username=...;SSL Mode=VerifyFull"
dotnet test tests/BlueTusk.Identity.Tests --no-restore
```

Unset variables skip only the corresponding account-backed test. Never place
cloud tokens, SDK credentials, or populated connection strings in the
repository or test output. Provider setup and lifecycle details are in the
[cloud identity guide](../ado-net/cloud-identity.md).

The compatibility project carries a test-only Npgsql dependency and runs equivalent value, parameter, transaction-error, cancellation, reuse, and schema-metadata operations through both providers. PostgreSQL internal type names (`int4`, `bool`) and SQL aliases (`integer`, `boolean`) are normalized before comparison; any other difference fails the suite and must be resolved against PostgreSQL behavior.

The default stress scale runs bounded concurrent pool churn, cancellation
storms, preparation, batches, partially consumed sequential streams, ordered
pipeline groups, pipeline cancellation recovery, and replication
cancellation/disposal. Set `BLUETUSK_STRESS_SCALE` to a positive integer to
multiply the worker count. The scheduled/manual PostgreSQL 19 provider-stress
job uses scale 8 for the pooled ADO.NET and Client pipeline tests. Replication
is excluded from that high-connection-count job because its durability and
recovery surface has the separate 1,000-epoch endurance job below; replication
cancellation/disposal stress still runs in every PostgreSQL 15–19 matrix job.

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

Refresh the live PostgreSQL 19 provider comparison separately so the ordinary
suite remains server-independent:

```powershell
$env:BLUETUSK_BENCHMARK_CONNECTION_STRING = "Host=localhost;Port=5419;Database=bluetusk_tests;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable"
$env:BLUETUSK_BENCHMARK_ARTIFACTS = "benchmarks/baselines/windows-ryzen7-5800x-dotnet10"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short --filter '*ProviderComparisonBenchmarks*'
```

Commit only the brief JSON and GitHub Markdown reports. Record the PostgreSQL
major version, machine profile, SDK/runtime, date, and any material semantic
difference between the provider pairs. Never turn a ShortRun ratio into a
universal performance claim.
