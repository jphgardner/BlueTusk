[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidenceRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$resolvedEvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$repositoryPrefix = $repositoryRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedEvidenceRoot.StartsWith(
        $repositoryPrefix,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "V1 package evidence '$resolvedEvidenceRoot' must be below '$repositoryRoot'."
}

$ExpectedCommit = $ExpectedCommit.ToLowerInvariant()
$manifestPath = Join-Path $resolvedEvidenceRoot 'package-manifest.json'
$packageRoot = Join-Path $resolvedEvidenceRoot 'packages'
$sbomRoot = Join-Path $resolvedEvidenceRoot 'sbom'
foreach ($path in @($manifestPath, $packageRoot, $sbomRoot))
{
    if (-not (Test-Path -LiteralPath $path))
    {
        throw "Required V1 package evidence '$path' is missing."
    }
}
$rootFiles = @(
    Get-ChildItem -LiteralPath $resolvedEvidenceRoot -File
)
$rootDirectories = @(
    Get-ChildItem -LiteralPath $resolvedEvidenceRoot -Directory |
        ForEach-Object { $_.Name } |
        Sort-Object
)
if ($rootFiles.Count -ne 1 -or
    $rootFiles[0].Name -ne 'package-manifest.json' -or
    -not [Linq.Enumerable]::SequenceEqual(
        [string[]]@('packages', 'sbom'),
        [string[]]$rootDirectories,
        [StringComparer]::Ordinal))
{
    throw 'The V1 package evidence root contains an unexpected file or directory.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 1)
{
    throw "Expected V1 package-evidence schema 1; found '$($manifest.schemaVersion)'."
}
if (-not [string]::Equals(
        [string]$manifest.sourceCommit,
        $ExpectedCommit,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "Package evidence commit '$($manifest.sourceCommit)' does not match '$ExpectedCommit'."
}

$productManifest = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'product-families.json') -Raw | ConvertFrom-Json
$expectedFamilies = @(
    $productManifest.families.PSObject.Properties |
        ForEach-Object { $_.Name } |
        Sort-Object
)
$familyEntries = @($manifest.families)
$declaredFamilies = @($familyEntries | ForEach-Object { [string]$_.id } | Sort-Object)
if ([int]$manifest.familyCount -ne $expectedFamilies.Count -or
    -not [Linq.Enumerable]::SequenceEqual(
        [string[]]$expectedFamilies,
        [string[]]$declaredFamilies,
        [StringComparer]::Ordinal))
{
    throw 'The V1 package manifest does not contain exactly every product family.'
}

$artifactEntries = @($manifest.artifacts)
$allPackageFiles = @(
    Get-ChildItem -LiteralPath $packageRoot -Recurse -File
)
$actualArtifacts = @(
    $allPackageFiles |
        Where-Object { $_.Extension -in @('.nupkg', '.snupkg', '.tgz') } |
        Sort-Object Name
)
if ([int]$manifest.artifactCount -ne $artifactEntries.Count -or
    $actualArtifacts.Count -ne $artifactEntries.Count)
{
    throw (
        "V1 package artifact count mismatch: manifest=$($manifest.artifactCount), " +
        "records=$($artifactEntries.Count), files=$($actualArtifacts.Count).")
}
if ($allPackageFiles.Count -ne $actualArtifacts.Count)
{
    throw 'The canonical package directory contains a non-package file.'
}

$recordPaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$recordBytes = 0L
foreach ($record in $artifactEntries)
{
    $relativePath = ([string]$record.path).Replace('\', '/')
    if ($relativePath -notmatch '^packages/[A-Za-z0-9@._+-]+$' -or
        -not $recordPaths.Add($relativePath) -or
        [string]$record.family -notin $expectedFamilies)
    {
        throw "Package artifact path '$relativePath' is unsafe, duplicated, or has an unknown family."
    }

    $artifactPath = Join-Path $resolvedEvidenceRoot $relativePath
    $artifact = Get-Item -LiteralPath $artifactPath
    $actualHash = (
        Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ([string]$record.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $actualHash -ne [string]$record.sha256 -or
        $artifact.Length -ne [long]$record.bytes)
    {
        throw "Package artifact '$relativePath' does not match its hash or byte count."
    }
    $recordBytes += $artifact.Length
}
if ($recordBytes -ne [long]$manifest.totalBytes)
{
    throw "Package manifest total is '$($manifest.totalBytes)' bytes; actual total is '$recordBytes'."
}

foreach ($artifact in $actualArtifacts)
{
    $relativePath = "packages/$($artifact.Name)"
    if (-not $recordPaths.Contains($relativePath))
    {
        throw "Package artifact '$relativePath' is not integrity-bound by the manifest."
    }
    if ($artifact.Directory.FullName -ne (Resolve-Path -LiteralPath $packageRoot).Path)
    {
        throw "Package artifact '$relativePath' is not in the canonical package directory."
    }
}

foreach ($family in $expectedFamilies)
{
    $familyRecords = @($artifactEntries | Where-Object {
        [string]$_.family -eq $family
    })
    $summary = @($familyEntries | Where-Object { [string]$_.id -eq $family })
    $familyBytes = [long](
        $familyRecords |
            Measure-Object -Property bytes -Sum
    ).Sum
    if ($summary.Count -ne 1 -or
        $familyRecords.Count -eq 0 -or
        [int]$summary[0].artifactCount -ne $familyRecords.Count -or
        [long]$summary[0].totalBytes -ne $familyBytes)
    {
        throw "Package-family summary for '$family' is missing or inconsistent."
    }
}

foreach ($entry in @(
        @{ Property = 'cycloneDx'; Name = 'bluetusk.cdx.json' },
        @{ Property = 'spdx'; Name = 'bluetusk.spdx.json' },
        @{ Property = 'provenance'; Name = 'build-provenance.json' }))
{
    $record = $manifest.supplyChain.($entry.Property)
    if ($null -eq $record -or
        [string]$record.path -ne "sbom/$($entry.Name)")
    {
        throw "Supply-chain record '$($entry.Property)' is missing or has an unsafe path."
    }
    $path = Join-Path $resolvedEvidenceRoot ([string]$record.path)
    $file = Get-Item -LiteralPath $path
    $actualHash = (
        Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ([string]$record.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $actualHash -ne [string]$record.sha256 -or
        $file.Length -ne [long]$record.bytes)
    {
        throw "Supply-chain artifact '$($entry.Name)' does not match its integrity record."
    }
}
$actualSbomFiles = @(
    Get-ChildItem -LiteralPath $sbomRoot -Recurse -File
)
$actualSbomNames = @(
    $actualSbomFiles |
        ForEach-Object { $_.Name } |
        Sort-Object
)
if ($actualSbomFiles.Count -ne 3 -or
    -not [Linq.Enumerable]::SequenceEqual(
        [string[]]@(
            'bluetusk.cdx.json',
            'bluetusk.spdx.json',
            'build-provenance.json'),
        [string[]]$actualSbomNames,
        [StringComparer]::Ordinal) -or
    @($actualSbomFiles | Where-Object {
            $_.Directory.FullName -ne (Resolve-Path -LiteralPath $sbomRoot).Path
        }).Count -ne 0)
{
    throw 'The canonical SBOM directory must contain exactly two SBOMs and build provenance.'
}

$packageRelative = [IO.Path]::GetRelativePath($repositoryRoot, $packageRoot)
$sbomRelative = [IO.Path]::GetRelativePath($repositoryRoot, $sbomRoot)
& (Join-Path $PSScriptRoot 'verify-supply-chain.ps1') `
    -RepositoryRoot $repositoryRoot `
    -PackageDirectory $packageRelative `
    -SbomDirectory $sbomRelative `
    -ExpectedCommit $ExpectedCommit

$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()) "bluetusk-v1-packages-$([Guid]::NewGuid().ToString('N'))"
try
{
    foreach ($family in $expectedFamilies)
    {
        $familyRoot = Join-Path $temporaryRoot $family
        [IO.Directory]::CreateDirectory($familyRoot) | Out-Null
        foreach ($record in @($artifactEntries | Where-Object {
                    [string]$_.family -eq $family
                }))
        {
            $source = Join-Path $resolvedEvidenceRoot ([string]$record.path)
            [IO.File]::Copy(
                $source,
                (Join-Path $familyRoot ([IO.Path]::GetFileName($source))),
                $false)
        }

        & (Join-Path $PSScriptRoot 'verify-product-family-packages.ps1') `
            -Family $family `
            -PackageDirectory $familyRoot `
            -ExpectedCommit $ExpectedCommit
    }
}
finally
{
    if (Test-Path -LiteralPath $temporaryRoot)
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output (
    "Verified immutable V1 package evidence for commit ${ExpectedCommit}: " +
    "$($expectedFamilies.Count) families, $($artifactEntries.Count) package artifacts, " +
    "$recordBytes bytes, CycloneDX 1.6, SPDX 2.3, and build provenance.")
