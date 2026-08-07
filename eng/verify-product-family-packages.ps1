[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')]
    [string] $Family,

    [string] $PackageDirectory = 'artifacts/packages',

    [string] $ExpectedCommit,

    [string] $ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FamilyVersion
{
    param(
        [Parameter(Mandatory)]
        [object] $Definition,

        [Parameter(Mandatory)]
        [string] $Root
    )

    [xml] $document = Get-Content -LiteralPath (
        Join-Path $Root $Definition.versionFile) -Raw
    $prefix = [string]$document.Project.PropertyGroup.VersionPrefix
    $suffix = [string]$document.Project.PropertyGroup.VersionSuffix
    if ([string]::IsNullOrWhiteSpace($prefix))
    {
        throw "Version file '$($Definition.versionFile)' has no VersionPrefix."
    }

    if ([string]::IsNullOrWhiteSpace($suffix))
    {
        return $prefix
    }

    return "$prefix-$suffix"
}

function Get-ProjectPackageId
{
    param(
        [Parameter(Mandatory)]
        [string] $ProjectPath
    )

    [xml] $document = Get-Content -LiteralPath $ProjectPath -Raw
    $packageId = @(
        $document.SelectNodes('//PackageId') |
            ForEach-Object { $_.InnerText } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -First 1
    )
    if ($packageId.Count -gt 0)
    {
        return [string]$packageId[0]
    }

    return [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
}

function Get-Nuspec
{
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchive] $Archive,

        [Parameter(Mandatory)]
        [string] $PackageName
    )

    $entries = @($Archive.Entries | Where-Object FullName -Like '*.nuspec')
    if ($entries.Count -ne 1)
    {
        throw "Package '$PackageName' contains $($entries.Count) nuspec files."
    }

    $reader = [IO.StreamReader]::new($entries[0].Open())
    try
    {
        return [xml]$reader.ReadToEnd()
    }
    finally
    {
        $reader.Dispose()
    }
}

function Assert-ArchivePaths
{
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchive] $Archive,

        [Parameter(Mandatory)]
        [string] $PackageName
    )

    foreach ($entry in $Archive.Entries)
    {
        $path = [string]$entry.FullName
        if ([string]::IsNullOrWhiteSpace($path) -or
            $path.StartsWith('/', [StringComparison]::Ordinal) -or
            $path.StartsWith('\', [StringComparison]::Ordinal) -or
            $path.Contains('\', [StringComparison]::Ordinal) -or
            @($path.Split('/')) -contains '..')
        {
            throw "Package '$PackageName' contains unsafe archive path '$path'."
        }
    }
}

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$manifestPath = Join-Path $PSScriptRoot 'product-families.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2)
{
    throw "Expected product-family manifest schema 2; found '$($manifest.schemaVersion)'."
}

$prereleaseManifest = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion))
{
    $prereleaseManifest = Get-Content -LiteralPath (
        Join-Path $PSScriptRoot 'prerelease-train.json') -Raw |
        ConvertFrom-Json
    if ([int]$prereleaseManifest.schemaVersion -ne 1 -or
        $prereleaseManifest.publicationEnabled -ne $true -or
        -not [string]::Equals(
            [string]$prereleaseManifest.version,
            $ExpectedVersion,
            [StringComparison]::Ordinal))
    {
        throw "ExpectedVersion '$ExpectedVersion' is not the armed prerelease train."
    }
}

$definition = $manifest.families.$Family
if ($null -eq $definition)
{
    throw "Product family '$Family' is not registered."
}

$familyVersions = @{}
$packageFamilies = @{}
foreach ($familyProperty in $manifest.families.PSObject.Properties)
{
    $familyVersions[$familyProperty.Name] = if (
        [string]::IsNullOrWhiteSpace($ExpectedVersion))
    {
        Get-FamilyVersion `
            -Definition $familyProperty.Value `
            -Root $repositoryRoot
    }
    else
    {
        $ExpectedVersion
    }
    foreach ($project in @($familyProperty.Value.packages))
    {
        $projectPath = Join-Path $repositoryRoot $project
        $packageId = Get-ProjectPackageId -ProjectPath $projectPath
        if ($packageFamilies.ContainsKey($packageId))
        {
            throw "Package ID '$packageId' is registered by more than one product family."
        }

        $packageFamilies[$packageId] = $familyProperty.Name
    }
}

$familyVersion = [string]$familyVersions[$Family]
if ([string]::IsNullOrWhiteSpace($ExpectedCommit))
{
    $ExpectedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Could not resolve the expected package source commit.'
    }
}
if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$')
{
    throw "ExpectedCommit '$ExpectedCommit' is not a full Git commit."
}

$fullPackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$expectedNuGet = [Collections.Generic.Dictionary[string, object]]::new(
    [StringComparer]::Ordinal)
