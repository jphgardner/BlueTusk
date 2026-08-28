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
./eng/verify-solution-layout.ps1
dotnet format BlueTusk.slnx --verify-no-changes --no-restore
./eng/verify-documentation.ps1
dotnet build BlueTusk.slnx -c Release --no-restore
dotnet test BlueTusk.slnx -c Release --no-build --no-restore
./eng/verify-allocation-budgets.ps1
dotnet pack BlueTusk.slnx -c Release --no-build --no-restore --output artifacts/packages
```

Provider-core trimming and NativeAOT are separate publish gates because an
ordinary build does not run the linker or native compiler. On Windows x64:

```powershell
dotnet restore tests/BlueTusk.TrimSmoke/BlueTusk.TrimSmoke.csproj -r win-x64
dotnet restore tests/BlueTusk.NativeAotSmoke/BlueTusk.NativeAotSmoke.csproj -r win-x64
./eng/verify-provider-core-publish.ps1 -RuntimeIdentifier win-x64 -NoRestore
```

The verifier executes both published applications, applies the checked-in
deployable-size, cold-start and second-pass managed-allocation budgets, and
writes an evidence report under
`artifacts/provider-core-smoke/win-x64/report.json`. Deployable size excludes
optional PDB and XML documentation files while the report retains their bytes
separately.
Use `-SkipPublish` only to remeasure already published outputs; CI always
publishes from source. The required CI matrix runs both `win-x64` and
`linux-x64`.

The documentation check validates every repository-local link in every tracked
Markdown file on both Windows and Linux. External links remain a release-review
responsibility because network availability must not make the normal build
nondeterministic.

Each live PostgreSQL-version matrix entry has its own CI runner. When reproducing
the complete matrix on one development machine, run `dotnet test BlueTusk.slnx`
with `--maxcpucount:1` for each connection string. CI applies the same
project-serial setting inside each server runner. Multiple simultaneous copies
of the full EF relational specification suite can exhaust host memory and turn
database setup into misleading timeout failures.

## Dedicated extension-image gates

The extension profile supplies the four images that are not available in a
plain PostgreSQL distribution. CI runs each image in its own required matrix
entry and executes its applicable ADO.NET and EF Core projects, so a dynamically
skipped plain-image test cannot satisfy the extension acceptance gate:

```powershell
docker compose -f eng/compose/postgres.yml --profile extension-tests up -d --wait
```

Run both the ADO.NET and EF package projects against the corresponding port:

| Extension | Port | Test projects |
| --- | ---: | --- |
| pgvector | 5518 | `BlueTusk.Extensions.PgVector.Tests`, `BlueTusk.Extensions.PgVector.EntityFrameworkCore.Tests` |
| PostGIS | 5519 | `BlueTusk.Extensions.PostGIS.Tests`, `BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Tests` |
| TimescaleDB | 5520 | `BlueTusk.Extensions.TimescaleDB.Tests`, `BlueTusk.Extensions.TimescaleDB.EntityFrameworkCore.Tests` |
| pg_durable | 5521 | `BlueTusk.Extensions.PgDurable.Tests` (connect to the required `postgres` database) |

Set `BLUETUSK_TEST_CONNECTION_STRING` to the selected port before each pair.
The live case must pass on its dedicated image. On a plain matrix image it
dynamically skips only when `pg_available_extensions` confirms that the
optional server extension is unavailable.

## PgBouncer, locales, and time zones

The `compatibility-tests` profile builds a pinned PgBouncer 1.24.0 image and
runs both session and transaction pooling against PostgreSQL 18. The session
gate exercises temporary-table and prepared-statement state. The transaction
gate exercises explicit transactions and PgBouncer's protocol-level prepared
statement tracking. The isolated test configuration uses cleartext client
authentication on the Docker network, so its connection strings must explicitly
set `Allow Unencrypted Password=true`; this is not deployment guidance.

```powershell
docker compose -f eng/compose/postgres.yml --profile compatibility-tests up -d --build --wait pgbouncer-session18 pgbouncer-transaction18
$env:BLUETUSK_PGBOUNCER_SESSION_CONNECTION_STRING = "Host=localhost;Port=5818;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable;Allow Unencrypted Password=true;Pooling=false"
$env:BLUETUSK_PGBOUNCER_TRANSACTION_CONNECTION_STRING = "Host=localhost;Port=5819;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable;Allow Unencrypted Password=true;Pooling=false"
dotnet test tests/BlueTusk.IntegrationTests --filter FullyQualifiedName~PgBouncer
```

The same profile contains PostgreSQL 18 images initialized as `en_GB.UTF-8`
with `Europe/London` and `de_DE.UTF-8` with `America/New_York`. Their CI matrix
verifies the database collation, `lc_monetary`, locale-formatted `money` text,
server time zone, and UTC-equivalent `timestamptz` decoding. Ports 5820 and 5821
map to the English and German images respectively.

## Primary/standby topology

The `topology-tests` profile creates a PostgreSQL 18 primary and takes a real
`pg_basebackup` for a hot standby that continuously streams WAL. The topology
gate verifies strict and preferred primary/read-write/standby/read-only target
selection after both unavailable and role-incompatible endpoints. It also
writes on the primary, waits for the row to become visible on the standby, and
proves that the standby rejects writes.

```powershell
docker compose -f eng/compose/postgres.yml --profile topology-tests up -d --build --wait topology-standby18
$env:BLUETUSK_TOPOLOGY_CONNECTION_STRING = "Host=localhost,localhost;Port=5830,5831;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable;Pooling=false"
dotnet test tests/BlueTusk.IntegrationTests --filter FullyQualifiedName~Topology
```

The primary and standby are exposed on ports 5830 and 5831. Their credentials,
base backup, and WAL are ephemeral test infrastructure removed with `docker
compose down --volumes`.

## PostgreSQL 19 nightly snapshot

PostgreSQL publishes a checksummed PostgreSQL 19 branch snapshot from its
official development server each night, but the official Docker image project
does not publish a corresponding nightly tag. The `nightly-tests` profile
therefore compiles that official source tarball in a multi-stage image after
verifying its published SHA-256 file. Scheduled and manually dispatched CI runs
the full solution against the resulting server on port 5899.

```powershell
docker compose -f eng/compose/postgres.yml --profile nightly-tests up -d --build --wait postgres19-nightly
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5899;Username=postgres;Password=postgres;Database=bluetusk_tests;SSL Mode=Disable;Channel Binding=Disable"
dotnet test BlueTusk.slnx -c Release --no-restore --maxcpucount:1
```

The image is intentionally rebuilt from the moving snapshot. It is acceptance
infrastructure for detecting PostgreSQL 19 branch changes, not a distributable
database image or production dependency.

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

The separate Streams relay release gate is documented in
[Streams release endurance](../streams/release-endurance.md). Its normal test
path is skipped unless `BLUETUSK_RELAY_ENDURANCE_DURATION` is explicit. Local
short runs validate the harness; only the confirmed self-hosted workflow's
successful 72-hour JSON report satisfies the Streams 1.0 gate.

The Sync release gate is documented in
[Sync release endurance](../sync/release-endurance.md). Its runner refuses
implicit service endpoints and repeatedly executes the core, hosting, shared
conformance, PostgreSQL, NATS, Redis, and OpenSearch projects. A one-cycle local
smoke validates orchestration and report output; only the confirmed self-hosted
workflow's successful 24-hour report satisfies the Sync 1.0 endurance gate.

```powershell
$env:BLUETUSK_TEST_CONNECTION_STRING = "Host=localhost;Port=5418;Username=postgres;Password=postgres;Database=bluetusk_tests"
$env:BLUETUSK_REPLICATION_DURABILITY_EPOCHS = "250"
dotnet test tests/BlueTusk.CompatibilityTests --no-restore
dotnet test tests/BlueTusk.StressTests --no-restore
dotnet test tests/BlueTusk.IntegrationTests --no-restore --filter FullyQualifiedName~Logical_replication_validates_and_resumes
```

BenchmarkDotNet reports are written below `artifacts/benchmarks` by default. The named reference environment under `benchmarks/baselines` checks in human-readable GitHub Markdown and brief JSON reports. Refresh a short baseline with:

```powershell
$env:BLUETUSK_BENCHMARK_ARTIFACTS = "artifacts/benchmarks"
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
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- `
  --job medium --inProcess --filter '*ProviderComparisonBenchmarks*'
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release --no-build -- `
  --provider-paired-evidence artifacts/benchmarks/provider-paired-evidence.json
