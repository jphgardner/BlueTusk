[CmdletBinding()]
param(
    [string] $ContractPath = (Join-Path $PSScriptRoot 'performance-leadership-contract.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
if ($contract.schemaVersion -ne 1 -or $contract.release -ne '1.1.0')
{
    throw 'The performance-leadership contract must be schema 1 for release 1.1.0.'
}

$rules = $contract.comparisonRules
if ($rules.sameRuntimeMaximumRatio -ne 0.98 -or
    $rules.crossRuntimeMinimumThroughputRatio -ne 1.05 -or
    $rules.crossRuntimeMaximumCostRatio -ne 0.95 -or
    $rules.confidenceLevel -ne 0.95 -or
    $rules.statisticalTiesPass -ne $false -or
    $rules.trustedCdcMaximumFullRequeryRatio -ne 0.1 -or
    $rules.authoritativeDeltaMaximumFullRequeryRatio -ne 0.35)
{
    throw 'A release-leading comparison threshold was weakened or removed.'
}

$expectedFamilies = @(
    'Provider',
    'Streams',
    'Sync',
    'Live',
    'ControlPlane',
    'ContinuousGraph')
foreach ($family in $expectedFamilies)
{
    if ($null -eq $contract.references.PSObject.Properties[$family] -or
        $null -eq $contract.workloads.PSObject.Properties[$family])
    {
        throw "Performance reference or workload coverage is missing for '$family'."
    }
}

if (@($contract.environments).Count -ne 2 -or
    @($contract.environments.os | Sort-Object) -join ',' -ne 'linux,windows')
{
    throw 'Dedicated Windows x64 and Linux x64 environments are mandatory.'
}

$graph = $contract.workloads.ContinuousGraph
if (@($graph.edgeCounts).Count -ne 3 -or
    @($graph.topN).Count -ne 3 -or
    @($graph.tiers).Count -ne 3 -or
    @($graph.scenarios).Count -ne 7)
{
    throw 'Continuous Graph scale, tier, or correctness scenarios are incomplete.'
}

$provider = $contract.workloads.Provider
if (@($provider.features).Count -ne $provider.featureCount -or
    @($provider.features | Select-Object -Unique).Count -ne $provider.featureCount)
{
    throw 'The complete 16-feature Provider comparison matrix is not named explicitly.'
}

$requiredMetrics = @(
    'throughput', 'mean', 'p95', 'p99', 'allocatedBytes',
    'cpuPerEvent', 'peakRss', 'gcCounters')
foreach ($metric in $requiredMetrics)
{
    if (@($contract.requiredMetrics) -notcontains $metric)
    {
        throw "Required performance metric '$metric' is missing."
    }
}

if ($contract.enduranceHours.Streams -ne 72 -or
    $contract.enduranceHours.Sync -ne 24 -or
    $contract.enduranceHours.Live -ne 24 -or
    $contract.enduranceHours.ControlPlane -ne 24 -or
    $contract.enduranceHours.ContinuousGraph -ne 24 -or
    $contract.stablePublicationRequiresPostgreSql19Ga -ne $true)
{
    throw 'Endurance duration or the PostgreSQL 19 GA publication gate was weakened.'
}

$workflowPath = Join-Path (
    Split-Path $PSScriptRoot -Parent) '.github/workflows/performance-leadership.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
if ($workflow -notmatch '(?m)^\s*workflow_dispatch\s*:' -or
    $workflow -match '(?m)^\s*(push|pull_request|schedule)\s*:' -or
    $workflow -notmatch 'CAPTURE-1\.1-PERFORMANCE-EVIDENCE' -or
    $workflow -notmatch '\[\"self-hosted\",\"windows\",\"x64\",\"bluetusk-benchmark\"\]' -or
    $workflow -notmatch '\[\"self-hosted\",\"linux\",\"x64\",\"bluetusk-benchmark\"\]' -or
    $workflow -notmatch 'run-v1-performance-gate\.ps1' -or
    -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'verify-performance-leadership-evidence.ps1')) -or
    -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'test-performance-leadership-evidence-verifier.ps1')))
{
    throw 'The manual exact-SHA Windows/Linux evidence capture workflow is incomplete.'
}

Write-Output 'Verified the complete BlueTusk 1.1 performance-leadership contract.'
