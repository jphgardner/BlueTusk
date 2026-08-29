[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportPath,

    [Parameter(Mandatory)]
    [TimeSpan] $RequiredDuration,

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [long] $MinimumCycles,

    [string] $ExpectedCommit,

    [string] $CandidateProvenancePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($RequiredDuration -lt [TimeSpan]::FromSeconds(1))
{
    throw 'RequiredDuration must be at least one second.'
}

$fullReportPath = (Resolve-Path -LiteralPath $ReportPath).Path
$report = Get-Content -LiteralPath $fullReportPath -Raw | ConvertFrom-Json
$failures = [Collections.Generic.List[string]]::new()

if ($report.formatVersion -ne 1) { $failures.Add('Expected report format 1.') }
if ($report.completed -ne $true) { $failures.Add('The runner did not mark the report complete.') }
if ($report.preparationCompleted -ne $true) { $failures.Add('Restore/build preparation did not complete.') }

$requested = [TimeSpan]::Parse(
    [string]$report.requestedDuration,
    [Globalization.CultureInfo]::InvariantCulture)
$actual = [TimeSpan]::Parse(
    [string]$report.actualDuration,
    [Globalization.CultureInfo]::InvariantCulture)
if ($requested -lt $RequiredDuration) { $failures.Add('Requested duration is below the release gate.') }
if ($actual -lt $requested) { $failures.Add('Actual duration is below requested duration.') }

if ($RequiredDuration -ge [TimeSpan]::FromHours(24) -and
    ([string]$report.candidateProvenanceSha256 -notmatch '^[0-9a-f]{64}$' -or
     @($report.candidateArtifacts).Count -eq 0))
{
    $failures.Add('The 24-hour release report has no candidate-package provenance.')
}
if (-not [string]::IsNullOrWhiteSpace($CandidateProvenancePath))
{
    $provenancePath = (Resolve-Path -LiteralPath $CandidateProvenancePath).Path
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    $provenanceHash = (
        Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($provenanceHash -ne [string]$report.candidateProvenanceSha256 -or
        -not [string]::Equals(
            [string]$provenance.sourceCommit,
            [string]$report.sourceCommit,
            [StringComparison]::OrdinalIgnoreCase))
    {
        $failures.Add('Candidate provenance hash or source commit does not match.')
    }
    foreach ($artifact in @($provenance.artifacts))
    {
        $matches = @($report.candidateArtifacts | Where-Object {
            $_.path -eq $artifact.path -and
            $_.sha256 -eq $artifact.sha256 -and
            [long]$_.bytes -eq [long]$artifact.bytes
        })
        if ($matches.Count -ne 1)
        {
            $failures.Add("Candidate artifact '$($artifact.path)' does not match.")
        }
    }
}

if ([long]$report.minimumCycles -lt $MinimumCycles)
{
    $failures.Add('The runner minimum cycle count is below the required minimum.')
}
if ([string]$report.project -ne 'tests/BlueTusk.StressTests/BlueTusk.StressTests.csproj')
{
    $failures.Add("Unexpected endurance project '$($report.project)'.")
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and
    -not [string]::Equals(
        [string]$report.sourceCommit,
        $ExpectedCommit,
        [StringComparison]::OrdinalIgnoreCase))
{
    $failures.Add('The source commit does not match the expected candidate.')
}
if (-not [string]::Equals(
        [string]$report.sourceCommit,
        [string]$report.isolatedSourceCommitAtStart,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals(
        [string]$report.sourceCommit,
        [string]$report.isolatedSourceCommitAtEnd,
        [StringComparison]::OrdinalIgnoreCase))
{
    $failures.Add('The isolated source commit changed or did not match.')
}
if ($report.trackedWorktreeCleanAtStart -ne $true -or
    $report.isolatedTrackedWorktreeCleanAtEnd -ne $true)
{
    $failures.Add('A tracked worktree integrity check failed.')
}
$startHash = [string]$report.testArtifactHashAtStart
$endHash = [string]$report.testArtifactHashAtEnd
if ($startHash -notmatch '^[0-9a-f]{64}$' -or
    -not [string]::Equals($startHash, $endHash, [StringComparison]::Ordinal))
{
    $failures.Add('The isolated test artifact fingerprint changed or is invalid.')
}
if ([long]$report.testArtifactFileCount -le 0) { $failures.Add('No test artifacts were fingerprinted.') }
if ($report.isolatedWorktreeRemoved -ne $true) { $failures.Add('The isolated worktree was not removed.') }
if ([string]$report.configuration -ne 'Release') { $failures.Add('The gate did not use Release configuration.') }
if ([long]$report.testExitCode -ne 0) { $failures.Add('The endurance test process failed.') }
if ($null -ne $report.failedPhase -or $null -ne $report.failureExitCode)
{
    $failures.Add('The completed report contains failure metadata.')
}

if ($null -eq $report.harness)
{
    $failures.Add('The report has no harness evidence.')
}
else
{
    $harnessRequested = [TimeSpan]::Parse(
        [string]$report.harness.requestedDuration,
        [Globalization.CultureInfo]::InvariantCulture)
    $harnessActual = [TimeSpan]::Parse(
        [string]$report.harness.actualDuration,
        [Globalization.CultureInfo]::InvariantCulture)
    if ($harnessRequested -lt $RequiredDuration) { $failures.Add('Harness requested duration is too short.') }
    if ($harnessActual -lt $harnessRequested) { $failures.Add('Harness actual duration is too short.') }
    if ([long]$report.harness.cycles -lt $MinimumCycles) { $failures.Add('Harness completed too few cycles.') }
    if ([int]$report.harness.liveRowCount -ne 10000) { $failures.Add('Harness did not retain 10,000 Live rows.') }
    if ([int]$report.harness.deploymentCount -ne 256) { $failures.Add('Harness did not retain 256 deployments.') }
    foreach ($counter in @('liveUpdates', 'inventoryReads', 'operationExecutions'))
    {
        if ([long]$report.harness.$counter -ne [long]$report.harness.cycles)
        {
            $failures.Add("Harness counter '$counter' does not equal completed cycles.")
        }
    }
    if ([long]$report.harness.auditRecords -ne (2L * [long]$report.harness.cycles))
    {
        $failures.Add('Harness audit records do not prove Requested/Succeeded completion.')
    }
    if ([long]$report.harness.authoritativeChecks -le 0)
    {
        $failures.Add('Harness performed no authoritative Live drift checks.')
    }
    foreach ($measurement in @(
            'maximumCycleMilliseconds', 'maximumWorkingSetBytes', 'allocatedBytes'))
    {
        if ([long]$report.harness.$measurement -le 0)
        {
            $failures.Add("Harness measurement '$measurement' is not positive.")
        }
    }
}

if ($failures.Count -gt 0)
{
    throw "Live/Control Plane endurance verification failed:`n- $($failures -join "`n- ")"
}
Write-Output (
    "Verified Live/Control Plane endurance for $($report.sourceCommit): " +
    "$($report.harness.cycles) cycles across 10,000 Live rows and 256 deployments.")
