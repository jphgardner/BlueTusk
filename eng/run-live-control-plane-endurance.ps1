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

    [string] $CandidateProvenancePath,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateRange(0, 60000)]
    [int] $IntervalMilliseconds = 250
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TestArtifactFingerprint
{
    param(
        [Parameter(Mandatory)]
        [string] $Root,

        [Parameter(Mandatory)]
        [string] $ArtifactRoot
    )

    $files = @(Get-ChildItem -LiteralPath $ArtifactRoot -Recurse -File |
        Sort-Object FullName -Unique)
    if ($files.Count -eq 0)
    {
        throw 'Live/Control Plane endurance found no isolated test artifacts.'
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
    return [pscustomobject]@{
        Hash = [Convert]::ToHexString($aggregate).ToLowerInvariant()
        FileCount = $files.Count
    }
}

function ConvertTo-TimeSpan
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $parsed = [TimeSpan]::Zero
    if (-not [TimeSpan]::TryParse(
            [string]$Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed))
    {
        throw "Live/Control Plane harness report has an invalid $Name."
    }
    return $parsed
}

if ($Duration -lt [TimeSpan]::FromSeconds(1))
{
    throw 'Duration must be at least one second.'
}

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$sourceCommit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$')
{
    throw 'Live/Control Plane endurance could not resolve the source commit.'
}
$sourceBranch = (& git -C $RepositoryRoot branch --show-current).Trim()
$trackedStatus = (& git -C $RepositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or
    -not [string]::IsNullOrWhiteSpace(($trackedStatus -join [Environment]::NewLine)))
{
    throw 'Live/Control Plane endurance requires a clean tracked worktree.'
}

