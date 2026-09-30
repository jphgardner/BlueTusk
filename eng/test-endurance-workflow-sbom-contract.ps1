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

$runnerNames = @(
    'run-streams-endurance.ps1',
    'run-sync-endurance.ps1',
    'run-live-control-plane-endurance.ps1',
    'run-continuous-graph-endurance.ps1'
)
$unsafeDetachedHeadPattern =
    '\(\[string\]\(& git -C \$RepositoryRoot branch --show-current\)\)\.Trim\(\)'
$safeDetachedHeadPattern =
    '(?s)\$sourceBranchOutput\s*=\s*@\(& git -C \$RepositoryRoot branch --show-current\).*?' +
    '\$sourceBranch\s*=\s*\(\$sourceBranchOutput -join \[Environment\]::NewLine\)\.Trim\(\)'

foreach ($runnerName in $runnerNames)
{
    $runnerPath = Join-Path $repositoryRoot "eng/$runnerName"
    $source = Get-Content -LiteralPath $runnerPath -Raw
    if ($source -match $unsafeDetachedHeadPattern -or
        $source -notmatch $safeDetachedHeadPattern)
    {
        throw (
            "Endurance runner '$runnerName' must preserve an empty branch " +
            'name when the exact candidate is checked out at detached HEAD.')
    }
}

$deploymentPath = Join-Path $repositoryRoot 'eng/deploy-kubernetes-endurance.ps1'
$deploymentSource = Get-Content -LiteralPath $deploymentPath -Raw
$requiredDownloaderMarkers = @(
    '$relativeOutput = [IO.Path]::GetRelativePath(',
    'Push-Location $repositoryRoot',
    'runAsNonRoot: true',
    'runAsUser: 1654',
    'type: RuntimeDefault',
    '--ignore-not-found --wait=true'
)
foreach ($marker in $requiredDownloaderMarkers)
{
    if (-not $deploymentSource.Contains($marker, [StringComparison]::Ordinal))
    {
        throw (
            "Kubernetes evidence downloader is missing required marker '$marker'.")
    }
}
if ($deploymentSource -match
    'kubectl cp\s+"\$namespace/evidence-reader:/evidence/endurance/\."\s+\$fullOutput')
{
    throw (
        'Kubernetes evidence download must not pass a Windows drive-qualified ' +
        'destination directly to kubectl cp.')
}

Write-Output (
    'Endurance workflow contract passed: Streams, Sync, Live/Control Plane, ' +
    'and ContinuousGraph restore the complete locked dependency graph, accept ' +
    'detached exact-SHA checkouts, and retain cross-platform evidence downloads.')
