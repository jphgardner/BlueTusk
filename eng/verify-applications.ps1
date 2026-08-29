[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$applicationsRoot = Join-Path $repositoryRoot 'applications'
$expectedVersion = [string](
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerelease-train.json') -Raw |
        ConvertFrom-Json).version
$localNpmPackageRoot = Join-Path $repositoryRoot 'artifacts/prerelease/live'
$hasLocalNpmCandidate = Test-Path -LiteralPath $localNpmPackageRoot -PathType Container
$verifiedLocalNpmPackages = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)

function Get-NpmIntegrity
{
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $hex = (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash
    $bytes = [byte[]]::new($hex.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++)
    {
        $bytes[$index] = [Convert]::ToByte($hex.Substring($index * 2, 2), 16)
    }

    return 'sha512-' + [Convert]::ToBase64String($bytes)
}
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

$webRoot = Join-Path $applicationsRoot 'web'
$webManifests = @(Get-ChildItem -LiteralPath $webRoot -Directory | ForEach-Object {
        $manifest = Join-Path $_.FullName 'package.json'
        if (Test-Path -LiteralPath $manifest -PathType Leaf)
        {
            Get-Item -LiteralPath $manifest
        }
    })
if ($webManifests.Count -ne 4)
{
    throw "Expected four browser-client manifests; found $($webManifests.Count)."
}

foreach ($webManifest in $webManifests)
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

        if ($hasLocalNpmCandidate)
        {
            $candidatePath = Join-Path $localNpmPackageRoot (
                "bluetusk-$leaf-$expectedVersion.tgz")
            if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf))
            {
                throw "The local npm candidate for '$($dependency.Name)' is missing: '$candidatePath'."
            }

            $candidateIntegrity = Get-NpmIntegrity -Path $candidatePath
            if (-not [string]::Equals(
                    [string]$lockEntry.integrity,
                    $candidateIntegrity,
                    [StringComparison]::Ordinal))
            {
                throw "Web app '$($package.name)' lock integrity for '$($dependency.Name)' does not match the locally verified RC tarball."
            }
            $null = $verifiedLocalNpmPackages.Add($dependency.Name)
        }
    }
}

$containerRoot = Join-Path $applicationsRoot 'containers'
$expectedContainerBases = [ordered]@{
    'Dockerfile.api' = @(
        'FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:0e53453ccfc8ff2d51319fe80c678971c6d0f8008dff3565fa88e15840b69854 AS build',
        'FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble-chiseled@sha256:edec6ea65a92f432083a8f75fc3c18addd004015bbd4d523ce1d13e23b347008'
    )
    'Dockerfile.worker' = @(
        'FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble@sha256:0e53453ccfc8ff2d51319fe80c678971c6d0f8008dff3565fa88e15840b69854 AS build',
        'FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble-chiseled@sha256:edec6ea65a92f432083a8f75fc3c18addd004015bbd4d523ce1d13e23b347008'
    )
    'Dockerfile.ui' = @(
        'FROM node:24.19.0-alpine3.23@sha256:244cc2b53f46f9e876304391d17682b0ddae9ac33491f4857e25e35a36ba7995 AS build',
        'FROM nginxinc/nginx-unprivileged:1.30.3-alpine3.23@sha256:b3f2436575bd5be7386518084d842dac414ab4962712afa31e99e0942a56e3b2'
    )
}
$requiredContainerSnippets = [ordered]@{
    'Dockerfile.api' = @('DOTNET_EnableDiagnostics=0', '/p:UseAppHost=false', 'USER 1654:1654')
    'Dockerfile.worker' = @('DOTNET_EnableDiagnostics=0', '/p:UseAppHost=false', 'USER 1654:1654')
    'Dockerfile.ui' = @('npm ci --ignore-scripts --no-audit --no-fund', 'USER 101:101')
}
foreach ($containerDefinition in $expectedContainerBases.GetEnumerator())
{
    $path = Join-Path $containerRoot $containerDefinition.Key
    $content = Get-Content -LiteralPath $path -Raw
    $actualBases = @(
        Get-Content -LiteralPath $path |
            Where-Object { $_ -match '^FROM\s' }
    )
    $expectedBases = @($containerDefinition.Value)
    if ($actualBases.Count -ne $expectedBases.Count -or
        @(Compare-Object -ReferenceObject $expectedBases -DifferenceObject $actualBases -SyncWindow 0).Count -ne 0)
    {
        throw "Container '$($containerDefinition.Key)' does not use the reviewed digest-pinned base sequence."
    }

    foreach ($snippet in @($requiredContainerSnippets[$containerDefinition.Key]))
    {
        if (-not $content.Contains($snippet, [StringComparison]::Ordinal))
        {
            throw "Container '$($containerDefinition.Key)' is missing hardening contract '$snippet'."
        }
    }
}

