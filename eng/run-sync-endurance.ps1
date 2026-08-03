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

    [string] $IsolatedWorktreePath,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateRange(0, 60000)]
    [int] $IntervalMilliseconds = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TestArtifactFingerprint
{
    param(
        [Parameter(Mandatory)]
        [string] $Root,

        [Parameter(Mandatory)]
        [string[]] $ArtifactRoots
    )

    $files = @(
        $ArtifactRoots |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
            ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File } |
            Sort-Object FullName -Unique
    )
    if ($files.Count -eq 0)
    {
        throw 'Sync endurance found no isolated test artifacts to fingerprint.'
    }

    $entries = foreach ($file in $files)
    {
        $relativePath = ([IO.Path]::GetRelativePath($Root, $file.FullName)).Replace(
            [IO.Path]::DirectorySeparatorChar,
            [char]'/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        "$relativePath`t$($file.Length)`t$hash"
    }
    $manifest = [Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))
    $aggregate = [Security.Cryptography.SHA256]::HashData($manifest)
    [pscustomobject]@{
        Hash = [Convert]::ToHexString($aggregate).ToLowerInvariant()
        FileCount = $files.Count
    }
}

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
$reportDirectory = Split-Path $fullReportPath -Parent
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null

$fullWorktreePath = if ([string]::IsNullOrWhiteSpace($IsolatedWorktreePath))
{
    [IO.Path]::GetFullPath((Join-Path $reportDirectory 'worktree'))
}
elseif ([IO.Path]::IsPathRooted($IsolatedWorktreePath))
{
    [IO.Path]::GetFullPath($IsolatedWorktreePath)
}
else
{
    [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $IsolatedWorktreePath))
}

