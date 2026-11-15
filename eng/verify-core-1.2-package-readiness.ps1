[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][hashtable] $RunIds,
    [Parameter(Mandatory)][string] $ProtectedEvidenceBase64,
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $Token = $env:GITHUB_TOKEN
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')
Import-Module (Join-Path $PSScriptRoot 'core-github-artifact-capture.psm1')
Import-Module (Join-Path $PSScriptRoot 'core-package-release-github.psm1')
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$contract = Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'v1.2-core-package-readiness.json')
$tracks = Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'release-tracks.json')
& (Join-Path $PSScriptRoot 'verify-release-track.ps1') | Out-Null

$workflowKeys = @($contract.requiredWorkflows.PSObject.Properties.Name)
if ($contract.schemaVersion -ne 1 -or $contract.releaseVersion -cne '1.2.0' -or $contract.releaseVersion -cne $tracks.releaseVersion -or
    $contract.scope -cne 'CorePackages' -or $contract.approvalTrack -cne 'CorePackages' -or $contract.usesProtectedPublication -ne $true -or
    @(Compare-Object @($tracks.stableFamilies) @($contract.families) -CaseSensitive).Count -ne 0 -or
    @(Compare-Object @($tracks.stablePostgreSqlMajors) @($contract.stablePostgreSqlMajors)).Count -ne 0 -or
    @(Compare-Object $workflowKeys @($RunIds.Keys) -CaseSensitive).Count -ne 0)
{
    throw 'The exact Core 1.2 package track requires its full workflow and stable database scope.'
}
$endurance = $contract.endurance
if ($endurance.streamsHours -ne 72 -or $endurance.streamsMinimumTransactions -ne 100000 -or
    $endurance.syncHours -ne 24 -or $endurance.syncMinimumCycles -ne 100 -or
    $endurance.liveControlPlaneHours -ne 24 -or $endurance.liveControlPlaneMinimumCycles -ne 100000)
{
    throw 'Core 1.2 requires the complete Streams, Sync, and Live/Control Plane endurance gates.'
}

$root = Join-Path $repositoryRoot 'artifacts/core-1.2-package-readiness'
if (Test-Path -LiteralPath $root) { throw 'Core package readiness requires a new evidence directory.' }
Initialize-CorePackageEvidence -Repository $Repository -Token $Token -Commit $ExpectedCommit
$runs = @{}
foreach ($key in $workflowKeys)
{
    $runId = 0L
    if (-not [long]::TryParse([string]$RunIds[$key], [ref]$runId) -or $runId -le 0)
    { throw "The $key workflow run ID must be a positive integer." }
    $runs[$key] = Get-CorePackageEvidenceRun -WorkflowFile $contract.requiredWorkflows.$key -RunId $runId
}

function Receive-Evidence([string] $Key, [string] $Prefix, [string] $Directory)
{
    $run = $runs[$Key]
    $name = "$Prefix-$($run.Record.runId)-$($run.Record.runAttempt)"
    Receive-CorePackageEvidenceArtifact -EvidenceRun $run -Name $name -Destination (Join-Path $root $Directory)
}

$packageArtifact = Receive-Evidence build "v1.2-core-packages-$ExpectedCommit" packages
$null = Receive-Evidence build "v1.2-core-test-evidence-$ExpectedCommit" tests
$null = Receive-Evidence runtime "verified-runtime-matrix-$ExpectedCommit" runtime
$null = Receive-Evidence performance "core-performance-evidence-qualification-$ExpectedCommit" performance
$null = Receive-Evidence streams streams-relay-endurance streams
$null = Receive-Evidence sync sync-endurance sync
$null = Receive-Evidence liveControlPlane live-control-plane-endurance live-control-plane

