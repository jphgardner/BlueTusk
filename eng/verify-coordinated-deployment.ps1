[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Candidate,
    [Parameter(Mandatory)] [ValidateRange(1, [long]::MaxValue)] [long] $ReadinessRunId,
    [Parameter(Mandatory)] [ValidateRange(1, [long]::MaxValue)] [long] $ApplicationsRunId,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')] [string] $Repository = 'jphgardner/BlueTusk'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$api = "repos/$Repository"

function Assert-Run([long] $Id, [string] $Workflow, [string] $Event, [string] $Ref = '')
{
    $run = & gh api "$api/actions/runs/$Id" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $run.path.Split('@')[0] -cne ".github/workflows/$Workflow" -or
        $run.event -cne $Event -or $run.head_sha -cne $Candidate -or $run.conclusion -cne 'success' -or
        ($Ref -and $run.head_branch -cne $Ref))
    {
        throw "Run $Id must be a successful '$Workflow' run for the exact candidate."
    }
}

Assert-Run $ReadinessRunId 'core-1.2-package-readiness.yml' 'workflow_dispatch'
Assert-Run $ApplicationsRunId 'applications-images.yml' 'workflow_dispatch'
$main = & gh api "$api/commits/main" --jq .sha
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the reviewed main branch.' }
$comparison = & gh api "$api/compare/$Candidate...$main" --jq .status
if ($LASTEXITCODE -ne 0 -or $comparison -cnotin @('ahead', 'identical'))
{
    throw 'The candidate must be included in main.'
}

foreach ($prefix in 'provider', 'streams', 'sync', 'live', 'control-plane')
{
    $tag = "$prefix-v1.2.0"
    $tagCommit = & gh api "$api/commits/$tag" --jq .sha
    if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $Candidate)
    {
        throw "The protected package tag '$tag' must identify the same candidate."
    }
    $run = & gh run list -R $Repository --workflow release-product-family.yml --event push --branch $tag `
        --commit $Candidate --status success --limit 1 --json databaseId --jq '.[0].databaseId // empty'
    if ($LASTEXITCODE -ne 0 -or !$run) { throw "Package publication for '$tag' has not passed." }
    Assert-Run ([long] $run) 'release-product-family.yml' 'push' $tag
    $jobs = & gh run view $run -R $Repository --json jobs | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or
        @($jobs.jobs | Where-Object { $_.name -ceq 'publish-stable' -and $_.conclusion -ceq 'success' }).Count -ne 1)
    {
        throw "No successful protected publication job was found for '$tag'."
    }
}

Write-Output "Verified coordinated Core 1.2.0 publication and exact-candidate application images for $Candidate."