if (Test-Path -LiteralPath $fullWorktreePath)
{
    throw "The isolated endurance worktree already exists: '$fullWorktreePath'."
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
$failedPhase = $null
$failureCycle = $null
$failureExitCode = $null
$preparationCompleted = $false
$gateExecutionCompleted = $false
$completed = $false
$worktreeAdded = $false
$worktreeRemoved = $false
$isolatedSourceCommitAtStart = $null
$isolatedSourceCommitAtEnd = $null
$isolatedTrackedWorktreeCleanAtEnd = $false
$testArtifactHashAtStart = $null
$testArtifactHashAtEnd = $null
$testArtifactFileCount = 0
$launchRepositoryCommitAtEnd = $null
$launchRepositoryTrackedWorktreeCleanAtEnd = $false

try
{
    & git -C $RepositoryRoot worktree add --detach $fullWorktreePath $sourceCommit
    if ($LASTEXITCODE -ne 0)
    {
        $failedPhase = 'worktree'
        $failureExitCode = $LASTEXITCODE
        throw "Sync endurance could not create its isolated worktree (exit code $LASTEXITCODE)."
    }

    $worktreeAdded = $true
    $isolatedSourceCommitAtStart = (& git -C $fullWorktreePath rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $isolatedSourceCommitAtStart -ne $sourceCommit)
    {
        $failedPhase = 'worktree'
        $failureExitCode = $LASTEXITCODE
        throw 'Sync endurance isolated worktree does not match the requested source commit.'
    }

    foreach ($project in $projects)
    {
        $projectPath = Join-Path $fullWorktreePath $project
        & dotnet restore $projectPath --verbosity quiet
        if ($LASTEXITCODE -ne 0)
        {
            $failedProject = $project
            $failedPhase = 'restore'
            $failureExitCode = $LASTEXITCODE
            throw "Sync endurance restore failed for '$project' with exit code $LASTEXITCODE."
        }
    }

    foreach ($project in $projects)
    {
        $projectPath = Join-Path $fullWorktreePath $project
        & dotnet build $projectPath `
            --configuration $Configuration `
            --no-restore `
            --verbosity quiet
        if ($LASTEXITCODE -ne 0)
        {
            $failedProject = $project
            $failedPhase = 'build'
            $failureExitCode = $LASTEXITCODE
            throw "Sync endurance build failed for '$project' with exit code $LASTEXITCODE."
        }
    }

    $artifactRoots = @(
        foreach ($project in $projects)
        {
            $projectDirectory = Split-Path (Join-Path $fullWorktreePath $project) -Parent
            Join-Path $projectDirectory "bin/$Configuration"
        }
    )
    $startFingerprint = Get-TestArtifactFingerprint `
        -Root $fullWorktreePath `
        -ArtifactRoots $artifactRoots
    $testArtifactHashAtStart = $startFingerprint.Hash
    $testArtifactFileCount = $startFingerprint.FileCount
    $preparationCompleted = $true
    $stopwatch.Restart()
    $startedAt = [DateTimeOffset]::UtcNow

    while ($stopwatch.Elapsed -lt $Duration -or $cycles -eq 0)
    {
        $cycleStopwatch = [Diagnostics.Stopwatch]::StartNew()
        foreach ($project in $projects)
        {
            $projectPath = Join-Path $fullWorktreePath $project
            & dotnet test $projectPath `
                --configuration $Configuration `
                --no-build `
                --no-restore `
                --verbosity quiet
            $projectRuns++
            if ($LASTEXITCODE -ne 0)
            {
                $failedProject = $project
                $failedPhase = 'test'
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
        $failedPhase = 'minimum-cycles'
        throw "Sync endurance completed $cycles cycle(s), below the required minimum of $MinimumCycles."
    }

    $gateExecutionCompleted = $true
}
catch
{
    if ($null -eq $failedPhase)
    {
        $failedPhase = 'runner'
    }

    throw
}
finally
{
    $stopwatch.Stop()

    if ($worktreeAdded)
    {
        $isolatedSourceCommitAtEnd = (& git -C $fullWorktreePath rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -eq 0)
        {
            $isolatedSourceCommitAtEnd = $isolatedSourceCommitAtEnd.Trim()
        }
        else
        {
            $isolatedSourceCommitAtEnd = $null
        }

        $isolatedStatus = (& git -C $fullWorktreePath status --porcelain --untracked-files=no 2>$null)
        $isolatedTrackedWorktreeCleanAtEnd =
            $LASTEXITCODE -eq 0 -and
            [string]::IsNullOrWhiteSpace(($isolatedStatus -join [Environment]::NewLine))

        if ($preparationCompleted)
        {
            try
            {
                $endFingerprint = Get-TestArtifactFingerprint `
                    -Root $fullWorktreePath `
                    -ArtifactRoots $artifactRoots
                $testArtifactHashAtEnd = $endFingerprint.Hash
                if ($endFingerprint.FileCount -ne $testArtifactFileCount -and $null -eq $failedPhase)
                {
                    $failedPhase = 'artifact-integrity'
                }
            }
            catch
            {
                if ($null -eq $failedPhase)
                {
                    $failedPhase = 'artifact-integrity'
                }
            }
        }

        & git -C $RepositoryRoot worktree remove --force $fullWorktreePath 2>$null
        $worktreeRemoved = $LASTEXITCODE -eq 0
        if (-not $worktreeRemoved -and $null -eq $failedPhase)
        {
            $failedPhase = 'worktree-cleanup'
        }
    }

    $launchRepositoryCommitAtEnd = (& git -C $RepositoryRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0)
    {
        $launchRepositoryCommitAtEnd = $launchRepositoryCommitAtEnd.Trim()
    }
    else
    {
        $launchRepositoryCommitAtEnd = $null
    }

    $launchStatusAtEnd = (& git -C $RepositoryRoot status --porcelain --untracked-files=no 2>$null)
    $launchRepositoryTrackedWorktreeCleanAtEnd =
        $LASTEXITCODE -eq 0 -and
        [string]::IsNullOrWhiteSpace(($launchStatusAtEnd -join [Environment]::NewLine))

    $sourceIntegrityPassed =
        $isolatedSourceCommitAtStart -eq $sourceCommit -and
        $isolatedSourceCommitAtEnd -eq $sourceCommit -and
        $isolatedTrackedWorktreeCleanAtEnd
    $artifactIntegrityPassed =
        $preparationCompleted -and
        $testArtifactHashAtStart -eq $testArtifactHashAtEnd
    $cleanupPassed = -not $worktreeAdded -or $worktreeRemoved
    $completed =
        $gateExecutionCompleted -and
        $sourceIntegrityPassed -and
        $artifactIntegrityPassed -and
        $cleanupPassed

    if ($gateExecutionCompleted -and -not $sourceIntegrityPassed -and $null -eq $failedPhase)
    {
        $failedPhase = 'source-integrity'
    }
    elseif ($gateExecutionCompleted -and -not $artifactIntegrityPassed -and $null -eq $failedPhase)
    {
        $failedPhase = 'artifact-integrity'
    }

    $report = [ordered]@{
        formatVersion = 3
        sourceCommit = $sourceCommit
        sourceBranch = $sourceBranch
        trackedWorktreeCleanAtStart = $true
        isolatedWorkspaceKind = 'detached-git-worktree'
        isolatedSourceCommitAtStart = $isolatedSourceCommitAtStart
        isolatedSourceCommitAtEnd = $isolatedSourceCommitAtEnd
        isolatedTrackedWorktreeCleanAtEnd = $isolatedTrackedWorktreeCleanAtEnd
        testArtifactHashAlgorithm = 'SHA256'
        testArtifactHashAtStart = $testArtifactHashAtStart
        testArtifactHashAtEnd = $testArtifactHashAtEnd
        testArtifactFileCount = $testArtifactFileCount
        isolatedWorktreeRemoved = $worktreeRemoved
        launchRepositoryCommitAtEnd = $launchRepositoryCommitAtEnd
        launchRepositoryTrackedWorktreeCleanAtEnd =
            $launchRepositoryTrackedWorktreeCleanAtEnd
        startedAt = $startedAt.ToString('O')
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        requestedDuration = $Duration.ToString('c')
        actualDuration = $stopwatch.Elapsed.ToString('c')
        completed = $completed
        preparationCompleted = $preparationCompleted
        cycles = $cycles
        projectRuns = $projectRuns
        minimumCycles = $MinimumCycles
        maximumCycleMilliseconds = $maximumCycleMilliseconds
        failedPhase = $failedPhase
        failedProject = $failedProject
        failureCycle = $failureCycle
        failureExitCode = $failureExitCode
        configuration = $Configuration
        dotnetVersion = (& dotnet --version)
        operatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        processArchitecture =
            [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        processorCount = [Environment]::ProcessorCount
        projects = $projects
    }
    [IO.File]::WriteAllText(
        $fullReportPath,
        ($report | ConvertTo-Json -Depth 4),
        [Text.UTF8Encoding]::new($false))
}

if (-not $completed)
{
    throw "Sync endurance did not complete its source, artifact, duration, and cycle gates; report=$fullReportPath"
}

Write-Output "Sync endurance completed $cycles cycle(s) and $projectRuns project run(s); report=$fullReportPath"
