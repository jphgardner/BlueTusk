[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [TimeSpan] $Duration,

    [Parameter(Mandatory)]
    [ValidateRange(260, [long]::MaxValue)]
    [long] $MinimumTransactions,

    [Parameter(Mandatory)]
    [string] $ReportPath,

    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),

    [string] $IsolatedWorktreePath,

    [string] $CandidateProvenancePath,

    [string] $PostgreSqlImage,

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

    $files = @(
        Get-ChildItem -LiteralPath $ArtifactRoot -Recurse -File |
            Sort-Object FullName -Unique
    )
    if ($files.Count -eq 0)
    {
        throw 'Streams endurance found no isolated test artifacts to fingerprint.'
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
        throw "Streams endurance harness report contains an invalid $Name."
    }

    return $parsed
}

if ($Duration -lt [TimeSpan]::FromSeconds(1))
{
    throw 'Duration must be at least one second.'
}

$connectionString = [Environment]::GetEnvironmentVariable(
    'BLUETUSK_TEST_CONNECTION_STRING')
if ([string]::IsNullOrWhiteSpace($connectionString))
{
    throw "Required endurance environment variable 'BLUETUSK_TEST_CONNECTION_STRING' is not configured."
}

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$sourceCommit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit))
{
    throw 'Streams endurance could not resolve the source commit.'
}

$sourceBranchOutput = @(& git -C $RepositoryRoot branch --show-current)
if ($LASTEXITCODE -ne 0)
{
    throw 'Streams endurance could not resolve the source branch.'
}
$sourceBranch = ($sourceBranchOutput -join [Environment]::NewLine).Trim()

$trackedStatus = (& git -C $RepositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0)
{
    throw 'Streams endurance could not inspect the repository status.'
}

if (-not [string]::IsNullOrWhiteSpace(($trackedStatus -join [Environment]::NewLine)))
{
    throw 'Streams endurance requires a clean tracked worktree so its report identifies the exact tested source.'
}

