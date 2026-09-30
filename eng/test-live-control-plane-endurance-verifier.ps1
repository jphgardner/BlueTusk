[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $repositoryRoot 'artifacts/verifier-self-tests/live-control-plane'
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$reportPath = Join-Path $scratch 'report.json'
$commit = '1234567890abcdef1234567890abcdef12345678'
$hash = 'a' * 64

$report = [ordered]@{
    formatVersion = 1
    sourceCommit = $commit
    sourceBranch = 'main'
    candidateProvenanceSha256 = $null
    candidateArtifacts = @()
    trackedWorktreeCleanAtStart = $true
    isolatedWorkspaceKind = 'detached-git-worktree'
    isolatedSourceCommitAtStart = $commit
    isolatedSourceCommitAtEnd = $commit
    isolatedTrackedWorktreeCleanAtEnd = $true
    testArtifactHashAlgorithm = 'SHA256'
    testArtifactHashAtStart = $hash
    testArtifactHashAtEnd = $hash
    testArtifactFileCount = 10
    isolatedWorktreeRemoved = $true
    requestedDuration = '00:00:01'
    actualDuration = '00:00:01.1000000'
    minimumCycles = 2
    completed = $true
    preparationCompleted = $true
    testExitCode = 0
    failedPhase = $null
    failureExitCode = $null
    configuration = 'Release'
    project = 'tests/BlueTusk.StressTests/BlueTusk.StressTests.csproj'
    harness = [ordered]@{
        requestedDuration = '00:00:01'
        actualDuration = '00:00:01.0500000'
        cycles = 128
        minimumCycles = 2
        liveRowCount = 10000
        deploymentCount = 256
        liveUpdates = 128
        authoritativeChecks = 1
        inventoryReads = 128
        operationExecutions = 128
        auditRecords = 256
        maximumCycleMilliseconds = 2
        maximumWorkingSetBytes = 104857600
        allocatedBytes = 1048576
    }
}
$report | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM

& (Join-Path $PSScriptRoot 'verify-live-control-plane-endurance-report.ps1') `
    -ReportPath $reportPath `
    -RequiredDuration '00:00:01' `
    -MinimumCycles 2 `
    -ExpectedCommit $commit | Out-Null

$report.harness.auditRecords = 255
$report | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
$rejected = $false
try
{
    & (Join-Path $PSScriptRoot 'verify-live-control-plane-endurance-report.ps1') `
        -ReportPath $reportPath `
        -RequiredDuration '00:00:01' `
        -MinimumCycles 2 `
        -ExpectedCommit $commit | Out-Null
}
catch
{
    $rejected = $true
}
if (-not $rejected)
{
    throw 'The verifier accepted invalid audit-completion evidence.'
}

Write-Output 'Live/Control Plane endurance verifier self-test passed.'
