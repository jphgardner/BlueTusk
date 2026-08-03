[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportPath,

    [Parameter(Mandatory)]
    [TimeSpan] $RequiredDuration,

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [long] $MinimumCycles,

    [string] $ExpectedCommit
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

if ($report.formatVersion -ne 3)
{
    $failures.Add("Expected report format 3; found '$($report.formatVersion)'.")
}

if ($report.completed -ne $true)
{
    $failures.Add('The endurance runner did not mark the report complete.')
}

if ($report.preparationCompleted -ne $true)
{
    $failures.Add('The isolated restore/build preparation did not complete.')
}

$requestedDuration = [TimeSpan]::Parse(
    [string]$report.requestedDuration,
    [Globalization.CultureInfo]::InvariantCulture)
$actualDuration = [TimeSpan]::Parse(
    [string]$report.actualDuration,
    [Globalization.CultureInfo]::InvariantCulture)
if ($requestedDuration -lt $RequiredDuration)
{
    $failures.Add(
        "Requested duration $requestedDuration is below required duration $RequiredDuration.")
}

if ($actualDuration -lt $requestedDuration)
{
    $failures.Add(
        "Actual duration $actualDuration is below requested duration $requestedDuration.")
}

if ([long]$report.cycles -lt $MinimumCycles)
{
    $failures.Add(
        "Completed $($report.cycles) cycles, below the required $MinimumCycles.")
}

if ([long]$report.minimumCycles -lt $MinimumCycles)
{
    $failures.Add(
        "Runner minimum $($report.minimumCycles) is below the required $MinimumCycles.")
}

$projects = @($report.projects)
$expectedProjectRuns = [long]$report.cycles * $projects.Count
if ([long]$report.projectRuns -ne $expectedProjectRuns)
{
    $failures.Add(
        "Project runs $($report.projectRuns) do not equal " +
        "$($report.cycles) cycle(s) x $($projects.Count) project(s).")
}

if ($projects.Count -ne 6)
{
    $failures.Add("Expected six Sync endurance projects; found $($projects.Count).")
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and
    -not [string]::Equals(
        [string]$report.sourceCommit,
        $ExpectedCommit,
        [StringComparison]::OrdinalIgnoreCase))
{
    $failures.Add(
        "Source commit '$($report.sourceCommit)' does not match expected commit '$ExpectedCommit'.")
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
    $failures.Add('The isolated source commit changed or did not match the recorded source.')
}

if ($report.trackedWorktreeCleanAtStart -ne $true -or
    $report.isolatedTrackedWorktreeCleanAtEnd -ne $true)
{
    $failures.Add('The launch or isolated tracked worktree integrity check failed.')
}

$startHash = [string]$report.testArtifactHashAtStart
$endHash = [string]$report.testArtifactHashAtEnd
if ($startHash -notmatch '^[0-9a-f]{64}$' -or
    -not [string]::Equals($startHash, $endHash, [StringComparison]::Ordinal))
{
    $failures.Add('The isolated test artifact SHA-256 fingerprint changed or is invalid.')
}

if ([long]$report.testArtifactFileCount -le 0)
{
    $failures.Add('The report did not fingerprint any test artifacts.')
}

if ($report.isolatedWorktreeRemoved -ne $true)
{
    $failures.Add('The isolated worktree was not removed.')
}

if (-not [string]::Equals(
        [string]$report.configuration,
        'Release',
        [StringComparison]::Ordinal))
{
    $failures.Add("Expected Release configuration; found '$($report.configuration)'.")
}

if ($null -ne $report.failedPhase -or
    $null -ne $report.failedProject -or
    $null -ne $report.failureCycle -or
    $null -ne $report.failureExitCode)
{
    $failures.Add('The completed report contains failure metadata.')
}

if ($failures.Count -gt 0)
{
    throw "Sync endurance report validation failed:`n- $($failures -join "`n- ")"
}

Write-Output (
    "Verified Sync endurance report format 3 for commit $($report.sourceCommit): " +
    "$($report.cycles) cycles, $($report.projectRuns) project runs, " +
    "duration $actualDuration, $($report.testArtifactFileCount) immutable artifacts.")