$candidateProvenanceSha256 = $null
$candidateArtifacts = @()
if (-not [string]::IsNullOrWhiteSpace($CandidateProvenancePath))
{
    $resolvedCandidateProvenancePath = (
        Resolve-Path -LiteralPath $CandidateProvenancePath).Path
    $candidateProvenance = Get-Content `
        -LiteralPath $resolvedCandidateProvenancePath `
        -Raw | ConvertFrom-Json
    if ($candidateProvenance.schemaVersion -ne 1 -or
        $candidateProvenance.sourceTreeDirty -eq $true -or
        -not [string]::Equals(
            [string]$candidateProvenance.sourceCommit,
            $sourceCommit,
            [StringComparison]::OrdinalIgnoreCase) -or
        @($candidateProvenance.artifacts).Count -eq 0)
    {
        throw 'Streams candidate provenance is incomplete, dirty, or for another commit.'
    }
    $candidateProvenanceSha256 = (
        Get-FileHash -LiteralPath $resolvedCandidateProvenancePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $candidateArtifacts = @($candidateProvenance.artifacts)
}
if ($Duration -ge [TimeSpan]::FromHours(72) -and
    ([string]::IsNullOrWhiteSpace($candidateProvenanceSha256) -or
     [string]$PostgreSqlImage -notmatch '@sha256:[0-9a-f]{64}$'))
{
    throw (
        'The 72-hour Streams gate requires clean candidate-package provenance ' +
        'and a digest-pinned PostgreSQL image.')
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
        throw "Streams endurance could not create its isolated worktree (exit code $LASTEXITCODE)."
    }

    $worktreeAdded = $true
    $isolatedSourceCommitAtStart = (& git -C $fullWorktreePath rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $isolatedSourceCommitAtStart -ne $sourceCommit)
    {
        $failedPhase = 'worktree'
        $failureExitCode = $LASTEXITCODE
        throw 'Streams endurance isolated worktree does not match the requested source commit.'
    }

    $projectPath = Join-Path $fullWorktreePath $project
    & dotnet restore $projectPath --verbosity quiet
    if ($LASTEXITCODE -ne 0)
    {
        $failedPhase = 'restore'
        $failureExitCode = $LASTEXITCODE
        throw "Streams endurance restore failed with exit code $LASTEXITCODE."
    }

    & dotnet build $projectPath `
        --configuration $Configuration `
        --no-restore `
        --verbosity quiet
    if ($LASTEXITCODE -ne 0)
    {
        $failedPhase = 'build'
        $failureExitCode = $LASTEXITCODE
        throw "Streams endurance build failed with exit code $LASTEXITCODE."
    }

    $projectDirectory = Split-Path $projectPath -Parent
    $artifactRoot = Join-Path $projectDirectory "bin/$Configuration"
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
    $env:BLUETUSK_RELAY_ENDURANCE_DURATION = $Duration.ToString('c')
    $env:BLUETUSK_RELAY_ENDURANCE_INTERVAL_MS =
        $IntervalMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BLUETUSK_RELAY_ENDURANCE_MIN_TRANSACTIONS =
        $MinimumTransactions.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BLUETUSK_RELAY_ENDURANCE_REPORT = $harnessReportPath

    & dotnet test $projectPath `
        --configuration $Configuration `
        --no-build `
        --no-restore `
        --filter 'FullyQualifiedName~StreamsRelayEnduranceTests' `
        --logger 'trx;LogFileName=streams-relay-endurance.trx' `
        --results-directory $resultsDirectory `
        --verbosity quiet
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0)
    {
        $failedPhase = 'test'
        $failureExitCode = $testExitCode
        throw "Streams endurance test failed with exit code $testExitCode."
    }

    if (-not (Test-Path -LiteralPath $harnessReportPath -PathType Leaf))
    {
        $failedPhase = 'harness-report'
        throw 'Streams endurance test passed without producing its harness report.'
    }

    $harnessReport = Get-Content -LiteralPath $harnessReportPath -Raw |
        ConvertFrom-Json
    $harnessRequestedDuration = ConvertTo-TimeSpan `
        -Value $harnessReport.requestedDuration `
        -Name 'requested duration'
    $harnessActualDuration = ConvertTo-TimeSpan `
        -Value $harnessReport.actualDuration `
        -Name 'actual duration'
    $invalidHarness =
        $harnessRequestedDuration -lt $Duration -or
        $harnessActualDuration -lt $harnessRequestedDuration -or
        [long]$harnessReport.transactions -lt $MinimumTransactions -or
        [long]$harnessReport.duplicateAppends -le 0 -or
        [long]$harnessReport.replayedDeliveries -le 0 -or
        [long]$harnessReport.generationConflicts -le 0 -or
        [long]$harnessReport.fencedLeases -le 0 -or
        [long]$harnessReport.relayRestarts -le 0 -or
        [long]$harnessReport.maximumStorageBytes -gt 64L * 1024 * 1024 -or
        [long]$harnessReport.finalStorageBytes -ne 0
    if ($invalidHarness)
    {
        $failedPhase = 'harness-report'
        throw 'Streams endurance harness report does not satisfy the requested fault and storage gates.'
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
    elseif ($gateExecutionCompleted -and
        -not $artifactIntegrityPassed -and
        $null -eq $failedPhase)
    {
        $failedPhase = 'artifact-integrity'
    }

    $report = [ordered]@{
        formatVersion = 1
        sourceCommit = $sourceCommit
        sourceBranch = $sourceBranch
        candidateProvenanceSha256 = $candidateProvenanceSha256
        candidateArtifacts = $candidateArtifacts
        postgresqlImage = $PostgreSqlImage
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
        minimumTransactions = $MinimumTransactions
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
    throw "Streams endurance did not complete its source, artifact, duration, fault, and storage gates; report=$fullReportPath"
}

Write-Output (
    "Streams endurance completed $($harnessReport.transactions) transaction(s); " +
    "report=$fullReportPath")
