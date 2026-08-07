[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')]
    [string] $Family,

    [string] $Configuration = 'Release',
    [string] $Output = 'artifacts/packages',
    [switch] $ValidateOnly,
    [switch] $Candidate,
    [switch] $Prerelease,
    [string] $VersionOverride,
    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Split-Path $PSScriptRoot -Parent)
$manifestPath = Join-Path $PSScriptRoot 'product-families.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2)
{
    throw "Expected product-family manifest schema 2; found '$($manifest.schemaVersion)'."
}

if ($Candidate -and $Prerelease)
{
    throw 'Candidate and Prerelease packaging modes are mutually exclusive.'
}

$prereleaseManifest = $null
if ($Prerelease)
{
    $prereleaseManifestPath = Join-Path $PSScriptRoot 'prerelease-train.json'
    $prereleaseManifest = Get-Content -LiteralPath $prereleaseManifestPath -Raw |
        ConvertFrom-Json
    if ([int]$prereleaseManifest.schemaVersion -ne 1 -or
        $prereleaseManifest.publicationEnabled -ne $true)
    {
        throw 'The prerelease train is not enabled with supported schema 1.'
    }

    if ([string]::IsNullOrWhiteSpace($VersionOverride) -or
        -not [string]::Equals(
            $VersionOverride,
            [string]$prereleaseManifest.version,
            [StringComparison]::Ordinal))
    {
        throw (
            "Prerelease packaging requires exact train version " +
            "'$($prereleaseManifest.version)'.")
    }

    if ($VersionOverride -notmatch '^\d+\.\d+\.\d+-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*$')
    {
        throw "Prerelease version '$VersionOverride' is not valid SemVer."
    }

    if ($Family -notin @($prereleaseManifest.families))
    {
        throw "Product family '$Family' is not armed for the prerelease train."
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($VersionOverride))
{
    throw 'VersionOverride is permitted only with Prerelease packaging.'
}

$definition = $manifest.families.$Family

if ($null -eq $definition)
{
    throw "Product family '$Family' is not registered."
}

$publication = $definition.publication
if ($null -eq $publication)
{
    throw "Product family '$Family' has no publication policy."
}

$publicationEnabled = $publication.enabled -eq $true
$publicationChannel = [string]$publication.channel
if ($publicationChannel -notin @('stable', 'preview'))
{
    throw "Product family '$Family' has unsupported publication channel '$publicationChannel'."
}

$tagPrefix = [string]$publication.tagPrefix
if ($tagPrefix -notmatch '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$')
{
    throw "Product family '$Family' has invalid release tag prefix '$tagPrefix'."
}

$duplicateTagPrefixes = @(
    $manifest.families.PSObject.Properties |
        Group-Object { [string]$_.Value.publication.tagPrefix } |
        Where-Object Count -gt 1
)
if ($duplicateTagPrefixes.Count -gt 0)
{
    throw "Product-family release tag prefixes must be unique."
}

$requiredWorkflowEvidence = @($publication.requiredWorkflowEvidence)
if ($requiredWorkflowEvidence.Count -eq 0)
{
    throw "Product family '$Family' has no required exact-commit workflow evidence."
}

$workflowFiles = @(
    $requiredWorkflowEvidence |
        ForEach-Object { [string]$_.workflowFile }
)
if (@($workflowFiles | Sort-Object -Unique).Count -ne $workflowFiles.Count)
{
    throw "Product family '$Family' has duplicate workflow evidence requirements."
}

foreach ($workflowEvidence in $requiredWorkflowEvidence)
{
    $workflowFile = [string]$workflowEvidence.workflowFile
    if ($workflowFile -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.(?:yml|yaml)$')
    {
        throw "Product family '$Family' has invalid workflow evidence '$workflowFile'."
    }

    $workflowPath = Join-Path $repositoryRoot ".github/workflows/$workflowFile"
    if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf))
    {
        throw "Product family '$Family' references missing workflow '$workflowFile'."
    }

    $allowedEvents = @($workflowEvidence.allowedEvents)
    if ($allowedEvents.Count -eq 0 -or
        @($allowedEvents | Sort-Object -Unique).Count -ne $allowedEvents.Count)
    {
        throw "Product family '$Family' has missing or duplicate allowed events for '$workflowFile'."
    }

    foreach ($allowedEvent in $allowedEvents)
    {
        if ([string]$allowedEvent -ne 'workflow_dispatch')
        {
            throw "Product family '$Family' permits unsupported evidence event '$allowedEvent'."
        }
    }
}

$releaseDependencies = @()
if ($null -ne $definition.PSObject.Properties['releaseDependencies'])
{
    $releaseDependencies = @($definition.releaseDependencies)
}

