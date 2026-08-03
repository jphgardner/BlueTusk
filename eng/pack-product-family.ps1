[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')]
    [string] $Family,

    [string] $Configuration = 'Release',
    [string] $Output = 'artifacts/packages',
    [switch] $ValidateOnly,
    [switch] $Candidate,
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
$familyVersion = if ([string]::IsNullOrWhiteSpace($versionSuffix))
{
    $versionPrefix
}
else
{
    "$versionPrefix-$versionSuffix"
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
            if ($npmManifest.version -ne $familyVersion)
            {
                throw "npm package '$($npmManifest.name)' has version '$($npmManifest.version)', expected '$familyVersion'."
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

    Write-Output "Validated $Family release train with $($projects.Count) registered project(s) and $($npmPackages.Count) npm package(s); publicationEnabled=$publicationEnabled; channel=$publicationChannel; releaseDependencies=$dependencySummary; blockedDependencies=$blockedSummary; exactCommitWorkflows=$($requiredWorkflowEvidence.Count)."
    return
}

if (-not $publicationEnabled -and -not $Candidate)
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

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Packing '$($project.FullName)' failed with exit code $LASTEXITCODE."
    }
}

foreach ($package in $npmPackages)
{
    & npm pack $package.FullName --pack-destination $outputPath
    if ($LASTEXITCODE -ne 0)
    {
        throw "npm pack failed for '$($package.FullName)'."
    }
}
