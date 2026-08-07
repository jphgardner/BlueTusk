[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$applicationsRoot = Join-Path $repositoryRoot 'applications'
$expectedVersion = [string](
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerelease-train.json') -Raw |
        ConvertFrom-Json).version
[xml]$central = Get-Content -LiteralPath (
    Join-Path $applicationsRoot 'Directory.Packages.props') -Raw
$centralVersions = @{}
foreach ($node in @($central.SelectNodes('//PackageVersion')))
{
    $centralVersions[[string]$node.Include] = [string]$node.Version
}

$projects = @(Get-ChildItem -LiteralPath $applicationsRoot -Filter '*.csproj' -File -Recurse)
if ($projects.Count -ne 20)
{
    throw "Expected 20 application solution projects; found $($projects.Count)."
}

$solutionPath = Join-Path $applicationsRoot 'BlueTusk.Applications.slnx'
[xml]$solution = Get-Content -LiteralPath $solutionPath -Raw
$solutionProjects = @($solution.SelectNodes('//Project') | ForEach-Object {
        [IO.Path]::GetFullPath((Join-Path $applicationsRoot ([string]$_.Path)))
    })
$repositoryProjects = @($projects | ForEach-Object { $_.FullName })
if ($solutionProjects.Count -ne $projects.Count -or
    @($solutionProjects | Where-Object { $_ -notin $repositoryProjects }).Count -ne 0 -or
    @($repositoryProjects | Where-Object { $_ -notin $solutionProjects }).Count -ne 0)
{
    throw 'BlueTusk.Applications.slnx must contain each of the 20 application projects exactly once.'
}

$blueTuskReferences = 0
foreach ($project in $projects)
{
    [xml]$document = Get-Content -LiteralPath $project.FullName -Raw
    foreach ($reference in @($document.SelectNodes('//ProjectReference')))
    {
        $resolved = [IO.Path]::GetFullPath((Join-Path $project.DirectoryName ([string]$reference.Include)))
        if (-not $resolved.StartsWith(
                $applicationsRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase))
        {
            throw "Project '$($project.FullName)' escapes the applications package boundary: '$resolved'."
        }
    }

    foreach ($reference in @($document.SelectNodes('//PackageReference')))
    {
        $package = [string]$reference.Include
        if (-not $package.StartsWith('BlueTusk.', [StringComparison]::Ordinal))
        {
            continue
        }

        $blueTuskReferences++
        if ($reference.HasAttribute('Version') -or
            -not $centralVersions.ContainsKey($package) -or
            -not [string]::Equals(
                [string]$centralVersions[$package],
                $expectedVersion,
                [StringComparison]::Ordinal))
        {
            throw "Project '$($project.Name)' does not centrally pin '$package' to '$expectedVersion'."
        }
    }
}

foreach ($webManifest in @(Get-ChildItem -LiteralPath (
        Join-Path $applicationsRoot 'web') -Filter 'package.json' -File -Recurse))
{
    $package = Get-Content -LiteralPath $webManifest.FullName -Raw | ConvertFrom-Json
    $lockPath = Join-Path $webManifest.DirectoryName 'package-lock.json'
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf))
    {
        throw "Web app '$($package.name)' has no immutable npm lockfile."
    }
    $lockText = Get-Content -LiteralPath $lockPath -Raw
    if ($lockText -match 'file:|[A-Za-z]:[/\\]Workspace')
    {
        throw "Web app '$($package.name)' lockfile contains a local artifact reference."
    }
    $lock = $lockText | ConvertFrom-Json -AsHashtable
    foreach ($dependency in @($package.dependencies.PSObject.Properties | Where-Object {
            $_.Name.StartsWith('@bluetusk/', [StringComparison]::Ordinal)
        }))
    {
        if (-not [string]::Equals(
                [string]$dependency.Value,
                $expectedVersion,
                [StringComparison]::Ordinal))
        {
            throw "Web app '$($package.name)' does not pin '$($dependency.Name)' to '$expectedVersion'."
        }
        $lockEntry = $lock['packages']["node_modules/$($dependency.Name)"]
        $leaf = $dependency.Name.Split('/')[-1]
        $expectedResolved = (
            "https://registry.npmjs.org/$($dependency.Name)/-/" +
            "$leaf-$expectedVersion.tgz")
        if ([string]$lockEntry.version -ne $expectedVersion -or
            [string]$lockEntry.resolved -ne $expectedResolved -or
            [string]$lockEntry.integrity -notmatch '^sha512-[A-Za-z0-9+/]+={0,2}$')
        {
            throw "Web app '$($package.name)' lock entry for '$($dependency.Name)' is not the exact public RC artifact."
        }
    }
}

if ($blueTuskReferences -lt 20)
{
    throw "Expected broad BlueTusk package coverage; found only $blueTuskReferences references."
}

Write-Output (
    "Verified 20 application projects, $blueTuskReferences BlueTusk package references, " +
    "three browser clients, and the package-only $expectedVersion boundary.")
