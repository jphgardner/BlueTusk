[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workflow = Get-Content (Join-Path $root '.github/workflows/jobs-release-capacity.yml') -Raw
$combinedWorkflow = Get-Content (Join-Path $root '.github/workflows/ecosystem-performance.yml') -Raw
$wrapper = Get-Content (Join-Path $PSScriptRoot 'verify-jobs-release-capacity.ps1') -Raw
$verifier = Get-Content (Join-Path $PSScriptRoot 'verify-ecosystem-performance.ps1') -Raw
$campaign = Get-Content (Join-Path $PSScriptRoot 'jobs-storage-campaign.ps1') -Raw
$budget = Get-Content (Join-Path $PSScriptRoot 'jobs-release-capacity-budgets.json') -Raw | ConvertFrom-Json

function Require([bool] $Condition, [string] $Message)
{
    if (-not $Condition) { throw $Message }
}

Require ($workflow -match '(?m)^  workflow_dispatch:\s*$' -and
    $workflow -notmatch '(?m)^  (?:push|pull_request|schedule):\s*$') 'Jobs capacity must be manual-only.'
Require ($workflow -match [regex]::Escape('ref: ${{ inputs.candidate_sha }}') -and
    $workflow -match 'bluetusk-benchmark' -and
    $workflow -match 'RUN-JOBS-RELEASE-CAPACITY') 'Jobs capacity must use the exact candidate and reference runner.'
foreach ($mode in @('Preflight', 'Run', 'Verify'))
{
    Require ($workflow -match "-Mode $mode ") "Jobs capacity omits the $mode verification step."
}
Require ($workflow -match [regex]::Escape('name: expansion-jobs-capacity-${{ inputs.candidate_sha }}') -and
    $workflow -match 'name: jobs-capacity-partial-' -and
    $workflow -match 'retention-days: 90') 'Jobs capacity must publish a distinct retained success artifact.'
foreach ($fixtureWorkflow in @($workflow, $combinedWorkflow))
{
    $passwordAssignments = [regex]::Matches($fixtureWorkflow, 'POSTGRES_PASSWORD=')
    Require ($fixtureWorkflow -match '\[Security\.Cryptography\.RandomNumberGenerator\]::GetBytes\(24\)' -and
        $fixtureWorkflow -match 'POSTGRES_PASSWORD=\$fixturePassword' -and
        $passwordAssignments.Count -eq 1) 'Release capacity fixtures must use a fresh unpredictable PostgreSQL password.'
}
Require ($wrapper -match '-Product Jobs' -and
    $verifier -match 'jobs-release-capacity-budgets.json' -and
    $verifier -match '-Product Jobs -FixtureName bluetusk-jobs-release-pg15') 'Jobs capacity must use its scoped verifier and fixture.'
Require ($campaign -match "'storage-jobs'" -and
    $campaign -match "'bluetusk-jobs-release-pg15'" -and
    $campaign -match 'ImageRepoDigests' -and
    $verifier -match 'ImageRepoDigests') 'Jobs campaign must use its Jobs-only profile and verify the pinned image.'
Require ($budget.schemaVersion -eq 1 -and $budget.repetitions -eq 2 -and
    $budget.qualification -ceq 'manual-exact-candidate-jobs-capacity' -and
    $budget.jobsWorkflows.secondsPerProduct -ge 1800 -and
    $budget.jobsWorkflows.payloadMode -ceq 'SeededHighEntropy') 'Jobs capacity requires two full high-entropy campaigns.'
Require ($budget.jobsWorkflows.minimumCompletionsPerSecond.Jobs -ge 20 -and
    $budget.jobsWorkflows.maximumHotDurableP99Milliseconds.Jobs -le 10000 -and
    $budget.jobsWorkflows.maximumColdDurableP99Milliseconds.Jobs -le 3000 -and
    $budget.jobsWorkflows.maximumClusterWalBytesPerAccepted.Jobs -le 65536 -and
    $budget.jobsWorkflows.maximumRuntimeRelationBytes.Jobs -le 67108864 -and
    $budget.jobsWorkflows.maximumLateGrowthBytesPerMinute.Jobs -le 2097152) 'Jobs capacity budget cannot silently weaken its product limits.'

Write-Output 'Jobs release capacity source contract passed; no workload was run or qualified.'
