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
$definition = $manifest.families.$Family

if ($null -eq $definition)
{
    throw "Product family '$Family' is not registered."
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
        $manifest.families.$_.publishable -ne $true
    }
)

if ($definition.publishable -eq $true -and $blockedReleaseDependencies.Count -gt 0)
{
    throw "Product family '$Family' is marked publishable while release dependencies remain gated: $($blockedReleaseDependencies -join ', ')."
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

$projects = foreach ($entry in $definition.packages)
{
    $packageEntryPath = Join-Path $repositoryRoot $entry
    if (-not (Test-Path -LiteralPath $packageEntryPath))
    {
        throw "Product family '$Family' references missing package root '$entry'."
    }

    if ((Get-Item -LiteralPath $packageEntryPath) -is [System.IO.DirectoryInfo])
    {
        Get-ChildItem -LiteralPath $packageEntryPath -Filter '*.csproj' -Recurse -File
    }
    else
    {
        Get-Item -LiteralPath $packageEntryPath
    }
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

    Write-Output "Validated $Family release train with $($projects.Count) registered project(s) and $($npmPackages.Count) npm package(s); publishable=$($definition.publishable); releaseDependencies=$dependencySummary; blockedDependencies=$blockedSummary."
    return
}

if ($definition.publishable -ne $true -and -not $Candidate)
{
    throw "Product family '$Family' has not passed its publication gate."
}

if ($Candidate -and $definition.publishable -ne $true)
{
    Write-Warning "Packing gated $Family candidate artifacts for verification only. They must not be published."
}

if ($projects.Count -eq 0)
{
    throw "Product family '$Family' has no packages. Placeholder packages are not published."
}

$outputPath = Join-Path $repositoryRoot $Output
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
