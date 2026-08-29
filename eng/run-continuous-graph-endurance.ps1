[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [TimeSpan] $Duration,

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [long] $MinimumEvaluations,

    [Parameter(Mandatory)]
    [string] $ReportPath,

    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),

    [string] $CandidateProvenancePath,

    [string] $PostgreSqlImage,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateRange(0, 60000)]
    [int] $IntervalMilliseconds = 250
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Duration -lt [TimeSpan]::FromSeconds(1))
{
    throw 'Duration must be at least one second.'
}

$connectionString = [Environment]::GetEnvironmentVariable(
    'BLUETUSK_TEST_CONNECTION_STRING')
if ([string]::IsNullOrWhiteSpace($connectionString))
{
    throw "Required environment variable 'BLUETUSK_TEST_CONNECTION_STRING' is not configured."
}

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$sourceCommit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$')
{
    throw 'ContinuousGraph endurance could not resolve the source commit.'
}

$sourceBranch = ([string](& git -C $RepositoryRoot branch --show-current)).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw 'ContinuousGraph endurance could not resolve the source branch.'
}

$trackedStatus = (& git -C $RepositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0)
{
    throw 'ContinuousGraph endurance could not inspect the repository status.'
}
if (-not [string]::IsNullOrWhiteSpace(($trackedStatus -join [Environment]::NewLine)))
{
    throw (
        'ContinuousGraph endurance requires a clean tracked worktree so the ' +
        'report identifies the exact tested source.')
}

$candidateProvenanceSha256 = $null
$candidateArtifacts = @()
if (-not [string]::IsNullOrWhiteSpace($CandidateProvenancePath))
{
    $resolvedProvenancePath = (
        Resolve-Path -LiteralPath $CandidateProvenancePath).Path
    $provenance = Get-Content -LiteralPath $resolvedProvenancePath -Raw |
        ConvertFrom-Json
    if ($provenance.schemaVersion -ne 1 -or
        $provenance.sourceTreeDirty -eq $true -or
        -not [string]::Equals(
            [string]$provenance.sourceCommit,
            $sourceCommit,
            [StringComparison]::OrdinalIgnoreCase) -or
        @($provenance.artifacts).Count -eq 0)
    {
        throw (
            'ContinuousGraph candidate provenance is incomplete, dirty, or ' +
            'for another commit.')
    }

    $candidateProvenanceSha256 = (
        Get-FileHash -LiteralPath $resolvedProvenancePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $candidateArtifacts = @($provenance.artifacts)
}

if ($Duration -ge [TimeSpan]::FromHours(24) -and
    ([string]::IsNullOrWhiteSpace($candidateProvenanceSha256) -or
     [string]$PostgreSqlImage -notmatch
        '^postgres:19(?:\.0)?[^@\s]*@sha256:[0-9a-f]{64}$'))
{
    throw (
        'The 24-hour ContinuousGraph gate requires clean candidate-package ' +
        'provenance and a digest-pinned PostgreSQL 19 GA image.')
}

$fullReportPath = if ([IO.Path]::IsPathRooted($ReportPath))
{
    [IO.Path]::GetFullPath($ReportPath)
}
else
{
    [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $ReportPath))
}
$reportDirectory = Split-Path $fullReportPath -Parent
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
$harnessReportPath = Join-Path $reportDirectory 'harness-report.json'
$resultsDirectory = Join-Path $reportDirectory 'test-results'
[IO.Directory]::CreateDirectory($resultsDirectory) | Out-Null

$project = 'tests/BlueTusk.StressTests/BlueTusk.StressTests.csproj'
$testClass = 'BlueTusk.StressTests.ContinuousGraphEnduranceTests'
$phaseTests = [ordered]@{
    'restart-seed' = (
        "$testClass.Process_restart_seed_persists_replay_state")
    'restart-resume' = (
        "$testClass.Process_restart_resume_reads_and_advances_replay_state")
    'run' = (
        "$testClass.Continuous_graph_survives_repair_restart_cancellation_and_disconnect")
}
$startedUtc = [DateTimeOffset]::UtcNow
$testExitCode = $null
$phaseExitCodes = [ordered]@{}
$harness = $null
$completed = $false
$failure = $null

$previousDuration = [Environment]::GetEnvironmentVariable(
    'BLUETUSK_GRAPH_ENDURANCE_DURATION')
$previousMinimum = [Environment]::GetEnvironmentVariable(
    'BLUETUSK_GRAPH_ENDURANCE_MIN_EVALUATIONS')
$previousInterval = [Environment]::GetEnvironmentVariable(
    'BLUETUSK_GRAPH_ENDURANCE_INTERVAL_MS')
