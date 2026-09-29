[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workflow = Get-Content (Join-Path $root '.github/workflows/jobs-release-upgrade.yml') -Raw
$verifier = Get-Content (Join-Path $PSScriptRoot 'verify-jobs-release-upgrade.ps1') -Raw
$probe = Get-Content (Join-Path $PSScriptRoot 'JobsUpgradeProbe/Program.cs') -Raw
$project = Join-Path $PSScriptRoot 'JobsUpgradeProbe/JobsUpgradeProbe.csproj'
$candidateProject = Join-Path $root 'src/BlueTusk.Jobs/BlueTusk.Jobs.csproj'

if ($workflow -notmatch '(?m)^  workflow_dispatch:\s*$' -or
    $workflow -match '(?m)^  (?:push|pull_request|schedule):\s*$' -or
    $workflow -notmatch [regex]::Escape('ref: ${{ inputs.candidate_sha }}') -or
    $workflow -notmatch [regex]::Escape('name: expansion-jobs-upgrade-${{ inputs.candidate_sha }}') -or
    $workflow -notmatch 'old_sha:' -or
    $workflow -notmatch 'RUN-JOBS-RELEASE-UPGRADE' -or
    $workflow -notmatch 'name: jobs-upgrade-partial-')
{
    throw 'Jobs upgrade workflow lost its exact manual two-commit contract.'
}
foreach ($mode in @('Preflight', 'Run', 'Verify'))
{
    if ($workflow -notmatch "-Mode $mode ") { throw "Jobs upgrade workflow omits $mode." }
}
foreach ($boundary in @('OldCommit', 'merge-base --is-ancestor', 'old-source.tar',
    'OldJobsTree', 'CandidateJobsTree', 'dotnet pack', 'dotnet publish',
    'OldJobsDllSha256', 'CandidateJobsDllSha256',
    'cross-binary rehearsal', 'PackageDllHash', 'AssertReport', 'PhaseHashes'))
{
    if (-not $verifier.Contains($boundary)) { throw "Jobs upgrade verifier lost '$boundary'." }
}
foreach ($behavior in @('probe.held', 'probe.retry', 'probe.pending',
    'candidate-ready', 'old-done', 'probe.rollback', 'FormatVersion',
    '!await store.CompleteAsync', 'DeduplicationKey'))
{
    if (-not $probe.Contains($behavior)) { throw "Jobs upgrade probe lost '$behavior'." }
}

$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'verify-jobs-release-upgrade.ps1'), [ref]$null, [ref]$errors) | Out-Null
if ($null -ne $errors -and $errors.Count -gt 0) { throw "Jobs upgrade verifier has a PowerShell parse error: $($errors[0])" }

$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve Jobs candidate for the no-DB policy test.' }
$rejection = $null
try {
    & (Join-Path $PSScriptRoot 'verify-jobs-release-upgrade.ps1') -Mode Preflight `
        -ExpectedCommit $head -OldCommit $head | Out-Null
} catch { $rejection = $_.Exception.Message }
if ($null -eq $rejection -or -not $rejection.Contains('distinct exact old and candidate commits'))
{
    throw "Identical old/candidate commits were not rejected: '$rejection'."
}

& dotnet build $project -c Release -p:JobsProject=$candidateProject `
    --artifacts-path (Join-Path $root 'artifacts/jobs-upgrade-probe-policy-build')
if ($LASTEXITCODE -ne 0) { throw 'Candidate Jobs upgrade probe failed to compile without a database.' }
Write-Output 'Jobs upgrade source policy and candidate probe compile passed; no PostgreSQL or release run was used.'
