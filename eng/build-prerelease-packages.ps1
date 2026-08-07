[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Output = 'artifacts/prerelease',
    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerelease-train.json') -Raw |
    ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 1 -or $manifest.publicationEnabled -ne $true)
{
    throw 'The prerelease train is not enabled with supported schema 1.'
}

$outputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Output))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $outputPath.StartsWith(
        $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "Prerelease output '$outputPath' must be beneath '$artifactsRoot'."
}

if (Test-Path -LiteralPath $outputPath)
{
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
$feedPath = Join-Path $outputPath 'feed'
$null = New-Item -ItemType Directory -Path $feedPath -Force

foreach ($family in @($manifest.families))
{
    $familyPath = Join-Path $outputPath ([string]$family).ToLowerInvariant()
    $arguments = @{
        Family = [string]$family
        Configuration = $Configuration
        Output = $familyPath
        Prerelease = $true
        VersionOverride = [string]$manifest.version
    }
    if ($NoRestore)
    {
        $arguments.NoRestore = $true
    }

    & (Join-Path $PSScriptRoot 'pack-product-family.ps1') @arguments
    & (Join-Path $PSScriptRoot 'verify-product-family-packages.ps1') `
        -Family ([string]$family) `
        -PackageDirectory $familyPath `
        -ExpectedVersion ([string]$manifest.version)
    Get-ChildItem -LiteralPath $familyPath -Filter '*.nupkg' -File |
        Copy-Item -Destination $feedPath
}

Write-Output (
    "Built and verified all six product families at $($manifest.version); " +
    "local NuGet feed: $feedPath")
