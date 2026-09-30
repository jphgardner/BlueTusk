[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) "bluetusk-core-endurance-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch
$commit = '1234567890abcdef1234567890abcdef12345678'
$image = 'postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873'
$provenancePath = Join-Path $scratch 'provenance.json'
@{ sourceCommit = $commit; artifacts = @(@{path = 'candidate.nupkg'; sha256 = ('a' * 64); bytes = 10}) } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $provenancePath -Encoding utf8NoBOM
$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
$provenanceHash = (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash.ToLowerInvariant()

function Common-Report
{
    return [ordered]@{
        sourceCommit = $commit
        candidateProvenanceSha256 = $provenanceHash
        candidateArtifacts = $provenance.artifacts
        postgresqlImage = $image
        completed = $true
        preparationCompleted = $true
        trackedWorktreeCleanAtStart = $true
        isolatedSourceCommitAtStart = $commit
        isolatedSourceCommitAtEnd = $commit
        isolatedTrackedWorktreeCleanAtEnd = $true
        testArtifactHashAtStart = ('b' * 64)
        testArtifactHashAtEnd = ('b' * 64)
        testArtifactFileCount = 10
        isolatedWorktreeRemoved = $true
        configuration = 'Release'
        testExitCode = 0
        failedPhase = $null
        failureExitCode = $null
    }
}
$streams = Common-Report
$streams.formatVersion = 1
$streams.project = 'tests/BlueTusk.StressTests/BlueTusk.StressTests.csproj'
$streams.requestedDuration = '3.00:00:00'
$streams.actualDuration = '3.00:00:01'
$streams.minimumTransactions = 100000
$streams.harness = @{
    requestedDuration = '3.00:00:00'; actualDuration = '3.00:00:00.1'
    transactions = 100000; duplicateAppends = 1; replayedDeliveries = 1
    generationConflicts = 1; fencedLeases = 1; relayRestarts = 1
    maximumStorageBytes = 1024; finalStorageBytes = 0
}
$sync = Common-Report
$sync.formatVersion = 4
$sync.requestedDuration = '1.00:00:00'
$sync.actualDuration = '1.00:00:01'
$sync.minimumCycles = 100
$sync.cycles = 100
$sync.projectRuns = 900
$sync.failedProject = $null
$sync.failureCycle = $null
$sync.projects = @(
    'tests/BlueTusk.Sync.Tests/BlueTusk.Sync.Tests.csproj',
    'tests/BlueTusk.Sync.DependencyInjection.Tests/BlueTusk.Sync.DependencyInjection.Tests.csproj',
    'tests/BlueTusk.Sync.Testing.Tests/BlueTusk.Sync.Testing.Tests.csproj',
    'tests/BlueTusk.Sync.Kafka.Tests/BlueTusk.Sync.Kafka.Tests.csproj',
    'tests/BlueTusk.Sync.Nats.Tests/BlueTusk.Sync.Nats.Tests.csproj',
    'tests/BlueTusk.Sync.Redis.Tests/BlueTusk.Sync.Redis.Tests.csproj',
    'tests/BlueTusk.Sync.OpenSearch.Tests/BlueTusk.Sync.OpenSearch.Tests.csproj',
    'tests/BlueTusk.Sync.S3.Tests/BlueTusk.Sync.S3.Tests.csproj',
    'tests/BlueTusk.Sync.Webhooks.Tests/BlueTusk.Sync.Webhooks.Tests.csproj'
)
$sync.destinationImages = @('redis:8-alpine', 'nats:2-alpine', 'apache/kafka:4', 'minio/minio:release', 'opensearchproject/opensearch:3') |
    ForEach-Object { "$_@sha256:$('c' * 64)" }

function Invoke-Fixture
{
    param($Fixture, [string] $Family, [string] $Track = 'Core')
    $path = Join-Path $scratch "$Family.json"
    $Fixture | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    $arguments = @{
        ReportPath = $path; ExpectedCommit = $commit
        CandidateProvenancePath = $provenancePath
        ExpectedPostgreSqlImage = $image; ReleaseTrack = $Track
    }
    if ($Family -eq 'streams')
    {
        & (Join-Path $PSScriptRoot 'verify-streams-endurance-report.ps1') @arguments `
            -RequiredDuration '3.00:00:00' -MinimumTransactions 100000 | Out-Null
    }
    else
    {
        & (Join-Path $PSScriptRoot 'verify-sync-endurance-report.ps1') @arguments `
            -RequiredDuration '1.00:00:00' -MinimumCycles 100 `
            -ExpectedDestinationImages $sync.destinationImages | Out-Null
    }
}
function Reject-Fixture
{
    param($Fixture, [string] $Family, [scriptblock] $Mutation, [string] $Name, [string] $Track = 'Core')
    $copy = $Fixture | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    & $Mutation $copy
    $rejected = $false
    try { Invoke-Fixture $copy $Family $Track } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid synthetic endurance fixture '$Name' was accepted." }
}
try
{
    Invoke-Fixture $streams 'streams'
    Invoke-Fixture $sync 'sync'
    foreach ($family in @('streams', 'sync'))
    {
        $fixture = if ($family -eq 'streams') { $streams } else { $sync }
        Reject-Fixture $fixture $family {param($r) $r.postgresqlImage = "postgres:19beta3-alpine@sha256:$('a' * 64)"} 'preview-image'
        Reject-Fixture $fixture $family {param($r) $r.postgresqlImage = 'postgres:18-alpine'} 'unpinned-image'
        Reject-Fixture $fixture $family {param($r) $r.requestedDuration = '00:00:10'} 'short-duration'
        Reject-Fixture $fixture $family {param($r) $r.actualDuration = '00:00:10'} 'short-observation'
        Reject-Fixture $fixture $family {param($r) $r.sourceCommit = ('0' * 40)} 'wrong-commit'
        Reject-Fixture $fixture $family {param($r) $r.candidateProvenanceSha256 = ('0' * 64)} 'wrong-provenance'
        Reject-Fixture $fixture $family {param($r) $r.testArtifactHashAtEnd = ('0' * 64)} 'changed-binary'
        Reject-Fixture $fixture $family {param($r) $r.completed = $false} 'incomplete'
        Reject-Fixture $fixture $family {param($r) $r.failureExitCode = 1} 'failed'
        Reject-Fixture $fixture $family {} 'stable-is-not-legacy-preview' 'Legacy'
    }
    Reject-Fixture $streams 'streams' {param($r) $r.harness.transactions = 99999} 'missing-transactions'
    Reject-Fixture $streams 'streams' {param($r) $r.harness.fencedLeases = 0} 'missing-fencing'
    Reject-Fixture $sync 'sync' {param($r) $r.cycles = 99} 'missing-cycles'
    Reject-Fixture $sync 'sync' {param($r) $r.projects[8] = $r.projects[7]} 'duplicate-connector'
    Reject-Fixture $sync 'sync' {param($r) $r.projects[8] = 'tests/BlueTusk.ContinuousGraph.Tests/BlueTusk.ContinuousGraph.Tests.csproj'} 'substitute-project'
    Reject-Fixture $sync 'sync' {param($r) $r.destinationImages = @($r.destinationImages | Select-Object -First 4)} 'missing-destination'
    Write-Output 'Synthetic core endurance self-tests passed: two stable fixtures and 26 rejected mutations. These are verifier tests, NOT completed endurance runs.'
}
finally
{
    if ((Split-Path $scratch -Parent) -ne [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar))
    { throw 'Refusing unexpected self-test cleanup target.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
