[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$verifier = Join-Path $PSScriptRoot 'verify-expansion-readiness-evidence.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bluetusk-expansion-readiness-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$commit = '1234567890abcdef1234567890abcdef12345678'
$tag = 'jobs-v1.0.0'
$digest = 'sha256:' + ('a' * 64)
$otherDigest = 'sha256:' + ('b' * 64)
$runs = @(
    [pscustomobject]@{ workflowFile = 'jobs-release-capacity.yml'; runId = 101; runAttempt = 1 },
    [pscustomobject]@{ workflowFile = 'jobs-release-failover.yml'; runId = 102; runAttempt = 1 },
    [pscustomobject]@{ workflowFile = 'jobs-release-upgrade.yml'; runId = 103; runAttempt = 1 },
    [pscustomobject]@{ workflowFile = 'expansion-candidate-readiness.yml'; runId = 104; runAttempt = 2 }
)
$entries = @(
    [ordered]@{ role = 'capacity'; workflowFile = 'jobs-release-capacity.yml'; runId = 101; runAttempt = 1; artifactName = "expansion-jobs-capacity-$commit"; artifactDigest = $digest },
    [ordered]@{ role = 'failover'; workflowFile = 'jobs-release-failover.yml'; runId = 102; runAttempt = 1; artifactName = "expansion-jobs-failover-$commit"; artifactDigest = $digest },
    [ordered]@{ role = 'upgrade'; workflowFile = 'jobs-release-upgrade.yml'; runId = 103; runAttempt = 1; artifactName = "expansion-jobs-upgrade-$commit"; artifactDigest = $digest }
)
$evidence = [ordered]@{
    schemaVersion = 1
    family = 'Jobs'
    candidateCommit = $commit
    tag = $tag
    version = '1.0.0'
    readinessRun = @{ id = 104; attempt = 2 }
    qualificationEvidence = $entries
}
$artifacts = @()
foreach ($entry in $entries)
{
    $artifacts += [ordered]@{
        id = $entry.runId + 1000
        name = $entry.artifactName
        digest = $digest
        expired = $false
        size_in_bytes = 100
        workflow_run = @{ id = $entry.runId; head_sha = $commit }
    }
}
$artifacts += [ordered]@{
    id = 1104
    name = "expansion-readiness-jobs-$commit"
    digest = $digest
    expired = $false
    size_in_bytes = 100
    workflow_run = @{ id = 104; head_sha = $commit }
}
$evidencePath = Join-Path $temporaryRoot 'readiness.json'
$indexPath = Join-Path $temporaryRoot 'artifacts.json'
$arguments = @{
    Family = 'Jobs'
    Commit = $commit
    Tag = $tag
    Version = '1.0.0'
    VerifiedRuns = $runs
    EvidencePath = $evidencePath
    ArtifactIndexPath = $indexPath
}

function Write-Fixtures
{
    $evidence | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $evidencePath -Encoding utf8
    @{ schemaVersion = 1; artifacts = $artifacts } | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $indexPath -Encoding utf8
}

function Assert-Rejected
{
    param([string] $ExpectedMessage)
    Write-Fixtures
    $failure = $null
    try { & $verifier @arguments | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($ExpectedMessage))
    {
        throw "Expected rejection containing '$ExpectedMessage'; received '$failure'."
    }
}

try
{
    Write-Fixtures
    & $verifier @arguments | Out-Null
    $evidence.family = 'Workflows'
    Assert-Rejected 'exact'
    $evidence.family = 'Jobs'
    $evidence.candidateCommit = 'f' * 40
    Assert-Rejected 'exact'
    $evidence.candidateCommit = $commit
    $evidence.tag = 'jobs-v1.0.1'
    Assert-Rejected 'exact'
    $evidence.tag = $tag
    $evidence.qualificationEvidence[0].runId = 999
    Assert-Rejected 'capacity'
    $evidence.qualificationEvidence[0].runId = 101
    $evidence.qualificationEvidence[0].runAttempt = 2
    Assert-Rejected 'capacity'
    $evidence.qualificationEvidence[0].runAttempt = 1
    $evidence.qualificationEvidence[0].artifactDigest = $otherDigest
    Assert-Rejected 'digest'
    $evidence.qualificationEvidence[0].artifactDigest = $digest
    $artifacts[0].expired = $true
    Assert-Rejected 'retained digest'
    $artifacts[0].expired = $false
    $artifacts[3].workflow_run.head_sha = 'f' * 40
    Assert-Rejected 'exact candidate run'
    $artifacts[3].workflow_run.head_sha = $commit
    $artifacts[3].size_in_bytes = 4194305
    Assert-Rejected '4 MiB'
    Write-Output 'Expansion readiness evidence self-test passed: exact family, candidate, run and artifact bindings.'
}
finally
{
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedRoot).StartsWith(
            'bluetusk-expansion-readiness-tests-', [StringComparison]::Ordinal)))
    {
        throw 'Refusing to remove a readiness test directory outside the explicit temporary root.'
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
