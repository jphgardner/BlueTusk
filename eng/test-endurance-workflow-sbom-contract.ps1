[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$workflowNames = @(
    'streams-release-endurance.yml',
    'sync-release-endurance.yml',
    'live-control-plane-release-endurance.yml',
    'continuous-graph-release-endurance.yml'
)

foreach ($workflowName in $workflowNames)
{
    $workflowPath = Join-Path $repositoryRoot ".github/workflows/$workflowName"
    $source = Get-Content -LiteralPath $workflowPath -Raw
    $restoreMatches = [regex]::Matches(
        $source,
        '(?m)^\s*dotnet restore BlueTusk\.slnx --locked-mode\s*$')
    $sbomMatches = [regex]::Matches(
        $source,
        '(?m)^\s*\./eng/generate-sbom\.ps1 `\s*$')
    $noRestoreMatches = [regex]::Matches(
        $source,
        '(?m)^\s*-NoRestore\s*$')

    if ($restoreMatches.Count -ne 1)
    {
        throw (
            "Endurance workflow '$workflowName' must restore BlueTusk.slnx " +
            'exactly once in locked mode before SBOM generation.')
    }
    if ($sbomMatches.Count -ne 1 -or $noRestoreMatches.Count -ne 1)
    {
        throw (
            "Endurance workflow '$workflowName' must generate exactly one SBOM " +
            'from the restored dependency graph without a second restore.')
    }
    if ($restoreMatches[0].Index -gt $sbomMatches[0].Index -or
        $noRestoreMatches[0].Index -lt $sbomMatches[0].Index)
    {
        throw (
            "Endurance workflow '$workflowName' does not restore before its " +
            'no-restore SBOM inventory.')
    }
}

Write-Output (
    'Endurance workflow SBOM contract passed: Streams, Sync, Live/Control Plane, ' +
    'and ContinuousGraph restore the complete locked dependency graph before ' +
    'no-restore inventory.')
