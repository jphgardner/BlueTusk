[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$contractPath = Join-Path $PSScriptRoot 'v1-endurance-disturbance-contract.json'
$examplePath = Join-Path $PSScriptRoot 'v1-operational-disturbance-evidence.example.json'
$candidateExamplePath = Join-Path $PSScriptRoot 'v1-candidate-evidence.example.json'
$readinessPath = Join-Path $PSScriptRoot 'v1-production-readiness.json'
$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$candidateWorkflowPath = Join-Path $repositoryRoot '.github/workflows/v1-candidate-readiness.yml'
$candidateVerifierPath = Join-Path $PSScriptRoot 'verify-v1-production-readiness.ps1'

$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
$example = Get-Content -LiteralPath $examplePath -Raw | ConvertFrom-Json
$candidateExample = Get-Content -LiteralPath $candidateExamplePath -Raw | ConvertFrom-Json
$readiness = Get-Content -LiteralPath $readinessPath -Raw | ConvertFrom-Json
$candidateWorkflow = Get-Content -LiteralPath $candidateWorkflowPath -Raw
$candidateVerifier = Get-Content -LiteralPath $candidateVerifierPath -Raw

if ([int]$contract.schemaVersion -ne 1 -or [int]$example.schemaVersion -ne 1)
{
    throw 'Endurance-disturbance contract and example must both use schema 1.'
}
if ([string]$candidateExample.disturbances.reportPath -ne
        'disturbances/operational-disturbance-evidence.json' -or
    [string]$candidateExample.disturbances.reportSha256 -notmatch '^[0-9a-f]{64}$')
{
    throw 'The candidate-evidence example does not bind the canonical disturbance report.'
}

$expectedRuns = @('streams', 'sync')
$actualRuns = @($contract.requiredRuns | ForEach-Object { [string]$_ })
if ($actualRuns.Count -ne $expectedRuns.Count -or
    @(Compare-Object $expectedRuns $actualRuns).Count -ne 0)
{
    throw "The disturbance contract must require exactly: $($expectedRuns -join ', ')."
}

$expectedScenarios = @(
    'process-death',
    'network-interruption',
    'storage-exhaustion',
    'credential-rotation',
    'primary-failover',
    'clock-movement',
    'postgresql-minor-upgrade'
)
$contractScenarios = @($contract.requiredScenarios)
$actualScenarios = @($contractScenarios | ForEach-Object { [string]$_.id })
if ($actualScenarios.Count -ne $expectedScenarios.Count -or
    @(Compare-Object $expectedScenarios $actualScenarios).Count -ne 0 -or
    @($actualScenarios | Group-Object | Where-Object Count -ne 1).Count -ne 0)
{
    throw "The disturbance contract must require exactly: $($expectedScenarios -join ', ')."
}
foreach ($scenario in $contractScenarios)
{
    if ([string]::IsNullOrWhiteSpace([string]$scenario.description) -or
        @($scenario.requiredFacts).Count -lt 1 -or
        @($scenario.requiredFacts | ForEach-Object { [string]$_ } |
            Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0)
    {
        throw "Disturbance contract '$($scenario.id)' lacks a description or required facts."
    }
}

$artifactRoles = @($contract.requiredArtifactRoles | ForEach-Object { [string]$_ })
if ($artifactRoles.Count -ne 2 -or
    @(Compare-Object @('injection', 'recovery') $artifactRoles).Count -ne 0)
{
    throw 'The disturbance contract must content-address injection and recovery artifacts.'
}
if ([double]$contract.storageMinimumUtilisation -lt 0.95 -or
    [double]$contract.storageMinimumUtilisation -gt 1)
{
    throw 'The controlled storage-exhaustion threshold must be between 95% and 100%.'
}

if ([int]$readiness.minimums.enduranceDisturbanceRuns -ne $expectedRuns.Count -or
    [int]$readiness.minimums.enduranceDisturbancesPerRun -ne $expectedScenarios.Count)
{
    throw 'V1 minimums do not match the operational-disturbance contract.'
}

$exampleRuns = @($example.runs)
if ($exampleRuns.Count -ne $expectedRuns.Count)
{
    throw 'The operational-disturbance example must contain both endurance runs.'
}
$commonScenarioProperties = @(
    'startedAt',
    'completedAt',
    'target',
    'injectionMethod',
    'detectionSignal',
    'recoveryAction',
    'recoveryProbe',
    'observations',
    'outcome',
    'faultInjected',
    'detectionObserved',
    'recoveryObserved',
    'continuityVerified',
    'dataLossObserved',
    'blockingFindings',
    'facts',
    'references',
    'artifacts'
)
foreach ($runId in $expectedRuns)
{
    $runMatches = @($exampleRuns | Where-Object { [string]$_.id -eq $runId })
    if ($runMatches.Count -ne 1)
    {
        throw "The operational-disturbance example requires one '$runId' run."
    }
    $run = $runMatches[0]
    if ([string]$run.enduranceReportSha256 -notmatch '^[0-9a-f]{64}$')
    {
        throw "The '$runId' example has no endurance-report hash."
    }
    $scenarios = @($run.scenarios)
    if ($scenarios.Count -ne $expectedScenarios.Count)
    {
        throw "The '$runId' example must contain all seven disturbances."
    }
    foreach ($contractScenario in $contractScenarios)
    {
        $scenarioId = [string]$contractScenario.id
        $matches = @($scenarios | Where-Object { [string]$_.id -eq $scenarioId })
        if ($matches.Count -ne 1)
        {
            throw "The '$runId' example requires one '$scenarioId' disturbance."
        }
        $scenario = $matches[0]
        foreach ($property in $commonScenarioProperties)
        {
            if ($null -eq $scenario.PSObject.Properties[$property])
            {
                throw "The '$runId/$scenarioId' example is missing '$property'."
            }
        }
        foreach ($fact in @($contractScenario.requiredFacts | ForEach-Object { [string]$_ }))
        {
            if ($null -eq $scenario.facts.PSObject.Properties[$fact])
            {
                throw "The '$runId/$scenarioId' example is missing fact '$fact'."
            }
        }
        $roles = @($scenario.artifacts | ForEach-Object { [string]$_.role })
        if ($roles.Count -ne $artifactRoles.Count -or
            @(Compare-Object $artifactRoles $roles).Count -ne 0)
        {
            throw "The '$runId/$scenarioId' example must bind injection and recovery artifacts."
        }
    }
}

foreach ($requiredSource in @(
        '$disturbanceReport = OneFile (Join-Path $root disturbances) operational-disturbance-evidence.json',
        'reportPath = Relative $disturbanceReport',
        'reportSha256 = Hash $disturbanceReport'))
{
    if (-not $candidateWorkflow.Contains($requiredSource, [StringComparison]::Ordinal))
    {
        throw "The exact-candidate workflow does not bind disturbances through '$requiredSource'."
    }
}
if (-not $candidateVerifier.Contains(
        "'verify-endurance-disturbance-evidence.ps1'",
        [StringComparison]::Ordinal))
{
    throw 'Candidate readiness does not invoke the strict disturbance-evidence verifier.'
}

& (Join-Path $PSScriptRoot 'test-endurance-disturbance-verifier.ps1')

Write-Output (
    "Endurance-disturbance contract passed: $($expectedRuns.Count) runs x " +
    "$($expectedScenarios.Count) scenarios = " +
    "$($expectedRuns.Count * $expectedScenarios.Count) required recoveries.")
