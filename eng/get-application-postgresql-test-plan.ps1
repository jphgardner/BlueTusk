[CmdletBinding()]
param(
    [ValidateSet('Core', 'ContinuousGraphPreview')]
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
    $contract = Get-Content -LiteralPath (
        Join-Path $ConfigurationDirectory 'v1.2-candidate-readiness.json') -Raw | ConvertFrom-Json
    $image = [string]$contract.endurancePostgreSqlImage
    if ($contract.schemaVersion -ne 1 -or $contract.scope -cne 'Core' -or
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
    $programme = Get-Content -LiteralPath (
        Join-Path $ConfigurationDirectory 'postgresql19-programme.json') -Raw | ConvertFrom-Json
    # A newly announced milestone is not evidence. Use the explicitly qualified
    # historical fixture; never fall through to an unqualified current image.
    $milestone = [string]$programme.lastVerifiedMilestone
    $milestoneRecords = @($programme.milestones | Where-Object { $_.version -ceq $milestone })
    if ($programme.schemaVersion -ne 1 -or
        $milestone -cne $tracks.graph.historicalPreviewMilestone -or
        $milestoneRecords.Count -ne 1 -or $milestoneRecords[0].status -cne 'verified')
    {
        throw 'Graph application tests require exactly one qualified historical SQL/PGQ fixture.'
    }
    $image = [string]$milestoneRecords[0].image
    $expectedPattern = '^postgres:' + [regex]::Escape($milestone) + '-alpine@sha256:[0-9a-f]{64}$'
    if ($image -cnotmatch $expectedPattern)
    {
        throw 'The historical Graph application fixture must have its exact milestone and immutable image digest.'
    }
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