foreach ($project in @($definition.packages))
{
    $projectPath = Join-Path $repositoryRoot $project
    [xml] $projectDocument = Get-Content -LiteralPath $projectPath -Raw
    $packageId = Get-ProjectPackageId -ProjectPath $projectPath
    $includeBuildOutput = @(
        $projectDocument.SelectNodes('//IncludeBuildOutput') |
            ForEach-Object { $_.InnerText } |
            Select-Object -First 1
    )
    $symbolsExpected =
        $includeBuildOutput.Count -eq 0 -or
        -not [string]::Equals(
            [string]$includeBuildOutput[0],
            'false',
            [StringComparison]::OrdinalIgnoreCase)
    $expectedNuGet.Add(
        "$packageId.$familyVersion.nupkg",
        [pscustomobject]@{
            Id = $packageId
            SymbolsExpected = $symbolsExpected
        })
}

$actualNuGet = @(
    Get-ChildItem -LiteralPath $fullPackageDirectory -Filter '*.nupkg' -File |
        Sort-Object Name
)
if (-not [Linq.Enumerable]::SequenceEqual(
        [string[]]@($expectedNuGet.Keys | Sort-Object),
        [string[]]@($actualNuGet.Name | Sort-Object),
        [StringComparer]::Ordinal))
{
    throw (
        "NuGet package set does not match the $Family manifest. Expected: " +
        "$(@($expectedNuGet.Keys | Sort-Object) -join ', '); actual: " +
        "$(@($actualNuGet.Name | Sort-Object) -join ', ').")
}