./eng/verify-provider-performance.ps1 `
  -ReportPath benchmarks/baselines/windows-ryzen7-5800x-dotnet10/results/BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-brief.json `
  -PairedReportPath artifacts/benchmarks/provider-paired-evidence.json
```

The same isolated database drives the EF application and SQL/PGQ traversal
fixtures. They recreate fixed `bluetusk_benchmark_*` objects and therefore must
not target a shared development database:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short --filter '*EntityFrameworkCoreBenchmarks*' '*SqlPgqBenchmarks*'
```

Commit or archive the brief JSON, GitHub Markdown and paired provider report.
BenchmarkDotNet remains the absolute-latency and managed-allocation source. The
paired report is the provider-relative latency authority: five trials, 501
alternating blocks per trial, and workload-specific block sizes. The complete
matrix covers 16 matched pool, command, streaming, transaction, batch, COPY,
typed-row, notification, large-object and EF workloads.
Before measurement it completes 4,096 warm pool checkouts, 512 parameterized
and prepared commands, 64 row streams, and 32 large-value streams per provider.
The untimed warmups prevent tiered-JIT transitions from contaminating the
sub-microsecond pool trials; every measured sample and the 1.00 limits remain
unchanged.
Every sample is normalized per completed operation. The verifier requires
managed allocation at or below Npgsql in every listed provider workload. Its
five established latency paths use a strict 1.0 ceiling; the eleven extended
paths use the checked-in 1.05 parity ceiling for median-of-trials mean, P95 and
P99. Record the
PostgreSQL major version, machine profile, SDK/runtime, date, and any material
semantic difference between provider pairs. Never turn a measured workload
ratio into a universal performance claim.