$imageWorkflowPath = Join-Path $repositoryRoot '.github/workflows/applications-images.yml'
$imageWorkflow = Get-Content -LiteralPath $imageWorkflowPath -Raw
$imageContract = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'application-image-evidence-contract.json') -Raw |
    ConvertFrom-Json
if (-not [string]::Equals(
        [string]$imageContract.rcVersion,
        $expectedVersion,
        [StringComparison]::Ordinal) -or
    -not $imageWorkflow.Contains(
        "-Version $expectedVersion",
        [StringComparison]::Ordinal) -or
    -not $imageWorkflow.Contains(
        "rcVersion = '$expectedVersion'",
        [StringComparison]::Ordinal))
{
    throw 'Application source, image workflow, and evidence contract must use the same exact RC version.'
}
foreach ($snippet in @(
        'name: Verify runtime framework closure',
        "if: matrix.component != 'ui'",
        'docker run --rm --entrypoint dotnet',
        'Microsoft.NETCore.App 10.0.11',
        'Microsoft.AspNetCore.App 10.0.11',
        'name: Verify worker outage startup',
        "if: matrix.component == 'worker'",
        '--network none --read-only',
        'docker inspect',
        'sleep 20'))
{
    if (-not $imageWorkflow.Contains($snippet, [StringComparison]::Ordinal))
    {
        throw "Application image workflow is missing runtime-closure contract '$snippet'."
    }
}

$candidateNuGetConfigurationPath = Join-Path (
    Join-Path $repositoryRoot 'eng/nuget') 'applications-candidate.config'
[xml]$candidateNuGetConfiguration = Get-Content -LiteralPath (
    $candidateNuGetConfigurationPath) -Raw
$candidateSource = @($candidateNuGetConfiguration.SelectNodes(
        '/configuration/packageSources/add') | Where-Object {
        [string]$_.key -eq 'BlueTuskCandidate'
    })
$candidateMapping = @($candidateNuGetConfiguration.SelectNodes(
        '/configuration/packageSourceMapping/packageSource') | Where-Object {
        [string]$_.key -eq 'BlueTuskCandidate'
    })
if ($candidateSource.Count -ne 1 -or
    [string]$candidateSource[0].value -ne '../../artifacts/prerelease/feed' -or
    $candidateMapping.Count -ne 1 -or
    @($candidateMapping[0].package | Where-Object {
        [string]$_.pattern -eq 'BlueTusk.*'
    }).Count -ne 1)
{
    throw 'The application candidate restore must map BlueTusk.* only to the locally packed candidate feed.'
}

$buildWorkflow = Get-Content -LiteralPath (
    Join-Path $repositoryRoot '.github/workflows/build.yml') -Raw
foreach ($snippet in @(
        '--configfile eng/nuget/applications-candidate.config',
        '--packages artifacts/application-nuget-cache',
        '--force-evaluate',
        '--no-http-cache',
        './eng/verify-application-candidate-restore.ps1'))
{
    if (-not $buildWorkflow.Contains($snippet, [StringComparison]::Ordinal))
    {
        throw "The application build workflow is missing deterministic candidate restore contract '$snippet'."
    }
}

$deploymentScript = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'eng/deploy-applications-rc.ps1') -Raw
foreach ($snippet in @(
        'verify-application-platform-health.ps1',
        '-RequireApplications'))
{
    if (-not $deploymentScript.Contains($snippet, [StringComparison]::Ordinal))
    {
        throw "RC deployment script is missing live-platform health contract '$snippet'."
    }
}

$nginxConfiguration = Get-Content -LiteralPath (Join-Path $containerRoot 'nginx.conf') -Raw
foreach ($snippet in @(
        'server_tokens off;',
        'Content-Security-Policy',
        'Strict-Transport-Security',
        'X-Content-Type-Options',
        'Permissions-Policy',
        'Cross-Origin-Opener-Policy',
        'Cache-Control "no-cache, no-store, must-revalidate"',
        'Cache-Control "public, max-age=31536000, immutable"'))
{
    if (-not $nginxConfiguration.Contains($snippet, [StringComparison]::Ordinal))
    {
        throw "The UI edge configuration is missing '$snippet'."
    }
}

if ($blueTuskReferences -lt 20)
{
    throw "Expected broad BlueTusk package coverage; found only $blueTuskReferences references."
}

& (Join-Path $PSScriptRoot 'test-application-platform-health-verifier.ps1')

Write-Output (
    "Verified 20 application projects, $blueTuskReferences BlueTusk package references, " +
    "four browser clients, three hardened digest-pinned container definitions, " +
    "the package-only $expectedVersion boundary, and " +
    "$($verifiedLocalNpmPackages.Count) locally integrity-bound npm candidate(s).")