$packageArguments = @{
    EvidenceRoot = Join-Path $root 'packages'
    ExpectedCommit = $ExpectedCommit
    ReleaseTrack = 'Core'
    CoreReleaseVersion = '1.2.0'
}
& (Join-Path $PSScriptRoot 'verify-v1-package-evidence.ps1') @packageArguments | Out-Null
$manifest = Resolve-CoreEvidenceFile $root 'packages/package-manifest.json'
$provenance = Resolve-CoreEvidenceFile $root 'packages/sbom/build-provenance.json'
function Get-Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$provenanceHash = Get-Hash $provenance
foreach ($family in @('streams', 'sync', 'live-control-plane'))
{
    $bound = Resolve-CoreEvidenceFile $root "$family/candidate-sbom/build-provenance.json"
    $artifact = Read-CoreEvidenceJson (Resolve-CoreEvidenceFile $root "$family/candidate-package-artifact.json")
    if ((Get-Hash $bound) -cne $provenanceHash -or $artifact.artifactId -ne $packageArtifact.artifactId -or
        $artifact.runId -ne $packageArtifact.runId -or $artifact.sha256 -cne $packageArtifact.sha256 -or $artifact.sourceCommit -cne $ExpectedCommit)
    { throw "Endurance evidence for '$family' does not identify the canonical package artifact." }
}

foreach ($kind in @('Regression', 'Compatibility'))
{
    $folder = $kind.ToLowerInvariant()
    $testManifest = Resolve-CoreEvidenceFile $root "tests/$folder/core-$folder-manifest.json"
    & (Join-Path $PSScriptRoot 'verify-core-test-evidence.ps1') -EvidencePath $testManifest -ExpectedCommit $ExpectedCommit -Kind $kind -CoreReleaseVersion 1.2.0 | Out-Null
}
$runtime = Read-CoreEvidenceJson (Resolve-CoreEvidenceFile $root 'runtime/verified-runtime-matrix.json')
if ($runtime.schemaVersion -ne 1 -or $runtime.sourceCommit -cne $ExpectedCommit -or $runtime.fullRuntimeCoverage -ne $true -or
    $runtime.matrixComplete -ne $true -or $runtime.currentSourceValidated -ne $true -or $runtime.inventoryComplete -ne $true -or
    $runtime.verifiedPairs -le 0 -or $runtime.verifiedPairs -ne $runtime.requiredPairs -or $runtime.passedTests -le 0)
{ throw 'The exact candidate is missing a complete verified runtime matrix.' }

$perfManifest = Resolve-CoreEvidenceFile $root 'performance/performance/performance-leadership-evidence.json'
& (Join-Path $PSScriptRoot 'verify-performance-leadership-evidence.ps1') -EvidencePath $perfManifest -ExpectedCommit $ExpectedCommit -Scope Core | Out-Null

$streamsReport = Resolve-CoreEvidenceFile $root 'streams/report.json'
$streamsArguments = @{
    ReportPath = $streamsReport; ExpectedCommit = $ExpectedCommit; ReleaseTrack = 'Core'
    CandidateProvenancePath = $provenance; ExpectedPostgreSqlImage = $endurance.postgresqlImage
    RequiredDuration = [TimeSpan]::FromHours($endurance.streamsHours); MinimumTransactions = $endurance.streamsMinimumTransactions
}
& (Join-Path $PSScriptRoot 'verify-streams-endurance-report.ps1') @streamsArguments | Out-Null

$syncReport = Resolve-CoreEvidenceFile $root 'sync/report.json'
$destinations = @((Read-CoreEvidenceJson $syncReport).destinationImages)
if ($destinations.Count -ne 5 -or @($destinations | Where-Object { $_ -cnotmatch '^\S+@sha256:[0-9a-f]{64}$' }).Count -ne 0)
{ throw 'Sync endurance must identify its five digest-pinned destination images.' }
$syncArguments = @{
    ReportPath = $syncReport; ExpectedCommit = $ExpectedCommit; ReleaseTrack = 'Core'
    CandidateProvenancePath = $provenance; ExpectedPostgreSqlImage = $endurance.postgresqlImage
    ExpectedDestinationImages = $destinations
    RequiredDuration = [TimeSpan]::FromHours($endurance.syncHours); MinimumCycles = $endurance.syncMinimumCycles
}
& (Join-Path $PSScriptRoot 'verify-sync-endurance-report.ps1') @syncArguments | Out-Null
$connectors = Resolve-CoreEvidenceFile $root 'sync/sync-connector-validation.json'
& (Join-Path $PSScriptRoot 'verify-sync-connector-evidence.ps1') -EvidencePath $connectors -ExpectedCommit $ExpectedCommit -CoreReleaseVersion 1.2.0 | Out-Null