$expectedSymbolNames = @(
    $expectedNuGet.GetEnumerator() |
        Where-Object { $_.Value.SymbolsExpected } |
        ForEach-Object {
            $_.Key.Substring(0, $_.Key.Length - '.nupkg'.Length) + '.snupkg'
        } |
        Sort-Object
)
$actualSymbols = @(
    Get-ChildItem -LiteralPath $fullPackageDirectory -Filter '*.snupkg' -File |
        Sort-Object Name
)
if (-not [Linq.Enumerable]::SequenceEqual(
        [string[]]$expectedSymbolNames,
        [string[]]@($actualSymbols.Name),
        [StringComparer]::Ordinal))
{
    throw (
        "Symbol package set does not match the $Family manifest. Expected: " +
        "$($expectedSymbolNames -join ', '); actual: " +
        "$(@($actualSymbols.Name) -join ', ').")
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$verifiedNuGet = 0
foreach ($package in $actualNuGet)
{
    $expected = $expectedNuGet[$package.Name]
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try
    {
        Assert-ArchivePaths -Archive $archive -PackageName $package.Name
        $nuspec = Get-Nuspec -Archive $archive -PackageName $package.Name
        $metadata = $nuspec.package.metadata
        if (-not [string]::Equals(
                [string]$metadata.id,
                [string]$expected.Id,
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [string]$metadata.version,
                $familyVersion,
                [StringComparison]::Ordinal))
        {
            throw "Package '$($package.Name)' has unexpected ID or version."
        }

        if (-not [string]::Equals(
                [string]$metadata.license.InnerText,
                'MIT',
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [string]$metadata.license.GetAttribute('type'),
                'expression',
                [StringComparison]::Ordinal))
        {
            throw "Package '$($package.Name)' does not declare the MIT license expression."
        }

        if (-not [string]::Equals(
                [string]$metadata.readme,
                'README.md',
                [StringComparison]::Ordinal) -or
            $null -eq $archive.GetEntry('README.md'))
        {
            throw "Package '$($package.Name)' does not contain its declared README."
        }

        if ([string]::IsNullOrWhiteSpace([string]$metadata.description) -or
            [string]::IsNullOrWhiteSpace([string]$metadata.authors))
        {
            throw "Package '$($package.Name)' has incomplete package metadata."
        }

        if (-not [string]::Equals(
                [string]$metadata.projectUrl,
                'https://github.com/jphgardner/BlueTusk',
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [string]$metadata.repository.GetAttribute('type'),
                'git',
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [string]$metadata.repository.GetAttribute('url'),
                'https://github.com/jphgardner/BlueTusk',
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [string]$metadata.repository.GetAttribute('commit'),
                $ExpectedCommit,
                [StringComparison]::OrdinalIgnoreCase))
        {
            throw "Package '$($package.Name)' is not bound to repository commit '$ExpectedCommit'."
        }

        $dependencies = @($nuspec.SelectNodes("//*[local-name()='dependency']"))
        foreach ($dependency in $dependencies)
        {
            $dependencyId = [string]$dependency.GetAttribute('id')
            if ($packageFamilies.ContainsKey($dependencyId))
            {
                $dependencyFamily = [string]$packageFamilies[$dependencyId]
                $expectedDependencyVersion = [string]$familyVersions[$dependencyFamily]
                if (-not [string]::Equals(
                        [string]$dependency.GetAttribute('version'),
                        $expectedDependencyVersion,
                        [StringComparison]::Ordinal))
                {
                    throw (
                        "Package '$($package.Name)' references '$dependencyId' version " +
                        "'$($dependency.GetAttribute('version'))', " +
                        "expected '$expectedDependencyVersion'.")
                }
            }
        }

        $verifiedNuGet++
    }
    finally
    {
        $archive.Dispose()
    }
}

foreach ($symbolPackage in $actualSymbols)
{
    $archive = [IO.Compression.ZipFile]::OpenRead($symbolPackage.FullName)
    try
    {
        Assert-ArchivePaths -Archive $archive -PackageName $symbolPackage.Name
        if (@($archive.Entries | Where-Object FullName -Like '*.pdb').Count -eq 0)
        {
            throw "Symbol package '$($symbolPackage.Name)' contains no portable PDB."
        }
    }
    finally
    {
        $archive.Dispose()
    }
}

$expectedNpm = @{}
$npmPackagePaths = @()
if ($null -ne $definition.PSObject.Properties['npmPackages'])
{
    $npmPackagePaths = @($definition.npmPackages)
}
foreach ($npmPath in $npmPackagePaths)
{
    $sourceManifestPath = Join-Path (Join-Path $repositoryRoot $npmPath) 'package.json'
    $sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json
    $archiveName =
        ([string]$sourceManifest.name).TrimStart('@').Replace('/', '-') +
        "-$familyVersion.tgz"
    $expectedNpm[$archiveName] = $sourceManifest
}

$actualNpm = @(
    Get-ChildItem -LiteralPath $fullPackageDirectory -Filter '*.tgz' -File |
        Sort-Object Name
)
$actualNpmNames = @($actualNpm | ForEach-Object { $_.Name })
if (-not [Linq.Enumerable]::SequenceEqual(
        [string[]]@($expectedNpm.Keys | Sort-Object),
        [string[]]$actualNpmNames,
        [StringComparer]::Ordinal))
{
    throw (
        "npm package set does not match the $Family manifest. Expected: " +
        "$(@($expectedNpm.Keys | Sort-Object) -join ', '); actual: " +
        "$($actualNpmNames -join ', ').")
}

foreach ($package in $actualNpm)
{
    $manifestJson = @(& tar -xOf $package.FullName 'package/package.json') -join "`n"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($manifestJson))
    {
        throw "Could not read package.json from '$($package.Name)'."
    }

    $npmManifest = $manifestJson | ConvertFrom-Json
    $sourceManifest = $expectedNpm[$package.Name]
    if (-not [string]::Equals(
            [string]$npmManifest.name,
            [string]$sourceManifest.name,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$npmManifest.version,
            $familyVersion,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$npmManifest.license,
            'MIT',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$npmManifest.repository.url,
            'git+https://github.com/jphgardner/BlueTusk.git',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$npmManifest.publishConfig.access,
            'public',
            [StringComparison]::Ordinal))
    {
        throw "npm archive '$($package.Name)' has incomplete or unexpected metadata."
    }

    foreach ($lifecycleScript in @('preinstall', 'install', 'postinstall'))
    {
        if ($null -ne $npmManifest.scripts.PSObject.Properties[$lifecycleScript])
        {
            throw "npm archive '$($package.Name)' contains forbidden '$lifecycleScript' script."
        }
    }

    foreach ($dependencyProperty in @('dependencies', 'optionalDependencies'))
    {
        if ($null -eq $npmManifest.PSObject.Properties[$dependencyProperty])
        {
            continue
        }

        foreach ($dependency in $npmManifest.$dependencyProperty.PSObject.Properties)
        {
            if ($dependency.Name.StartsWith('@bluetusk/', [StringComparison]::Ordinal) -and
                -not [string]::Equals(
                    [string]$dependency.Value,
                    $familyVersion,
                    [StringComparison]::Ordinal))
            {
                throw (
                    "npm archive '$($package.Name)' references '$($dependency.Name)' " +
                    "version '$($dependency.Value)', expected '$familyVersion'.")
            }
        }
    }

    $archiveEntries = @(& tar -tf $package.FullName)
    if ($LASTEXITCODE -ne 0 -or
        $archiveEntries -notcontains 'package/README.md' -or
        @($archiveEntries | Where-Object { $_ -like 'package/dist/*.js' }).Count -eq 0 -or
        @($archiveEntries | Where-Object { $_ -like 'package/dist/*.d.ts' }).Count -eq 0)
    {
        throw "npm archive '$($package.Name)' is missing README or compiled distribution files."
    }
}

Write-Output (
    "Verified $Family $familyVersion package set at commit ${ExpectedCommit}: " +
    "$verifiedNuGet NuGet package(s), $($actualSymbols.Count) symbol package(s), " +
    "$($actualNpm.Count) npm package(s).")