$previousReport = [Environment]::GetEnvironmentVariable(
    'BLUETUSK_GRAPH_ENDURANCE_REPORT')
$previousPhase = [Environment]::GetEnvironmentVariable(
    'BLUETUSK_GRAPH_ENDURANCE_PHASE')

try
{
    [Environment]::SetEnvironmentVariable(
        'BLUETUSK_GRAPH_ENDURANCE_DURATION',
        $Duration.ToString('c', [Globalization.CultureInfo]::InvariantCulture))
    [Environment]::SetEnvironmentVariable(
        'BLUETUSK_GRAPH_ENDURANCE_MIN_EVALUATIONS',
        $MinimumEvaluations.ToString([Globalization.CultureInfo]::InvariantCulture))
    [Environment]::SetEnvironmentVariable(
        'BLUETUSK_GRAPH_ENDURANCE_INTERVAL_MS',
        $IntervalMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture))
    [Environment]::SetEnvironmentVariable(
        'BLUETUSK_GRAPH_ENDURANCE_REPORT',
        $harnessReportPath)

    Push-Location $RepositoryRoot
    try
    {
        foreach ($phase in $phaseTests.Keys)
        {
            [Environment]::SetEnvironmentVariable(
                'BLUETUSK_GRAPH_ENDURANCE_PHASE',
                $phase)
            $filter = "FullyQualifiedName=$($phaseTests[$phase])"
            & dotnet test $project `
                --configuration $Configuration `
                --filter $filter `
                --logger "trx;LogFileName=continuous-graph-$phase.trx" `
                --results-directory $resultsDirectory
            $phaseExitCodes[$phase] = $LASTEXITCODE
            $testExitCode = $LASTEXITCODE
            if ($testExitCode -ne 0)
            {
                throw (
                    "ContinuousGraph endurance phase '$phase' exited with " +
                    "code $testExitCode.")
            }
        }
    }
    finally
    {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath $harnessReportPath -PathType Leaf))
    {
        throw 'ContinuousGraph endurance did not produce a harness report.'
    }

    $harness = Get-Content -LiteralPath $harnessReportPath -Raw |
        ConvertFrom-Json
    $completed = $true
}
catch
{
    $failure = $_.Exception.Message
    throw
}
finally
{
    [Environment]::SetEnvironmentVariable(
        'BLUETUSK_GRAPH_ENDURANCE_DURATION',
        $previousDuration)
    [Environment]::SetEnvironmentVariable(
        'BLUETUSK_GRAPH_ENDURANCE_MIN_EVALUATIONS',
        $previousMinimum)
    [Environment]::SetEnvironmentVariable(
        'BLUETUSK_GRAPH_ENDURANCE_INTERVAL_MS',
        $previousInterval)
    [Environment]::SetEnvironmentVariable(
        'BLUETUSK_GRAPH_ENDURANCE_REPORT',
        $previousReport)
    [Environment]::SetEnvironmentVariable(
        'BLUETUSK_GRAPH_ENDURANCE_PHASE',
        $previousPhase)

    $completedUtc = [DateTimeOffset]::UtcNow
    $actualDuration = if ($null -ne $harness)
    {
        [string]$harness.actualDuration
    }
    else
    {
        ($completedUtc - $startedUtc).ToString(
            'c',
            [Globalization.CultureInfo]::InvariantCulture)
    }
    $report = [ordered]@{
        formatVersion = 1
        completed = $completed
        sourceCommit = $sourceCommit.ToLowerInvariant()
        sourceBranch = $sourceBranch
        trackedWorktreeCleanAtStart = $true
        candidateProvenanceSha256 = $candidateProvenanceSha256
        candidateArtifacts = $candidateArtifacts
        postgresqlImage = $PostgreSqlImage
        project = $project
        phaseTests = $phaseTests
        phaseExitCodes = $phaseExitCodes
        configuration = $Configuration
        runtimeVersion = (& dotnet --version).Trim()
        operatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        requestedDuration = $Duration.ToString(
            'c',
            [Globalization.CultureInfo]::InvariantCulture)
        actualDuration = $actualDuration
        minimumEvaluations = $MinimumEvaluations
        intervalMilliseconds = $IntervalMilliseconds
        startedUtc = $startedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        testExitCode = $testExitCode
        failure = $failure
        harness = $harness
    }
    $report | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $fullReportPath -Encoding utf8NoBOM
}

Write-Host (
    "ContinuousGraph endurance completed $($harness.evaluations) evaluations " +
    "with P95 $($harness.lifecycleP95Milliseconds) ms.")