$liveArguments = @{
    ReportPath = Resolve-CoreEvidenceFile $root 'live-control-plane/report.json'
    ExpectedCommit = $ExpectedCommit; CandidateProvenancePath = $provenance
    RequiredDuration = [TimeSpan]::FromHours($endurance.liveControlPlaneHours); MinimumCycles = $endurance.liveControlPlaneMinimumCycles
}
& (Join-Path $PSScriptRoot 'verify-live-control-plane-endurance-report.ps1') @liveArguments | Out-Null

$archivePath = Join-Path $root 'protected-evidence.zip'
$protected = Join-Path $root 'protected'
$bytes = [Convert]::FromBase64String($ProtectedEvidenceBase64)
[IO.File]::WriteAllBytes($archivePath, $bytes)
$protectedEvidenceHash = Get-Hash $archivePath
$null = Get-CoreGithubArtifactArchive -Metadata @{ bytes = $bytes.LongLength; sha256 = $protectedEvidenceHash } -ArchivePath $archivePath
[IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $protected)
Remove-Item -LiteralPath $archivePath
$disturbanceArguments = @{
    EvidencePath = Resolve-CoreEvidenceFile $protected 'disturbances/operational-disturbance-evidence.json'
    EvidenceRoot = $protected; ExpectedCommit = $ExpectedCommit
    ExpectedPackageManifestSha256 = Get-Hash $manifest; ExpectedPackageProvenanceSha256 = $provenanceHash
    StreamsReportPath = $streamsReport; ExpectedStreamsReportSha256 = Get-Hash $streamsReport
    SyncReportPath = $syncReport; ExpectedSyncReportSha256 = Get-Hash $syncReport
}
& (Join-Path $PSScriptRoot 'verify-endurance-disturbance-evidence.ps1') @disturbanceArguments | Out-Null

$signoff = Read-CoreEvidenceJson (Resolve-CoreEvidenceFile $protected 'approvals/maintainer-signoff.json')
$records = @($workflowKeys | ForEach-Object { $runs[$_].Record })
$lastCompleted = ($records | ForEach-Object { [DateTimeOffset]$_.completedUtc } | Sort-Object -Descending)[0]
$approvalArguments = @{
    EvidenceDirectory = Join-Path $protected 'approvals'; ExpectedCommit = $ExpectedCommit
    ReleaseTrack = $contract.approvalTrack; NotBeforeUtc = $lastCompleted
    PublishedPrereleaseVersion = $signoff.details.publishedPrereleaseVersion
}
& (Join-Path $PSScriptRoot 'verify-v1-approval-evidence-set.ps1') @approvalArguments | Out-Null

[ordered]@{
    schemaVersion = 1; releaseVersion = $contract.releaseVersion; scope = $contract.scope
    sourceCommit = $ExpectedCommit; families = @($contract.families); workflowRuns = $records
    packageArtifact = $packageArtifact; packageManifestSha256 = Get-Hash $manifest; packageProvenanceSha256 = $provenanceHash
    protectedEvidenceSha256 = $protectedEvidenceHash; performanceEvidenceSha256 = Get-Hash $perfManifest
    verifiedRuntimePairs = $runtime.verifiedPairs
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'verified-core-package-readiness.json') -Encoding utf8NoBOM
Write-Output "Verified exact Core $($contract.releaseVersion) package readiness for $ExpectedCommit."
