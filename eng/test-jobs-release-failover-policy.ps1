[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$verifier = Join-Path $PSScriptRoot 'verify-jobs-failover-report.ps1'
$workflow = Get-Content (Join-Path $root '.github/workflows/jobs-release-failover.yml') -Raw
$archiveVerifier = Get-Content (Join-Path $PSScriptRoot 'verify-jobs-release-failover.ps1') -Raw
$policy = Get-Content (Join-Path $PSScriptRoot 'jobs-release-failover-policy.json') -Raw | ConvertFrom-Json
$sourcePath = Join-Path $root 'docs/jobs/performance-reports/optimized-physical-promotion-pg18.json'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bluetusk-jobs-failover-policy-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$fixturePath = Join-Path $temporaryRoot 'synthetic.json'
$report = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json -Depth 100
$commit = [string]$report.SourceProvenance.Before.commit
$report.SourceProvenance.Before.dirty = $false
$report.SourceProvenance.After.dirty = $false

function Write-Fixture
{
    $report | ConvertTo-Json -Depth 100 |
        Set-Content -LiteralPath $fixturePath -Encoding utf8
}
function Assert-Rejected([string] $Expected)
{
    Write-Fixture
    $failure = $null
    try { & $verifier -ReportPath $fixturePath -ExpectedCommit $commit | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($Expected))
    {
        throw "Expected Jobs failover rejection containing '$Expected'; received '$failure'."
    }
}

try
{
    if ($policy.repetitions -ne 3 -or
        $workflow -notmatch '(?m)^  workflow_dispatch:\s*$' -or
        $workflow -match '(?m)^  (?:push|pull_request|schedule):\s*$' -or
        $workflow -notmatch [regex]::Escape('ref: ${{ inputs.candidate_sha }}') -or
        $workflow -notmatch [regex]::Escape('name: expansion-jobs-failover-${{ inputs.candidate_sha }}') -or
        $workflow -notmatch 'RUN-JOBS-RELEASE-FAILOVER' -or
        $workflow -notmatch 'name: jobs-failover-partial-' -or
        $archiveVerifier -notmatch 'AssertBinaries' -or
        $archiveVerifier -notmatch 'SourceTreeSha256')
    {
        throw 'Jobs failover workflow lost its exact manual candidate contract.'
    }
    foreach ($mode in @('Preflight', 'Run', 'Verify'))
    {
        if ($workflow -notmatch "-Mode $mode ") { throw "Jobs failover omits its $mode evidence step." }
    }
    foreach ($gap in @('credential-rotation-during-promotion',
        'persistent-storage-corruption-or-loss', 'split-brain', 'independent-host-fleet'))
    {
        if ($gap -notin @($policy.unqualifiedDisturbances))
        {
            throw "Jobs failover policy no longer declares untested '$gap' disturbance."
        }
    }
    Write-Fixture
    & $verifier -ReportPath $fixturePath -ExpectedCommit $commit | Out-Null
    $report.SourceProvenance.Before.dirty = $true
    Assert-Rejected 'source is dirty'
    $report.SourceProvenance.Before.dirty = $false
    $report.Runs[0].Report.JobEffects = 65
    Assert-Rejected 'lost or duplicated'
    $report.Runs[0].Report.JobEffects = 66
    $report.Runs[0].Report.Leases[0].StaleEffectRejected = $false
    Assert-Rejected 'stale owner'
    $report.Runs[0].Report.Leases[0].StaleEffectRejected = $true
    $report.Runs[1].Report.BeforeSystemIdentifier = $report.Runs[0].Report.BeforeSystemIdentifier
    $report.Runs[1].Report.AfterSystemIdentifier = $report.Runs[0].Report.BeforeSystemIdentifier
    Assert-Rejected 'distinct physical standby'
    $report.Runs[1].Report.BeforeSystemIdentifier = '7690339919043268643'
    $report.Runs[1].Report.AfterSystemIdentifier = '7690339919043268643'
    $report.Runs[0].Metadata.BeforePrimarySettings = @('fsync=on')
    Assert-Rejected 'synchronous durability'
    Write-Output 'Jobs failover policy self-test passed on synthetic evidence; no promotion was run or qualified.'
}
finally
{
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedRoot).StartsWith(
            'bluetusk-jobs-failover-policy-', [StringComparison]::Ordinal)))
    {
        throw 'Refusing to remove a failover-policy test directory outside the explicit temporary root.'
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