The multiplexing comparison is the deliberate full-JSON exception: measured
workload samples are required to reproduce P99. It compares both providers'
multiplexed and ordinary four-session pools. Use the MediumRun and in-process
toolchain so ignored archival worktrees cannot confuse BenchmarkDotNet project
discovery:

```powershell
$env:BLUETUSK_BENCHMARK_CONNECTION_STRING = "Host=localhost;Port=5418;Database=bluetusk_tests;Username=postgres;Password=postgres;SSL Mode=Disable;Channel Binding=Disable"
$env:BLUETUSK_BENCHMARK_ARTIFACTS = "benchmarks/baselines/windows-ryzen7-5800x-dotnet10"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- `
  --job medium --inProcess --filter '*MultiplexingComparisonBenchmarks*'
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release --no-build -- `
  --multiplexing-paired-evidence artifacts/benchmarks/multiplexing-paired-evidence.json
./eng/verify-multiplexing-performance.ps1 `
  -ReportPath artifacts/benchmarks/results/BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json `
  -PairedReportPath artifacts/benchmarks/multiplexing-paired-evidence.json
./eng/test-multiplexing-performance-verifier.ps1
```

Commit its full JSON and GitHub Markdown reports. The machine gate requires at
least 20 measured samples and enforces relative mean, P95, P99, throughput, and
managed-allocation budgets against Npgsql multiplexing and BlueTusk's ordinary
pool. A budget change requires the report, rationale, and documentation in the
same review.

For a release candidate, commit or archive all BenchmarkDotNet and paired
reports. BenchmarkDotNet is the source for absolute latency and allocation; its
provider latency rows remain descriptive because methods run sequentially. The
provider and multiplexing paired reports run first and are the relative-latency
authority. The multiplexing capture uses 64 warm-ups per provider, five trials,
501 alternating blocks per trial, 4 bursts per block and 64 operations per
burst. The four paired concurrency workloads cover fresh and reused multiplexed
bursts plus both ordinary pooled controls. Both gates recompute each trial and use the median ratio; with 501 blocks,
P99 is the sixth-slowest block rather than a statistic decided by one or two
transient scheduler spikes.