foreach ($releaseDependency in $releaseDependencies)
{
    if ($releaseDependency -eq $Family)
    {
        throw "Product family '$Family' cannot depend on itself for release."
    }

    if ($null -eq $manifest.families.PSObject.Properties[$releaseDependency])
    {
        throw "Product family '$Family' references unknown release dependency '$releaseDependency'."
    }
}

if (@($releaseDependencies | Sort-Object -Unique).Count -ne $releaseDependencies.Count)
{
    throw "Product family '$Family' declares duplicate release dependencies."
}

$blockedReleaseDependencies = @(
    $releaseDependencies | Where-Object {
        $manifest.families.$_.publication.enabled -ne $true
    }
)

if ($publicationEnabled -and $blockedReleaseDependencies.Count -gt 0)
{
    throw "Product family '$Family' has publication enabled while release dependencies remain gated: $($blockedReleaseDependencies -join ', ')."
}

$versionPath = Join-Path $repositoryRoot $definition.versionFile
if (-not (Test-Path -LiteralPath $versionPath))
{
    throw "Product family '$Family' references missing version file '$($definition.versionFile)'."
}

[xml] $versionDocument = Get-Content -LiteralPath $versionPath -Raw
$versionPrefix = [string] $versionDocument.Project.PropertyGroup.VersionPrefix
$versionSuffix = [string] $versionDocument.Project.PropertyGroup.VersionSuffix
$sourceFamilyVersion = if ([string]::IsNullOrWhiteSpace($versionSuffix))
{
    $versionPrefix
}
else
{
    "$versionPrefix-$versionSuffix"
}
$familyVersion = if ($Prerelease)
{
    $VersionOverride
}
else
{
    $sourceFamilyVersion
}
if ([string]::IsNullOrWhiteSpace($versionPrefix))
{
    throw "Product family '$Family' has no VersionPrefix in '$($definition.versionFile)'."
}

if ($publicationEnabled)
{
    $parsedVersion = $null
    if (-not [Version]::TryParse($versionPrefix, [ref]$parsedVersion))
    {
        throw "Product family '$Family' has invalid VersionPrefix '$versionPrefix'."
    }

    if ($publicationChannel -eq 'stable' -and
        ($parsedVersion.Major -lt 1 -or -not [string]::IsNullOrWhiteSpace($versionSuffix)))
    {
        throw "Stable publication for '$Family' requires a 1.0.0-or-newer version without a suffix."
    }

    if ($publicationChannel -eq 'preview' -and [string]::IsNullOrWhiteSpace($versionSuffix))
    {
        throw "Preview publication for '$Family' requires a prerelease version suffix."
    }
}

$packageEntries = @($definition.packages)
if (@($packageEntries | Sort-Object -Unique).Count -ne $packageEntries.Count)
{
    throw "Product family '$Family' has duplicate package project entries."
}

$projects = foreach ($entry in $packageEntries)
{
    $packageEntryPath = Join-Path $repositoryRoot $entry
    if (-not (Test-Path -LiteralPath $packageEntryPath))
    {
        throw "Product family '$Family' references missing package root '$entry'."
    }

    if ((Get-Item -LiteralPath $packageEntryPath) -is [System.IO.DirectoryInfo])
    {
        throw "Product family '$Family' must list package projects explicitly; '$entry' is a directory."
    }

    $project = Get-Item -LiteralPath $packageEntryPath
    if ($project.Extension -ne '.csproj')
    {
        throw "Product family '$Family' package entry '$entry' is not a project file."
    }

    $project
}

$projects = @($projects | Sort-Object FullName -Unique)
$projects = @(
    $projects | Where-Object {
        [xml] $projectDocument = Get-Content -LiteralPath $_.FullName -Raw
        $declaredFamily = @(
            $projectDocument.SelectNodes('//BlueTuskProductFamily') |
                ForEach-Object { $_.InnerText } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Select-Object -First 1
        )
        $projectFamily = if ($declaredFamily.Count -eq 0)
        {
            'Provider'
        }
        else
        {
            [string] $declaredFamily[0]
        }

        $projectFamily -eq $Family
    }
)
$npmPackages = @()
if ($null -ne $definition.PSObject.Properties['npmPackages'])
{
    $npmPackages = @(
        foreach ($entry in $definition.npmPackages)
        {
            $npmPackagePath = Join-Path $repositoryRoot $entry
            $manifestPath = Join-Path $npmPackagePath 'package.json'
            if (-not (Test-Path -LiteralPath $manifestPath))
            {
                throw "Product family '$Family' references missing npm package '$entry'."
            }

            $npmManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            if ($npmManifest.version -ne $sourceFamilyVersion)
            {
                throw "npm package '$($npmManifest.name)' has version '$($npmManifest.version)', expected source version '$sourceFamilyVersion'."
            }

            Get-Item -LiteralPath $npmPackagePath
        }
    )
}

