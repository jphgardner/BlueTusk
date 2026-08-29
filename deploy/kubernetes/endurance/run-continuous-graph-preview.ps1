[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = '/workspace'
$reportRoot = 'artifacts/endurance/continuous-graph-preview'
$postgresImage = 'postgres:19beta3-alpine@sha256:b1692e50613a21e61c424859f943b9e193ae73e5a8c68abd5382dfb235bf15fc'
if ($env:CANDIDATE_SHA -notmatch '^[0-9a-f]{40}$' -or
    $env:CANDIDATE_VERSION -notmatch '^1\.2\.0-rc\.[1-9][0-9]*$')
{
    throw 'A lowercase full candidate SHA and a 1.2.0 RC version are required.'
}
if ($env:NON_GATING_PREVIEW -ne 'true')
{
    throw 'Continuous Graph Beta 3 execution must be explicitly marked non-gating.'
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
    throw 'The exact preview commit could not be checked out.'
}

& ./eng/pack-product-family.ps1 `
    -Family ContinuousGraph `
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
& ./eng/run-continuous-graph-endurance.ps1 `
    -Duration '01:00:00' `
    -MinimumEvaluations 1000 `
    -IntervalMilliseconds 250 `
    -ReportPath "$reportRoot/report.json" `
    -CandidateProvenancePath "$reportRoot/candidate-sbom/build-provenance.json" `
    -PostgreSqlImage $postgresImage `
    -Configuration Release
& ./eng/verify-continuous-graph-endurance-report.ps1 `
    -ReportPath "$reportRoot/report.json" `
    -RequiredDuration '01:00:00' `
    -MinimumEvaluations 1000 `
    -ExpectedCommit $env:CANDIDATE_SHA `
    -CandidateProvenancePath "$reportRoot/candidate-sbom/build-provenance.json" `
    -ExpectedPostgreSqlImage $postgresImage

$marker = [ordered]@{
    schemaVersion = 1
    releaseGate = $false
    postgreSqlChannel = '19beta3'
    sourceCommit = $env:CANDIDATE_SHA
    candidateVersion = $env:CANDIDATE_VERSION
    reason = 'Preliminary evidence only; PostgreSQL 19 GA is required for release.'
}
[IO.File]::WriteAllText(
    (Join-Path $repositoryRoot "$reportRoot/PREVIEW.json"),
    ($marker | ConvertTo-Json),
    [Text.UTF8Encoding]::new($false))
