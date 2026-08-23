[CmdletBinding()]
param(
    [string] $PackageCache = 'artifacts/application-nuget-cache',
    [string] $CandidateFeed = 'artifacts/prerelease/feed',
    [string] $ExpectedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($ExpectedCommit))
{
    $ExpectedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Could not resolve the application candidate commit.'
    }
}
if ($ExpectedCommit -cnotmatch '^[0-9a-f]{40}$')
{
    throw "Expected application candidate commit '$ExpectedCommit' is not a lowercase full SHA."
}
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PackageCache))
$feedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $CandidateFeed))
$artifactsPrefix = $artifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($cacheRoot, $feedRoot))
{
    if (-not $path.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Application restore evidence path '$path' must remain below '$artifactsRoot'."
    }
}
if (-not (Test-Path -LiteralPath $cacheRoot -PathType Container) -or
    -not (Test-Path -LiteralPath $feedRoot -PathType Container))
{
    throw 'The isolated application package cache and candidate feed must both exist.'
}

$expectedVersion = [string](
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerelease-train.json') -Raw |
        ConvertFrom-Json).version
$assetFiles = @(Get-ChildItem -LiteralPath (
        Join-Path $repositoryRoot 'applications') -Filter 'project.assets.json' -File -Recurse)
if ($assetFiles.Count -ne 20)
{
    throw "Expected restore evidence for 20 application projects; found $($assetFiles.Count)."
}

$packages = [ordered]@{}
foreach ($assetFile in $assetFiles)
{
    $assets = Get-Content -LiteralPath $assetFile.FullName -Raw | ConvertFrom-Json
    $folders = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
            [IO.Path]::GetFullPath([string]$_)
        })
    if ($folders.Count -ne 1 -or
        -not [string]::Equals(
            $folders[0],
            $cacheRoot,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "'$($assetFile.FullName)' was not restored exclusively into '$cacheRoot'."
    }

    foreach ($library in @($assets.libraries.PSObject.Properties | Where-Object {
                $_.Value.type -eq 'package' -and
                $_.Name.StartsWith('BlueTusk.', [StringComparison]::Ordinal)
            }))
    {
        $separator = $library.Name.LastIndexOf('/')
        if ($separator -le 0)
        {
            throw "Invalid BlueTusk package identity '$($library.Name)'."
        }
        $identity = $library.Name.Substring(0, $separator)
        $version = $library.Name.Substring($separator + 1)
        if (-not [string]::Equals(
                $version,
                $expectedVersion,
                [StringComparison]::Ordinal))
        {
            throw "Application package '$identity' resolved '$version', not '$expectedVersion'."
        }
        $packages[$identity.ToLowerInvariant()] = [ordered]@{
            identity = $identity
            version = $version
        }
    }
}

if ($packages.Count -lt 20)
{
    throw "Expected broad candidate package coverage; only $($packages.Count) unique packages resolved."
}
foreach ($record in $packages.Values)
{
    $metadataPath = Join-Path $cacheRoot (
        "$($record.identity.ToLowerInvariant())/$($record.version)/.nupkg.metadata")
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf))
    {
        throw "Restore metadata is missing for '$($record.identity)/$($record.version)'."
    }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $source = [IO.Path]::GetFullPath([string]$metadata.source)
    if (-not [string]::Equals(
            $source,
            $feedRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]::IsNullOrWhiteSpace([string]$metadata.contentHash))
    {
        throw (
            "'$($record.identity)/$($record.version)' was not content-hash verified " +
            "from the candidate feed '$feedRoot'.")
    }

    $nuspecPath = Join-Path (Split-Path $metadataPath -Parent) (
        "$($record.identity.ToLowerInvariant()).nuspec")
    if (-not (Test-Path -LiteralPath $nuspecPath -PathType Leaf))
    {
        throw "Package manifest is missing for '$($record.identity)/$($record.version)'."
    }
    [xml]$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
    $repository = $nuspec.package.metadata.repository
    if (-not [string]::Equals(
            [string]$repository.commit,
            $ExpectedCommit,
            [StringComparison]::Ordinal))
    {
        throw (
            "'$($record.identity)/$($record.version)' records commit " +
            "'$($repository.commit)', not candidate '$ExpectedCommit'.")
    }
}

Write-Output (
    "Verified $($assetFiles.Count) application restores and $($packages.Count) unique " +
    "BlueTusk $expectedVersion packages from exact commit $ExpectedCommit and the isolated candidate feed.")
