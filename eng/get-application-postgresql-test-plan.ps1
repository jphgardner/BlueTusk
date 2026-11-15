[CmdletBinding()]
param(
    [ValidateSet('Core', 'ContinuousGraphPortable')]
    [string] $ReleaseTrack = 'Core',
    [string] $ConfigurationDirectory = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tracksPath = Join-Path $ConfigurationDirectory 'release-tracks.json'
& (Join-Path $PSScriptRoot 'verify-release-track.ps1') -TrackPath $tracksPath | Out-Null
$tracks = Get-Content -LiteralPath $tracksPath -Raw | ConvertFrom-Json
$testClass = 'BlueTusk.Applications.ArchitectureTests.PostgreSqlApplicationIntegrationTests'

if ($ReleaseTrack -eq 'Core')
{
    if ($tracks.releaseVersion -ceq '1.2.0')
    {
        $contract = Get-Content -LiteralPath (
            Join-Path $ConfigurationDirectory 'v1.2-core-package-readiness.json') -Raw | ConvertFrom-Json
        $image = [string]$contract.endurance.postgresqlImage
        $scope = 'CorePackages'
    }
    else
    {
        $contract = Get-Content -LiteralPath (
            Join-Path $ConfigurationDirectory 'v1.1-candidate-readiness.json') -Raw | ConvertFrom-Json
        $image = [string]$contract.endurancePostgreSqlImage
        $scope = 'Core'
    }
    if ($contract.schemaVersion -ne 1 -or $contract.scope -cne $scope -or
        $contract.releaseVersion -cne $tracks.releaseVersion -or
        $image -cnotmatch '^postgres:(15|16|17|18)(?:\.\d+)?-alpine@sha256:[0-9a-f]{64}$')
    {
        throw 'Core application tests require the core contract and a digest-pinned stable PostgreSQL image.'
    }
    $major = [int]$Matches[1]
    if ($major -notin $tracks.stablePostgreSqlMajors)
    {
        throw 'The core application image is not in the stable PostgreSQL support policy.'
    }
    $milestone = 'stable'
    $tests = @("$testClass.Order_migrations_tenant_isolation_and_idempotency_work")
}
else
{
    $image = [string]$tracks.graph.portablePostgreSqlImage
    $milestone = 'portable'
    $tests = @(
        "$testClass.Topology_migrations_tenant_isolation_and_graph_paths_work",
        "$testClass.Fraud_migrations_tenant_isolation_and_graph_investigations_work"
    )
}

[pscustomobject][ordered]@{
    releaseVersion = [string]$tracks.releaseVersion
    releaseTrack = $ReleaseTrack
    image = $image
    milestone = $milestone
    filter = "ReleaseTrack=$ReleaseTrack"
    expectedTests = $tests
    stableGraphQualification = $false
}
