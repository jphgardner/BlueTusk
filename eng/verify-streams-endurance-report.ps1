[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportPath,

    [Parameter(Mandatory)]
    [TimeSpan] $RequiredDuration,

    [Parameter(Mandatory)]
    [ValidateRange(260, [long]::MaxValue)]
    [long] $MinimumTransactions,

    [string] $ExpectedCommit,

    [string] $CandidateProvenancePath,

    [string] $ExpectedPostgreSqlImage
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

if ($report.formatVersion -ne 1)
{
    $failures.Add("Expected report format 1; found '$($report.formatVersion)'.")
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

$releaseDuration = [TimeSpan]::FromHours(72)
if ($RequiredDuration -ge $releaseDuration)
{
    if ([string]$report.candidateProvenanceSha256 -notmatch '^[0-9a-f]{64}$' -or
        @($report.candidateArtifacts).Count -eq 0)
    {
        $failures.Add('The release report has no candidate-package provenance.')
    }
    if ([string]$report.postgresqlImage -notmatch
        '^postgres:19[^@\s]+@sha256:[0-9a-f]{64}$')
    {
        $failures.Add('The release report has no digest-pinned PostgreSQL image.')
    }
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
        $failures.Add('Candidate provenance hash or source commit does not match the report.')
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
            $failures.Add(
                "Candidate artifact '$($artifact.path)' does not match the report.")
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedPostgreSqlImage) -and
    -not [string]::Equals(
        [string]$report.postgresqlImage,
        $ExpectedPostgreSqlImage,
        [StringComparison]::Ordinal))
{
    $failures.Add('The PostgreSQL image does not match the expected candidate digest.')
}

if ([long]$report.minimumTransactions -lt $MinimumTransactions)
{
    $failures.Add(
        "Runner minimum $($report.minimumTransactions) is below the required $MinimumTransactions.")
}

if (-not [string]::Equals(
        [string]$report.project,
        'tests/BlueTusk.StressTests/BlueTusk.StressTests.csproj',
        [StringComparison]::Ordinal))
{
    $failures.Add("Unexpected Streams endurance project '$($report.project)'.")
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

if ([long]$report.testExitCode -ne 0)
{
    $failures.Add("The endurance test exited with code '$($report.testExitCode)'.")
}

if ($null -ne $report.failedPhase -or $null -ne $report.failureExitCode)
{
    $failures.Add('The completed report contains failure metadata.')
}

if ($null -eq $report.harness)
{
    $failures.Add('The report does not contain harness evidence.')
}
else
{
    $harnessRequestedDuration = [TimeSpan]::Parse(
        [string]$report.harness.requestedDuration,
        [Globalization.CultureInfo]::InvariantCulture)
    $harnessActualDuration = [TimeSpan]::Parse(
        [string]$report.harness.actualDuration,
        [Globalization.CultureInfo]::InvariantCulture)
    if ($harnessRequestedDuration -lt $RequiredDuration)
    {
        $failures.Add(
            "Harness requested duration $harnessRequestedDuration is below $RequiredDuration.")
    }

    if ($harnessActualDuration -lt $harnessRequestedDuration)
    {
        $failures.Add(
            "Harness actual duration $harnessActualDuration is below its requested duration.")
    }

    if ([long]$report.harness.transactions -lt $MinimumTransactions)
    {
        $failures.Add(
            "Harness completed $($report.harness.transactions) transactions, below $MinimumTransactions.")
    }

    foreach ($counter in @(
            'duplicateAppends',
            'replayedDeliveries',
            'generationConflicts',
            'fencedLeases',
            'relayRestarts'))
    {
        if ([long]$report.harness.$counter -le 0)
        {
            $failures.Add("Harness counter '$counter' is not positive.")
        }
    }

    if ([long]$report.harness.maximumStorageBytes -gt 64L * 1024 * 1024)
    {
        $failures.Add(
            "Maximum relay storage $($report.harness.maximumStorageBytes) exceeds 64 MiB.")
    }

    if ([long]$report.harness.finalStorageBytes -ne 0)
    {
        $failures.Add(
            "Final relay storage is $($report.harness.finalStorageBytes) bytes instead of zero.")
    }
}

if ($failures.Count -gt 0)
{
    throw "Streams endurance report validation failed:`n- $($failures -join "`n- ")"
}

Write-Output (
    "Verified Streams endurance report format 1 for commit $($report.sourceCommit): " +
    "$($report.harness.transactions) transactions, duration " +
    "$($report.harness.actualDuration), " +
    "$($report.testArtifactFileCount) immutable artifacts.")
