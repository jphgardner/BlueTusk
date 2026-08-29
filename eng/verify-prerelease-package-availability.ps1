[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')]
    [string] $BeforeFamily,

    [Parameter(Mandatory)]
    [string] $Version,

    [switch] $IncludeFamily,

    [string] $NuGetFlatContainerUrl = 'https://api.nuget.org/v3-flatcontainer',

    [string] $NpmRegistryUrl = 'https://registry.npmjs.org',

    [string] $PrereleaseTrainPath = (
        Join-Path $PSScriptRoot 'prerelease-train.json'),

    [int] $Attempts = 6,

    [int] $RetryDelaySeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Attempts -lt 1 -or $RetryDelaySeconds -lt 0)
{
    throw 'Availability retry settings must be non-negative and include an attempt.'
}

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$train = Get-Content -LiteralPath $PrereleaseTrainPath -Raw |
    ConvertFrom-Json
$manifest = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'product-families.json') -Raw |
    ConvertFrom-Json
if ([int]$train.schemaVersion -ne 1 -or
    [int]$manifest.schemaVersion -ne 2 -or
    -not [string]::Equals(
        [string]$train.version,
        $Version,
        [StringComparison]::Ordinal))
{
    throw "Version '$Version' is not the registered prerelease train."
}

function Get-PackageId
{
    param([Parameter(Mandatory)][string] $ProjectPath)

    [xml]$document = Get-Content -LiteralPath $ProjectPath -Raw
    $declared = @(
        $document.SelectNodes('//PackageId') |
            ForEach-Object { $_.InnerText } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -First 1
    )
    if ($declared.Count -ne 0)
    {
        return [string]$declared[0]
    }
    return [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
}

function Assert-Available
{
    param(
        [Parameter(Mandatory)][Uri] $Uri,
        [Parameter(Mandatory)][string] $Description
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++)
    {
        try
        {
            $response = Invoke-WebRequest `
                -Method Head `
                -Uri $Uri `
                -Headers @{ 'User-Agent' = 'BlueTusk-prerelease-gate' }
            if ([int]$response.StatusCode -eq 200)
            {
                return
            }
        }
        catch
        {
            if ($attempt -eq $Attempts)
            {
                throw "$Description is not available at '$Uri': $($_.Exception.Message)"
            }
        }

        if ($attempt -lt $Attempts -and $RetryDelaySeconds -gt 0)
        {
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }

    throw "$Description is not available at '$Uri'."
}

$orderedFamilies = @($train.families | ForEach-Object { [string]$_ })
$beforeIndex = [Array]::IndexOf($orderedFamilies, $BeforeFamily)
if ($beforeIndex -lt 0)
{
    throw "Family '$BeforeFamily' is not in the prerelease train."
}

$verified = 0
$lastExclusive = $beforeIndex + $(if ($IncludeFamily) { 1 } else { 0 })
for ($index = 0; $index -lt $lastExclusive; $index++)
{
    $family = $orderedFamilies[$index]
    $definition = $manifest.families.$family
    foreach ($project in @($definition.packages))
    {
        $packageId = Get-PackageId -ProjectPath (
            Join-Path $repositoryRoot $project)
        $normalizedId = $packageId.ToLowerInvariant()
        $normalizedVersion = $Version.ToLowerInvariant()
        $uri = [Uri](
            "$($NuGetFlatContainerUrl.TrimEnd('/'))/$normalizedId/" +
            "$normalizedVersion/$normalizedId.$normalizedVersion.nupkg")
        Assert-Available `
            -Uri $uri `
            -Description "NuGet package '$packageId' version '$Version'"
        $verified++
    }

    if ($null -ne $definition.PSObject.Properties['npmPackages'])
    {
        foreach ($npmPath in @($definition.npmPackages))
        {
            $npmManifest = Get-Content -LiteralPath (
                Join-Path (Join-Path $repositoryRoot $npmPath) 'package.json') `
                -Raw |
                ConvertFrom-Json
            $encodedName = [Uri]::EscapeDataString([string]$npmManifest.name)
            $encodedVersion = [Uri]::EscapeDataString($Version)
            $uri = [Uri](
                "$($NpmRegistryUrl.TrimEnd('/'))/$encodedName/$encodedVersion")
            Assert-Available `
                -Uri $uri `
                -Description "npm package '$($npmManifest.name)' version '$Version'"
            $verified++
        }
    }
}

Write-Output (
    "Verified availability of $verified prerelease package(s) " +
    "$(if ($IncludeFamily) { 'through' } else { 'before' }) $BeforeFamily $Version.")
