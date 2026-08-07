[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')]
    [string] $Family,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $Commit,

    [Parameter(Mandatory)]
    [string] $Tag,

    [string] $GitEvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$train = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'prerelease-train.json') -Raw |
    ConvertFrom-Json
$families = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'product-families.json') -Raw |
    ConvertFrom-Json

if ([int]$train.schemaVersion -ne 1 -or
    $train.publicationEnabled -ne $true)
{
    throw 'The prerelease train is not armed with supported schema 1.'
}
if ([int]$families.schemaVersion -ne 2)
{
    throw 'The stable product-family manifest must remain on schema 2.'
}
if (-not [string]::Equals(
        [string]$train.version,
        $Version,
        [StringComparison]::Ordinal))
{
    throw "Prerelease version '$Version' does not match '$($train.version)'."
}
if ($Version -notmatch '^1\.0\.0-rc\.\d+$')
{
    throw "Prerelease version '$Version' is not an allowed V1 RC version."
}

$orderedFamilies = @($train.families | ForEach-Object { [string]$_ })
$familyIndex = [Array]::IndexOf($orderedFamilies, $Family)
if ($familyIndex -lt 0 -or
    $orderedFamilies.Count -ne 6 -or
    @($orderedFamilies | Sort-Object -Unique).Count -ne 6)
{
    throw 'The prerelease train must contain exactly the six unique V1 families.'
}

$definition = $families.families.$Family
if ($null -eq $definition)
{
    throw "Product family '$Family' is not registered."
}
if ($definition.publication.enabled -eq $true -or
    -not [string]::Equals(
        [string]$definition.publication.channel,
        'stable',
        [StringComparison]::Ordinal))
{
    throw (
        "Stable publication for '$Family' must remain disabled on the stable channel " +
        'while publishing a prerelease.')
}

[xml]$versionDocument = Get-Content -LiteralPath (
    Join-Path $repositoryRoot $definition.versionFile) -Raw
if ([string]$versionDocument.Project.PropertyGroup.VersionPrefix -ne '1.0.0' -or
    -not [string]::IsNullOrWhiteSpace(
        [string]$versionDocument.Project.PropertyGroup.VersionSuffix))
{
    throw "Stable source version for '$Family' must remain exact 1.0.0."
}

$expectedTag = "$($definition.publication.tagPrefix)-v$Version"
if (-not [string]::Equals($Tag, $expectedTag, [StringComparison]::Ordinal))
{
    throw "Prerelease tag '$Tag' does not match expected '$expectedTag'."
}

$gitEvidence = $null
if (-not [string]::IsNullOrWhiteSpace($GitEvidencePath))
{
    $gitEvidence = Get-Content -LiteralPath (
        Resolve-Path -LiteralPath $GitEvidencePath) -Raw |
        ConvertFrom-Json
    if ([int]$gitEvidence.schemaVersion -ne 1)
    {
        throw 'Prerelease Git evidence must use schema 1.'
    }
}

function Resolve-ReleaseTagCommit
{
    param([Parameter(Mandatory)][string] $Name)

    if ($null -ne $gitEvidence)
    {
        $property = $gitEvidence.tags.PSObject.Properties[$Name]
        if ($null -eq $property)
        {
            return $null
        }
        return [string]$property.Value
    }

    & git -C $repositoryRoot show-ref --verify --quiet "refs/tags/$Name"
    if ($LASTEXITCODE -ne 0)
    {
        return $null
    }
    return (& git -C $repositoryRoot rev-parse "$Name^{commit}").Trim()
}

$resolvedCommit = Resolve-ReleaseTagCommit -Name $Tag
if ([string]::IsNullOrWhiteSpace($resolvedCommit) -or
    -not [string]::Equals($resolvedCommit, $Commit, [StringComparison]::OrdinalIgnoreCase))
{
    throw "Prerelease tag '$Tag' does not resolve to exact commit '$Commit'."
}

$mainCommit = if ($null -ne $gitEvidence)
{
    [string]$gitEvidence.mainCommit
}
else
{
    & git -C $repositoryRoot show-ref --verify --quiet refs/remotes/origin/main
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Prerelease verification requires a fetched origin/main reference.'
    }
    (& git -C $repositoryRoot rev-parse refs/remotes/origin/main).Trim()
}
if (-not [string]::Equals($mainCommit, $Commit, [StringComparison]::OrdinalIgnoreCase))
{
    throw (
        "Prerelease tags must point at the reviewed immutable origin/main commit; " +
        "origin/main is '$mainCommit', tag commit is '$Commit'.")
}

for ($index = 0; $index -lt $familyIndex; $index++)
{
    $dependencyFamily = $orderedFamilies[$index]
    $dependencyDefinition = $families.families.$dependencyFamily
    $dependencyTag =
        "$($dependencyDefinition.publication.tagPrefix)-v$Version"
    $dependencyCommit = Resolve-ReleaseTagCommit -Name $dependencyTag
    if ([string]::IsNullOrWhiteSpace($dependencyCommit))
    {
        throw "Required prerelease dependency tag '$dependencyTag' is missing."
    }
    if (-not [string]::Equals(
            $dependencyCommit,
            $Commit,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw (
            "Dependency tag '$dependencyTag' resolves to '$dependencyCommit', " +
            "not immutable prerelease commit '$Commit'.")
    }
}

$stableTags = @(
    if ($null -ne $gitEvidence)
    {
        @($gitEvidence.stableTags)
    }
    else
    {
        @(& git -C $repositoryRoot tag --list '*-v1.0.0')
    }
)
if ($stableTags.Count -ne 0)
{
    throw (
        'Stable V1 tags already exist while the prerelease train is active: ' +
        ($stableTags -join ', '))
}

Write-Output (
    "Verified $Family prerelease '$Version' at immutable origin/main commit " +
    "'$Commit' with tag '$Tag'; stable publication remains disabled.")
