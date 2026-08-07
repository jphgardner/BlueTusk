[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$train = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'prerelease-train.json') -Raw |
    ConvertFrom-Json
$families = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'product-families.json') -Raw |
    ConvertFrom-Json
$verifier = Join-Path $PSScriptRoot 'verify-prerelease-release.ps1'
$commit = '0' * 40
$testRoot = [IO.Path]::GetFullPath((
    Join-Path $repositoryRoot 'artifacts/test-results/prerelease-verifier'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $testRoot.StartsWith(
        $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "Prerelease verifier test output must remain below '$artifactsRoot'."
}
if (Test-Path -LiteralPath $testRoot)
{
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

function Write-Evidence
{
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][object] $Value
    )

    $path = Join-Path $testRoot "$Name.json"
    $Value | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $path -Encoding utf8NoBOM
    return $path
}

function Assert-Rejected
{
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][scriptblock] $Action
    )

    try
    {
        & $Action
        throw "Mutation '$Name' was unexpectedly accepted."
    }
    catch
    {
        if ($_.Exception.Message -eq "Mutation '$Name' was unexpectedly accepted.")
        {
            throw
        }
    }
}

$tags = [ordered]@{}
foreach ($family in @($train.families))
{
    $definition = $families.families.$family
    $tag = "$($definition.publication.tagPrefix)-v$($train.version)"
    $tags[$tag] = $commit
    $evidence = [ordered]@{
        schemaVersion = 1
        mainCommit = $commit
        tags = $tags
        stableTags = @()
    }
    $path = Write-Evidence -Name "valid-$family" -Value $evidence
    & $verifier `
        -Family $family `
        -Version ([string]$train.version) `
        -Commit $commit `
        -Tag $tag `
        -GitEvidencePath $path *> $null
}

$lastFamily = [string]@($train.families)[-1]
$lastDefinition = $families.families.$lastFamily
$lastTag = "$($lastDefinition.publication.tagPrefix)-v$($train.version)"

$wrongMain = [ordered]@{
    schemaVersion = 1
    mainCommit = '1' * 40
    tags = $tags
    stableTags = @()
}
$wrongMainPath = Write-Evidence -Name 'wrong-main' -Value $wrongMain
Assert-Rejected -Name 'wrong-main' -Action {
    & $verifier -Family $lastFamily -Version $train.version -Commit $commit `
        -Tag $lastTag -GitEvidencePath $wrongMainPath *> $null
}

$missingDependencyTags = [ordered]@{}
$missingDependencyTags[$lastTag] = $commit
$missingDependency = [ordered]@{
    schemaVersion = 1
    mainCommit = $commit
    tags = $missingDependencyTags
    stableTags = @()
}
$missingDependencyPath = Write-Evidence -Name 'missing-dependency' -Value $missingDependency
Assert-Rejected -Name 'missing-dependency' -Action {
    & $verifier -Family $lastFamily -Version $train.version -Commit $commit `
        -Tag $lastTag -GitEvidencePath $missingDependencyPath *> $null
}

$stableTag = [ordered]@{
    schemaVersion = 1
    mainCommit = $commit
    tags = $tags
    stableTags = @('provider-v1.0.0')
}
$stableTagPath = Write-Evidence -Name 'stable-tag' -Value $stableTag
Assert-Rejected -Name 'stable-tag' -Action {
    & $verifier -Family $lastFamily -Version $train.version -Commit $commit `
        -Tag $lastTag -GitEvidencePath $stableTagPath *> $null
}

Assert-Rejected -Name 'wrong-version' -Action {
    & $verifier -Family Provider -Version '1.0.0-rc.2' -Commit $commit `
        -Tag 'provider-v1.0.0-rc.2' `
        -GitEvidencePath (Join-Path $testRoot 'valid-Provider.json') *> $null
}

Write-Output (
    "Prerelease verifier accepted all $(@($train.families).Count) ordered families " +
    'and rejected wrong-main, missing-dependency, stable-tag, and wrong-version mutations.')