$candidateProvenanceSha256 = $null
$candidateArtifacts = @()
if (-not [string]::IsNullOrWhiteSpace($CandidateProvenancePath))
{
    $resolvedProvenancePath = (Resolve-Path -LiteralPath $CandidateProvenancePath).Path
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
        throw 'Candidate provenance is incomplete, dirty, or for another commit.'
    }
    $candidateProvenanceSha256 = (
        Get-FileHash -LiteralPath $resolvedProvenancePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $candidateArtifacts = @($provenance.artifacts)
}
if ($Duration -ge [TimeSpan]::FromHours(24) -and
    [string]::IsNullOrWhiteSpace($candidateProvenanceSha256))
{
    throw 'The 24-hour Live/Control Plane gate requires candidate-package provenance.'
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

$project = 'tests/BlueTusk.StressTests/BlueTusk.StressTests.csproj'
$startedAt = [DateTimeOffset]::UtcNow
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$failedPhase = $null
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
$testExitCode = $null
$harnessReport = $null
$launchRepositoryCommitAtEnd = $null
$launchRepositoryTrackedWorktreeCleanAtEnd = $false

try
{
    & git -C $RepositoryRoot worktree add --detach $fullWorktreePath $sourceCommit
    if ($LASTEXITCODE -ne 0)
    {
        $failedPhase = 'worktree'
        $failureExitCode = $LASTEXITCODE
        throw 'Could not create the isolated endurance worktree.'
    }
    $worktreeAdded = $true
    $isolatedSourceCommitAtStart = (& git -C $fullWorktreePath rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $isolatedSourceCommitAtStart -ne $sourceCommit)
    {
        $failedPhase = 'worktree'
        throw 'The isolated endurance worktree does not match the source commit.'
    }

    $projectPath = Join-Path $fullWorktreePath $project
    & dotnet restore $projectPath --verbosity quiet
    if ($LASTEXITCODE -ne 0)
    {
        $failedPhase = 'restore'
        $failureExitCode = $LASTEXITCODE
        throw 'Live/Control Plane endurance restore failed.'
    }
    & dotnet build $projectPath --configuration $Configuration --no-restore --verbosity quiet
    if ($LASTEXITCODE -ne 0)
    {
        $failedPhase = 'build'
        $failureExitCode = $LASTEXITCODE
        throw 'Live/Control Plane endurance build failed.'
    }

    $artifactRoot = Join-Path (Split-Path $projectPath -Parent) "bin/$Configuration"
    $startFingerprint = Get-TestArtifactFingerprint `
        -Root $fullWorktreePath `
        -ArtifactRoot $artifactRoot
    $testArtifactHashAtStart = $startFingerprint.Hash
    $testArtifactFileCount = $startFingerprint.FileCount
    $preparationCompleted = $true

    if (Test-Path -LiteralPath $harnessReportPath -PathType Leaf)
    {
        Remove-Item -LiteralPath $harnessReportPath -Force
    }

    $stopwatch.Restart()
    $startedAt = [DateTimeOffset]::UtcNow
    $env:BLUETUSK_LIVE_CONTROL_PLANE_ENDURANCE_DURATION = $Duration.ToString('c')
    $env:BLUETUSK_LIVE_CONTROL_PLANE_ENDURANCE_MIN_CYCLES =
        $MinimumCycles.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BLUETUSK_LIVE_CONTROL_PLANE_ENDURANCE_INTERVAL_MS =
        $IntervalMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BLUETUSK_LIVE_CONTROL_PLANE_ENDURANCE_REPORT = $harnessReportPath

    & dotnet test $projectPath `
        --configuration $Configuration `
        --no-build `
        --no-restore `
        --filter 'FullyQualifiedName~LiveControlPlaneEnduranceTests' `
        --logger 'trx;LogFileName=live-control-plane-endurance.trx' `
        --results-directory $resultsDirectory `
        --verbosity quiet
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0)
    {
        $failedPhase = 'test'
        $failureExitCode = $testExitCode
        throw 'Live/Control Plane endurance test failed.'
    }
    if (-not (Test-Path -LiteralPath $harnessReportPath -PathType Leaf))
    {
        $failedPhase = 'harness-report'
        throw 'The endurance test passed without producing a harness report.'
    }

    $harnessReport = Get-Content -LiteralPath $harnessReportPath -Raw |
        ConvertFrom-Json
    $harnessRequested = ConvertTo-TimeSpan `
        -Value $harnessReport.requestedDuration `
        -Name 'requested duration'
    $harnessActual = ConvertTo-TimeSpan `
        -Value $harnessReport.actualDuration `
        -Name 'actual duration'
    if ($harnessRequested -lt $Duration -or
        $harnessActual -lt $harnessRequested -or
        [long]$harnessReport.cycles -lt $MinimumCycles -or
        [long]$harnessReport.liveUpdates -ne [long]$harnessReport.cycles -or
        [long]$harnessReport.inventoryReads -ne [long]$harnessReport.cycles -or
        [long]$harnessReport.operationExecutions -ne [long]$harnessReport.cycles -or
        [long]$harnessReport.auditRecords -ne (2L * [long]$harnessReport.cycles) -or
        [int]$harnessReport.liveRowCount -ne 10000 -or
        [int]$harnessReport.deploymentCount -ne 256)
    {
        $failedPhase = 'harness-report'
        throw 'The endurance harness report does not satisfy its duration and correctness gates.'
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
                    -ArtifactRoot $artifactRoot
                $testArtifactHashAtEnd = $endFingerprint.Hash
                if ($endFingerprint.FileCount -ne $testArtifactFileCount -and
                    $null -eq $failedPhase)
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
        formatVersion = 1
        sourceCommit = $sourceCommit
        sourceBranch = $sourceBranch
        candidateProvenanceSha256 = $candidateProvenanceSha256
        candidateArtifacts = $candidateArtifacts
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
        minimumCycles = $MinimumCycles
        intervalMilliseconds = $IntervalMilliseconds
        completed = $completed
        preparationCompleted = $preparationCompleted
        testExitCode = $testExitCode
        failedPhase = $failedPhase
        failureExitCode = $failureExitCode
        configuration = $Configuration
        project = $project
        harness = $harnessReport
        dotnetVersion = (& dotnet --version)
        operatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        processArchitecture =
            [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        processorCount = [Environment]::ProcessorCount
    }
    [IO.File]::WriteAllText(
        $fullReportPath,
        ($report | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
}

if (-not $completed)
{
    throw "Live/Control Plane endurance did not pass; report=$fullReportPath"
}
Write-Output (
    "Live/Control Plane endurance completed $($harnessReport.cycles) cycle(s); " +
    "report=$fullReportPath")
