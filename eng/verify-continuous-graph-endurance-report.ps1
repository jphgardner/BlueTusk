[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportPath,

    [Parameter(Mandatory)]
    [TimeSpan] $RequiredDuration,

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [long] $MinimumEvaluations,

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

$report = Get-Content -LiteralPath (
    Resolve-Path -LiteralPath $ReportPath).Path -Raw | ConvertFrom-Json
$failures = [Collections.Generic.List[string]]::new()

if ($report.formatVersion -ne 1)
{
    $failures.Add("Expected report format 1; found '$($report.formatVersion)'.")
}
if ($report.completed -ne $true -or [long]$report.testExitCode -ne 0)
{
    $failures.Add('The ContinuousGraph endurance run did not complete successfully.')
}
if ($null -ne $report.failure)
{
    $failures.Add('The completed report contains failure metadata.')
}
if (-not [string]::Equals(
        [string]$report.configuration,
        'Release',
        [StringComparison]::Ordinal))
{
    $failures.Add("Expected Release configuration; found '$($report.configuration)'.")
}
if (-not [string]::Equals(
        [string]$report.project,
        'tests/BlueTusk.StressTests/BlueTusk.StressTests.csproj',
        [StringComparison]::Ordinal))
{
    $failures.Add("Unexpected endurance project '$($report.project)'.")
}
$expectedPhaseTests = [ordered]@{
    'restart-seed' = (
        'BlueTusk.StressTests.ContinuousGraphEnduranceTests.' +
        'Process_restart_seed_persists_replay_state')
    'restart-resume' = (
        'BlueTusk.StressTests.ContinuousGraphEnduranceTests.' +
        'Process_restart_resume_reads_and_advances_replay_state')
    'run' = (
        'BlueTusk.StressTests.ContinuousGraphEnduranceTests.' +
        'Continuous_graph_survives_repair_restart_cancellation_and_disconnect')
}
if (@($report.phaseTests.PSObject.Properties).Count -ne
        $expectedPhaseTests.Count -or
    @($report.phaseExitCodes.PSObject.Properties).Count -ne
        $expectedPhaseTests.Count)
{
    $failures.Add(
        'The report does not contain exactly three process-isolated phases.')
}
foreach ($phase in $expectedPhaseTests.Keys)
{
    if (-not [string]::Equals(
            [string]$report.phaseTests.$phase,
            [string]$expectedPhaseTests[$phase],
            [StringComparison]::Ordinal) -or
        [long]$report.phaseExitCodes.$phase -ne 0)
    {
        $failures.Add(
            "Process-isolated phase '$phase' is missing, changed, or failed.")
    }
}
if ([string]$report.runtimeVersion -notmatch '^\d+\.\d+\.\d+' -or
    [string]::IsNullOrWhiteSpace([string]$report.operatingSystem) -or
    [string]::IsNullOrWhiteSpace([string]$report.architecture))
{
    $failures.Add('Runtime, operating-system, or architecture evidence is missing.')
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and
    -not [string]::Equals(
        [string]$report.sourceCommit,
        $ExpectedCommit,
        [StringComparison]::OrdinalIgnoreCase))
{
    $failures.Add(
        "Source commit '$($report.sourceCommit)' does not match '$ExpectedCommit'.")
}
if ($report.trackedWorktreeCleanAtStart -ne $true)
{
    $failures.Add('The endurance run did not start from a clean tracked worktree.')
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
        "Requested duration $requestedDuration is below $RequiredDuration.")
}
if ($actualDuration -lt $requestedDuration)
{
    $failures.Add(
        "Actual duration $actualDuration is below requested duration $requestedDuration.")
}
if ([long]$report.minimumEvaluations -lt $MinimumEvaluations)
{
    $failures.Add(
        "Runner minimum $($report.minimumEvaluations) is below $MinimumEvaluations.")
}

$releaseDuration = [TimeSpan]::FromHours(24)
if ($RequiredDuration -ge $releaseDuration)
{
    if ([string]$report.candidateProvenanceSha256 -notmatch '^[0-9a-f]{64}$' -or
        @($report.candidateArtifacts).Count -eq 0)
    {
        $failures.Add('The release report has no candidate package/SBOM provenance.')
    }
    if ([string]$report.postgresqlImage -notmatch
        '^postgres:19(?:\.0)?[^@\s]*@sha256:[0-9a-f]{64}$')
    {
        $failures.Add('The release report has no digest-pinned PostgreSQL 19 GA image.')
    }
}

if (-not [string]::IsNullOrWhiteSpace($CandidateProvenancePath))
{
    $provenancePath = (
        Resolve-Path -LiteralPath $CandidateProvenancePath).Path
    $provenance = Get-Content -LiteralPath $provenancePath -Raw |
        ConvertFrom-Json
    $provenanceHash = (
        Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($provenance.schemaVersion -ne 1 -or
        $provenance.sourceTreeDirty -eq $true -or
        $provenanceHash -ne [string]$report.candidateProvenanceSha256 -or
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
    $failures.Add('The PostgreSQL image does not match the expected GA digest.')
}

if ($null -eq $report.harness)
{
    $failures.Add('The report has no ContinuousGraph harness evidence.')
}
else
{
    $harnessRequested = [TimeSpan]::Parse(
        [string]$report.harness.requestedDuration,
        [Globalization.CultureInfo]::InvariantCulture)
    $harnessActual = [TimeSpan]::Parse(
        [string]$report.harness.actualDuration,
        [Globalization.CultureInfo]::InvariantCulture)
    if ($harnessRequested -lt $RequiredDuration -or
        $harnessActual -lt $harnessRequested)
    {
        $failures.Add('Harness duration does not satisfy the requested release duration.')
    }

    $evaluations = [long]$report.harness.evaluations
    $committed = [long]$report.harness.committedEvaluations
    if ($evaluations -lt $MinimumEvaluations)
    {
        $failures.Add(
            "Harness completed $evaluations evaluations, below $MinimumEvaluations.")
    }
    if ($evaluations -le 0 -or ($committed / [double]$evaluations) -lt 0.999)
    {
        $failures.Add('Committed evaluation outcomes are below 99.9 percent.')
    }
    if ([double]$report.harness.lifecycleP95Milliseconds -gt 1000)
    {
        $failures.Add('ContinuousGraph lifecycle P95 exceeds one second.')
    }
    foreach ($counter in @(
            'authoritativeRepairs',
            'processRestartRecoveries',
            'cancellationRecoveries',
            'disconnectRecoveries',
            'replayCorruptionDetections'))
    {
        if ([long]$report.harness.$counter -le 0)
        {
            $failures.Add("Required harness evidence '$counter' is missing.")
        }
    }
    if ($report.harness.cancellationCleanupVerified -ne $true)
    {
        $failures.Add('Cancellation cleanup and subsequent query recovery were not verified.')
    }
    if ([long]$report.harness.replaySequenceErrors -ne 0 -or
        [long]$report.harness.incorrectlyOrderedResults -ne 0 -or
        [long]$report.harness.unreconciledResults -ne 0)
    {
        $failures.Add(
            'The harness recorded incorrectly ordered or unreconciled results.')
    }
}

if ($failures.Count -gt 0)
{
    throw (
        "ContinuousGraph endurance evidence failed:`n - " +
        ($failures -join "`n - "))
}

Write-Host (
    "ContinuousGraph endurance evidence passed: " +
    "$($report.harness.evaluations) evaluations, " +
    "$($report.harness.lifecycleP95Milliseconds) ms P95.")
