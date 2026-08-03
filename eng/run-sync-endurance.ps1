[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [TimeSpan] $Duration,

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [long] $MinimumCycles,

    [Parameter(Mandatory)]
    [string] $ReportPath,

    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateRange(0, 60000)]
    [int] $IntervalMilliseconds = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Duration -lt [TimeSpan]::FromSeconds(1))
{
    throw 'Duration must be at least one second.'
}

$requiredEnvironment = @(
    'BLUETUSK_TEST_CONNECTION_STRING',
    'BLUETUSK_NATS_URL',
    'BLUETUSK_TEST_REDIS_CONNECTION_STRING',
    'BLUETUSK_OPENSEARCH_URL'
)
foreach ($name in $requiredEnvironment)
{
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name)))
    {
        throw "Required endurance environment variable '$name' is not configured."
    }
}

$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
$sourceCommit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit))
{
    throw 'Sync endurance could not resolve the source commit.'
}

$sourceBranch = (& git -C $RepositoryRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw 'Sync endurance could not resolve the source branch.'
}

$trackedStatus = (& git -C $RepositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0)
{
    throw 'Sync endurance could not inspect the repository status.'
}

if (-not [string]::IsNullOrWhiteSpace(($trackedStatus -join [Environment]::NewLine)))
{
    throw 'Sync endurance requires a clean tracked worktree so its report identifies the exact tested source.'
}

$fullReportPath = if ([IO.Path]::IsPathRooted($ReportPath))
{
    [IO.Path]::GetFullPath($ReportPath)
}
else
{
    [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $ReportPath))
}

$projects = @(
    'tests/BlueTusk.Sync.Tests/BlueTusk.Sync.Tests.csproj',
    'tests/BlueTusk.Sync.DependencyInjection.Tests/BlueTusk.Sync.DependencyInjection.Tests.csproj',
    'tests/BlueTusk.Sync.Testing.Tests/BlueTusk.Sync.Testing.Tests.csproj',
    'tests/BlueTusk.Sync.Nats.Tests/BlueTusk.Sync.Nats.Tests.csproj',
    'tests/BlueTusk.Sync.Redis.Tests/BlueTusk.Sync.Redis.Tests.csproj',
    'tests/BlueTusk.Sync.OpenSearch.Tests/BlueTusk.Sync.OpenSearch.Tests.csproj'
)

$startedAt = [DateTimeOffset]::UtcNow
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$cycles = 0L
$projectRuns = 0L
$maximumCycleMilliseconds = 0L
$failedProject = $null
$failureCycle = $null
$failureExitCode = $null
$completed = $false

try
{
    while ($stopwatch.Elapsed -lt $Duration -or $cycles -eq 0)
    {
        $cycleStopwatch = [Diagnostics.Stopwatch]::StartNew()
        foreach ($project in $projects)
        {
            $projectPath = Join-Path $RepositoryRoot $project
            & dotnet test $projectPath `
                --configuration $Configuration `
                --no-build `
                --no-restore `
                --verbosity quiet
            $projectRuns++
            if ($LASTEXITCODE -ne 0)
            {
                $failedProject = $project
                $failureCycle = $cycles + 1
                $failureExitCode = $LASTEXITCODE
                throw "Sync endurance project '$project' failed in cycle $failureCycle with exit code $LASTEXITCODE."
            }
        }

        $cycles++
        $cycleStopwatch.Stop()
        $maximumCycleMilliseconds = [Math]::Max(
            $maximumCycleMilliseconds,
            [long]$cycleStopwatch.Elapsed.TotalMilliseconds)
        if ($IntervalMilliseconds -gt 0)
        {
            Start-Sleep -Milliseconds $IntervalMilliseconds
        }
    }

    if ($cycles -lt $MinimumCycles)
    {
        throw "Sync endurance completed $cycles cycle(s), below the required minimum of $MinimumCycles."
    }

    $completed = $true
}
finally
{
    $stopwatch.Stop()
    $reportDirectory = Split-Path $fullReportPath -Parent
    [IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
    $report = [ordered]@{
        formatVersion = 2
        sourceCommit = $sourceCommit
        sourceBranch = $sourceBranch
        trackedWorktreeCleanAtStart = $true
        startedAt = $startedAt.ToString('O')
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        requestedDuration = $Duration.ToString('c')
        actualDuration = $stopwatch.Elapsed.ToString('c')
        completed = $completed
        cycles = $cycles
        projectRuns = $projectRuns
        minimumCycles = $MinimumCycles
        maximumCycleMilliseconds = $maximumCycleMilliseconds
        failedProject = $failedProject
        failureCycle = $failureCycle
        failureExitCode = $failureExitCode
        configuration = $Configuration
        dotnetVersion = (& dotnet --version)
        operatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        processorCount = [Environment]::ProcessorCount
        projects = $projects
    }
    [IO.File]::WriteAllText(
        $fullReportPath,
        ($report | ConvertTo-Json -Depth 4),
        [Text.UTF8Encoding]::new($false))
}

Write-Output "Sync endurance completed $cycles cycle(s) and $projectRuns project run(s); report=$fullReportPath"
