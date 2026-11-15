[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'approval-release-tracks.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1') -Force

$root = Split-Path $PSScriptRoot -Parent
$contract = Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'v1.2-core-package-readiness.json')
$families = Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'product-families.json')
$tracks = Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'release-tracks.json')
$legacyGates = @(Get-ApprovalTrackGateIds -ReleaseTrack Legacy)
$historicalGates = @(Get-ApprovalTrackGateIds -ReleaseTrack Core)
$packageGates = @(Get-ApprovalTrackGateIds -ReleaseTrack CorePackages)
$required = @($legacyGates | Where-Object { $_ -cne 'website-deployment-acceptance' })
if ($legacyGates.Count -ne 10 -or $historicalGates.Count -ne 8 -or $packageGates.Count -ne 9 -or
    @(Compare-Object $required $packageGates -SyncWindow 0 -CaseSensitive).Count -ne 0)
{
    throw 'Packages must retain both pilots and every non-website approval.'
}
if ($contract.schemaVersion -ne 1 -or $contract.releaseVersion -cne '1.2.0' -or
    $contract.scope -cne 'CorePackages' -or $contract.approvalTrack -cne 'CorePackages' -or
    $contract.usesProtectedPublication -ne $true -or
    @(Compare-Object @($tracks.stableFamilies) @($contract.families) -SyncWindow 0 -CaseSensitive).Count -ne 0 -or
    @(Compare-Object @(15, 16, 17, 18) @($contract.stablePostgreSqlMajors) -SyncWindow 0).Count -ne 0)
{
    throw 'Core package readiness scope or protected publication changed.'
}

$producers = @(
    'build.yml', 'dotnet-compatibility.yml', 'security.yml', 'fuzzing.yml',
    'core-performance-evidence.yml', 'streams-release-endurance.yml',
    'sync-release-endurance.yml', 'live-control-plane-release-endurance.yml'
)
if (@(Compare-Object $producers @($contract.requiredWorkflows.PSObject.Properties.Value) -CaseSensitive).Count -ne 0)
{ throw 'Core 1.2 package readiness requires all eight qualifying workflows.' }

foreach ($family in $contract.families)
{
    $definition = $families.families.$family
    $workflows = @($definition.publication.requiredWorkflowEvidence.workflowFile)
    if ($contract.readinessWorkflow -cnotin $workflows -or 'core-performance-evidence.yml' -cnotin $workflows -or
        'v1-candidate-readiness.yml' -cin $workflows -or 'performance.yml' -cin $workflows -or
        @($definition.releaseDependencies | Where-Object { $_ -cnotin $contract.families }).Count -ne 0)
    { throw "'$family' must use the versioned package evidence and retain its independent Core dependencies." }
}

$graph = $families.families.ContinuousGraph.publication
$graphWorkflows = @($graph.requiredWorkflowEvidence.workflowFile)
if ($contract.readinessWorkflow -cin $graphWorkflows -or
    'continuous-graph-release-endurance.yml' -cnotin $graphWorkflows -or
    'performance.yml' -cnotin $graphWorkflows -or 'v1-candidate-readiness.yml' -cnotin $graphWorkflows)
{ throw 'ContinuousGraph retains its independent release gates.' }

$workflow = Get-Content -Raw -LiteralPath (Join-Path $root ".github/workflows/$($contract.readinessWorkflow)")
if ($workflow -notmatch '(?m)^\s*environment:\s*v1-candidate-readiness\s*$' -or
    !$workflow.Contains('verify-core-1.2-package-readiness.ps1', [StringComparison]::Ordinal) -or
    !$workflow.Contains('v1_2_core_evidence_base64', [StringComparison]::OrdinalIgnoreCase))
{ throw 'Core package readiness must bind the protected exact-candidate verifier.' }

Write-Output 'Verified the five-family 1.2 package readiness policy, nine approval gates, eight producers, and independent Graph gates.'
