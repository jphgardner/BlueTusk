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
$stableVersion = ([string]$train.version -replace '-rc\.\d+$', '')
$currentIteration = [int]([regex]::Match(
    [string]$train.version,
    '-rc\.(?<iteration>\d+)$').Groups['iteration'].Value)
$wrongVersion = "$stableVersion-rc.$($currentIteration + 1)"
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
$sourceRoot = Join-Path $testRoot 'source-root'
foreach ($family in @($train.families))
{
    $definition = $families.families.$family
    $versionPath = Join-Path $sourceRoot $definition.versionFile
    New-Item -ItemType Directory -Path (Split-Path $versionPath -Parent) `
        -Force | Out-Null
    @"
<Project>
  <PropertyGroup>
    <VersionPrefix>$stableVersion</VersionPrefix>
    <VersionSuffix></VersionSuffix>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath $versionPath -Encoding utf8NoBOM
}

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
        -GitEvidencePath $path `
        -SourceRoot $sourceRoot *> $null
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
        -Tag $lastTag -GitEvidencePath $wrongMainPath `
        -SourceRoot $sourceRoot *> $null
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
        -Tag $lastTag -GitEvidencePath $missingDependencyPath `
        -SourceRoot $sourceRoot *> $null
}

$stableTag = [ordered]@{
    schemaVersion = 1
    mainCommit = $commit
    tags = $tags
    stableTags = @("provider-v$stableVersion")
}
$stableTagPath = Write-Evidence -Name 'stable-tag' -Value $stableTag
Assert-Rejected -Name 'stable-tag' -Action {
    & $verifier -Family $lastFamily -Version $train.version -Commit $commit `
        -Tag $lastTag -GitEvidencePath $stableTagPath `
        -SourceRoot $sourceRoot *> $null
}

Assert-Rejected -Name 'wrong-version' -Action {
    & $verifier -Family Provider -Version $wrongVersion -Commit $commit `
        -Tag "provider-v$wrongVersion" `
        -GitEvidencePath (Join-Path $testRoot 'valid-Provider.json') `
        -SourceRoot $sourceRoot *> $null
}

$providerVersionPath = Join-Path $sourceRoot $families.families.Provider.versionFile
(Get-Content -LiteralPath $providerVersionPath -Raw).Replace(
    "<VersionPrefix>$stableVersion</VersionPrefix>",
    '<VersionPrefix>9.9.9</VersionPrefix>') |
    Set-Content -LiteralPath $providerVersionPath -Encoding utf8NoBOM
Assert-Rejected -Name 'wrong-source-version' -Action {
    & $verifier -Family Provider -Version $train.version -Commit $commit `
        -Tag "provider-v$($train.version)" `
        -GitEvidencePath (Join-Path $testRoot 'valid-Provider.json') `
        -SourceRoot $sourceRoot *> $null
}

Write-Output (
    "Prerelease verifier accepted all $(@($train.families).Count) ordered families " +
    'and rejected wrong-main, missing-dependency, stable-tag, wrong-version, ' +
    'and wrong-source-version mutations.')