if ($ValidateOnly)
{
    $dependencySummary = if ($releaseDependencies.Count -eq 0)
    {
        'none'
    }
    else
    {
        $releaseDependencies -join ','
    }
    $blockedSummary = if ($blockedReleaseDependencies.Count -eq 0)
    {
        'none'
    }
    else
    {
        $blockedReleaseDependencies -join ','
    }

    $mode = if ($Prerelease) { "prerelease/$familyVersion" } elseif ($Candidate) { 'candidate' } else { 'stable' }
    Write-Output "Validated $Family release train with $($projects.Count) registered project(s) and $($npmPackages.Count) npm package(s); mode=$mode; publicationEnabled=$publicationEnabled; channel=$publicationChannel; releaseDependencies=$dependencySummary; blockedDependencies=$blockedSummary; exactCommitWorkflows=$($requiredWorkflowEvidence.Count)."
    return
}

if (-not $publicationEnabled -and -not $Candidate -and -not $Prerelease)
{
    throw "Product family '$Family' has not passed its publication gate."
}

if ($Candidate -and -not $publicationEnabled)
{
    Write-Warning "Packing gated $Family candidate artifacts for verification only. They must not be published."
}

if ($projects.Count -eq 0)
{
    throw "Product family '$Family' has no packages. Placeholder packages are not published."
}

$outputPath = if ([System.IO.Path]::IsPathRooted($Output))
{
    [System.IO.Path]::GetFullPath($Output)
}
else
{
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Output))
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

if ($npmPackages.Count -gt 0)
{
    Push-Location $repositoryRoot
    try
    {
        & npm ci --ignore-scripts
        if ($LASTEXITCODE -ne 0)
        {
            throw "npm ci failed with exit code $LASTEXITCODE."
        }

        & npm audit --audit-level=high
        if ($LASTEXITCODE -ne 0)
        {
            throw "npm audit failed with exit code $LASTEXITCODE."
        }

        & npm run check:clients
        if ($LASTEXITCODE -ne 0)
        {
            throw "npm client build/test failed with exit code $LASTEXITCODE."
        }
    }
    finally
    {
        Pop-Location
    }
}

foreach ($project in $projects)
{
    $arguments = @(
        'pack',
        $project.FullName,
        '--configuration', $Configuration,
        '--output', $outputPath
    )
    if ($NoRestore)
    {
        $arguments += '--no-restore'
    }
    if ($Prerelease)
    {
        $arguments += "-p:Version=$familyVersion"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Packing '$($project.FullName)' failed with exit code $LASTEXITCODE."
    }
}

foreach ($package in $npmPackages)
{
    $packagePath = $package.FullName
    $temporaryPackagePath = $null
    if ($Prerelease)
    {
        $artifactsRoot = [IO.Path]::GetFullPath((
            Join-Path $repositoryRoot 'artifacts'))
        $temporaryRoot = [IO.Path]::GetFullPath((
            Join-Path $artifactsRoot "prerelease-work/$Family"))
        if (-not $temporaryRoot.StartsWith(
                $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase))
        {
            throw "Prerelease workspace '$temporaryRoot' must remain below artifacts."
        }
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        $temporaryPackagePath = Join-Path $temporaryRoot $package.Name
        if (Test-Path -LiteralPath $temporaryPackagePath)
        {
            Remove-Item -LiteralPath $temporaryPackagePath -Recurse -Force
        }
        Copy-Item -LiteralPath $package.FullName -Destination $temporaryPackagePath -Recurse

        $temporaryManifestPath = Join-Path $temporaryPackagePath 'package.json'
        $temporaryManifest = Get-Content -LiteralPath $temporaryManifestPath -Raw |
            ConvertFrom-Json
        $temporaryManifest.version = $familyVersion
        foreach ($dependencyProperty in @('dependencies', 'optionalDependencies'))
        {
            $dependencies = $temporaryManifest.PSObject.Properties[$dependencyProperty]
            if ($null -eq $dependencies)
            {
                continue
            }
            foreach ($dependency in $dependencies.Value.PSObject.Properties)
            {
                if ($dependency.Name.StartsWith(
                        '@bluetusk/',
                        [StringComparison]::Ordinal))
                {
                    $dependency.Value = $familyVersion
                }
            }
        }
        $temporaryManifest | ConvertTo-Json -Depth 32 |
            Set-Content -LiteralPath $temporaryManifestPath -Encoding utf8NoBOM
        $packagePath = $temporaryPackagePath
    }

    & npm pack $packagePath --pack-destination $outputPath
    $packExitCode = $LASTEXITCODE
    if ($null -ne $temporaryPackagePath -and
        (Test-Path -LiteralPath $temporaryPackagePath))
    {
        Remove-Item -LiteralPath $temporaryPackagePath -Recurse -Force
    }
    if ($packExitCode -ne 0)
    {
        throw "npm pack failed for '$($package.FullName)'."
    }
}
