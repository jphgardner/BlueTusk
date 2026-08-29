[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = '/workspace'
$reportRoot = 'artifacts/endurance/sync'
$postgresImage = 'postgres:19beta3-alpine@sha256:b1692e50613a21e61c424859f943b9e193ae73e5a8c68abd5382dfb235bf15fc'
$destinationImages = @(
    'redis:8-alpine@sha256:978f0e01593e65eed801f2402944efcd936d43b5027e4908a7897baf88ed6241',
    'nats:2.14-alpine@sha256:f2123f533c2b0cada0a5c5ec434fb2b8cfe1cf220215ef9d7517e1372917ad66',
    'apache/kafka:4.1.1@sha256:7240ff4534bd23dac2f215ba03a2d0aa9d041b45b830804bbdec3b81c2bdf479',
    'minio/minio:RELEASE.2025-09-07T16-13-09Z@sha256:a1a8bd4ac40ad7881a245bab97323e18f971e4d4cba2c2007ec1bedd21cbaba2',
    'opensearchproject/opensearch:3.7.0@sha256:44ba7ea58a319adf61c33ab16873f9ef5dbb30b291a832d375172f0b2d24e3c9')

if ($env:CANDIDATE_SHA -notmatch '^[0-9a-f]{40}$' -or
    $env:CANDIDATE_VERSION -notmatch '^1\.2\.0-rc\.[1-9][0-9]*$')
{
    throw 'A lowercase full candidate SHA and a 1.2.0 RC version are required.'
}

Set-Location $repositoryRoot
if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot '.git')))
{
    & git init --quiet
}
& git remote get-url origin *> $null
if ($LASTEXITCODE -ne 0)
{
    & git remote add origin https://github.com/jphgardner/BlueTusk.git
}
& git fetch --quiet --depth 1 origin $env:CANDIDATE_SHA
& git checkout --quiet --detach $env:CANDIDATE_SHA
if ($LASTEXITCODE -ne 0 -or (& git rev-parse HEAD).Trim() -ne $env:CANDIDATE_SHA)
{
    throw 'The exact candidate commit could not be checked out.'
}

& ./eng/pack-product-family.ps1 `
    -Family Sync `
    -Prerelease `
    -VersionOverride $env:CANDIDATE_VERSION `
    -PrereleaseTrainPath eng/prerelease-train.json `
    -Output "$reportRoot/candidate-packages"
& dotnet restore BlueTusk.slnx --locked-mode
& ./eng/generate-sbom.ps1 `
    -PackageDirectory "$reportRoot/candidate-packages" `
    -OutputDirectory "$reportRoot/candidate-sbom" `
    -Commit $env:CANDIDATE_SHA `
    -NoRestore

$env:BLUETUSK_TEST_CONNECTION_STRING = (
    'Host=postgresql;Port=5432;Username=postgres;' +
    "Password=$env:POSTGRES_PASSWORD;Database=bluetusk_tests;" +
    'SSL Mode=Disable;Channel Binding=Disable')
$env:BLUETUSK_NATS_URL = 'nats://nats:4222'
$env:BLUETUSK_KAFKA_BOOTSTRAP_SERVERS = 'kafka:9092'
$env:BLUETUSK_S3_ENDPOINT = 'http://minio:9000'
$env:BLUETUSK_TEST_REDIS_CONNECTION_STRING = 'redis:6379,abortConnect=false'
$env:BLUETUSK_OPENSEARCH_URL = 'http://opensearch:9200'

& ./eng/run-sync-endurance.ps1 `
    -Duration '1.00:00:00' `
    -MinimumCycles 100 `
    -ReportPath "$reportRoot/report.json" `
    -CandidateProvenancePath "$reportRoot/candidate-sbom/build-provenance.json" `
    -PostgreSqlImage $postgresImage `
    -DestinationImages $destinationImages `
    -Configuration Release
& ./eng/verify-sync-endurance-report.ps1 `
    -ReportPath "$reportRoot/report.json" `
    -RequiredDuration '1.00:00:00' `
    -MinimumCycles 100 `
    -ExpectedCommit $env:CANDIDATE_SHA `
    -CandidateProvenancePath "$reportRoot/candidate-sbom/build-provenance.json" `
    -ExpectedPostgreSqlImage $postgresImage `
    -ExpectedDestinationImages $destinationImages

[IO.File]::WriteAllText(
    (Join-Path $repositoryRoot "$reportRoot/SUCCESS"),
    "$($env:CANDIDATE_SHA) $($env:CANDIDATE_VERSION)`n",
    [Text.UTF8Encoding]::new($false))
