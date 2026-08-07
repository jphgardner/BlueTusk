[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$verifier = Join-Path $PSScriptRoot 'verify-continuous-graph-endurance-report.ps1'
$temporary = Join-Path (
    [IO.Path]::GetTempPath()) "bluetusk-graph-verifier-$([Guid]::NewGuid())"
[IO.Directory]::CreateDirectory($temporary) | Out-Null

$commit = '0123456789abcdef0123456789abcdef01234567'
$image = 'postgres:19-alpine@sha256:' + ('a' * 64)
$provenancePath = Join-Path $temporary 'build-provenance.json'
$reportPath = Join-Path $temporary 'report.json'
$rejections = 0

function Write-Json
{
    param([string] $Path, [object] $Value)
    $Value | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Assert-Rejected
{
    param([Parameter(Mandatory)][scriptblock] $Mutation)

    $candidate = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    & $Mutation $candidate
    $mutationPath = Join-Path $temporary "mutation-$([Guid]::NewGuid()).json"
    Write-Json $mutationPath $candidate
    try
    {
        & $verifier `
            -ReportPath $mutationPath `
            -RequiredDuration '1.00:00:00' `
            -MinimumEvaluations 100000 `
            -ExpectedCommit $commit `
            -CandidateProvenancePath $provenancePath `
            -ExpectedPostgreSqlImage $image
        throw 'Mutated ContinuousGraph evidence was accepted.'
    }
    catch
    {
        if ($_.Exception.Message -eq 'Mutated ContinuousGraph evidence was accepted.')
        {
            throw
        }
        $script:rejections++
    }
}

try
{
    $artifact = [ordered]@{
        path = 'BlueTusk.ContinuousGraph.1.0.0.nupkg'
        sha256 = 'b' * 64
        bytes = 12345
    }
    $provenance = [ordered]@{
        schemaVersion = 1
        sourceCommit = $commit
        sourceTreeDirty = $false
        artifacts = @($artifact)
    }
    Write-Json $provenancePath $provenance
    $provenanceHash = (
        Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()

    $report = [ordered]@{
        formatVersion = 1
        completed = $true
        sourceCommit = $commit
        sourceBranch = 'main'
        trackedWorktreeCleanAtStart = $true
        candidateProvenanceSha256 = $provenanceHash
        candidateArtifacts = @($artifact)
        postgresqlImage = $image
        project = 'tests/BlueTusk.StressTests/BlueTusk.StressTests.csproj'
        phaseTests = [ordered]@{
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
        phaseExitCodes = [ordered]@{
            'restart-seed' = 0
            'restart-resume' = 0
            'run' = 0
        }
        configuration = 'Release'
        runtimeVersion = '10.0.100'
        operatingSystem = 'Linux'
        architecture = 'X64'
        requestedDuration = '1.00:00:00'
        actualDuration = '1.00:00:01'
        minimumEvaluations = 100000
        intervalMilliseconds = 250
        startedUtc = '2026-10-01T00:00:00Z'
        completedUtc = '2026-10-02T00:00:01Z'
        testExitCode = 0
        failure = $null
        harness = [ordered]@{
            requestedDuration = '1.00:00:00'
            actualDuration = '1.00:00:01'
            evaluations = 100000
            committedEvaluations = 99900
            authoritativeRepairs = 100000
            processRestartRecoveries = 1
            cancellationRecoveries = 1
            cancellationCleanupVerified = $true
            disconnectRecoveries = 1
            replayCorruptionDetections = 1
            replaySequenceErrors = 0
            incorrectlyOrderedResults = 0
            unreconciledResults = 0
            lifecycleP95Milliseconds = 1000
        }
    }
    Write-Json $reportPath $report

    & $verifier `
        -ReportPath $reportPath `
        -RequiredDuration '1.00:00:00' `
        -MinimumEvaluations 100000 `
        -ExpectedCommit $commit `
        -CandidateProvenancePath $provenancePath `
        -ExpectedPostgreSqlImage $image

    Assert-Rejected { param($value) $value.sourceCommit = 'f' * 40 }
    Assert-Rejected { param($value) $value.postgresqlImage = 'postgres:19-alpine@sha256:' + ('c' * 64) }
    Assert-Rejected { param($value) $value.harness.actualDuration = '23:59:59' }
    Assert-Rejected { param($value) $value.harness.evaluations = 99999 }
    Assert-Rejected { param($value) $value.harness.committedEvaluations = 99899 }
    Assert-Rejected { param($value) $value.harness.lifecycleP95Milliseconds = 1000.01 }
    Assert-Rejected { param($value) $value.harness.authoritativeRepairs = 0 }
    Assert-Rejected { param($value) $value.harness.processRestartRecoveries = 0 }
    Assert-Rejected { param($value) $value.phaseExitCodes.'restart-resume' = 1 }
    Assert-Rejected { param($value) $value.harness.cancellationRecoveries = 0 }
    Assert-Rejected { param($value) $value.harness.cancellationCleanupVerified = $false }
    Assert-Rejected { param($value) $value.harness.disconnectRecoveries = 0 }
    Assert-Rejected { param($value) $value.harness.replayCorruptionDetections = 0 }
    Assert-Rejected { param($value) $value.harness.replaySequenceErrors = 1 }
    Assert-Rejected { param($value) $value.harness.incorrectlyOrderedResults = 1 }
    Assert-Rejected { param($value) $value.harness.unreconciledResults = 1 }
    Assert-Rejected { param($value) $value.candidateArtifacts[0].sha256 = 'd' * 64 }

    if ($rejections -ne 17)
    {
        throw "Expected 17 verifier rejections; observed $rejections."
    }
    Write-Host (
        'ContinuousGraph endurance verifier self-test passed ' +
        "(positive fixture and $rejections mutations).")
}
finally
{
    if (Test-Path -LiteralPath $temporary)
    {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
