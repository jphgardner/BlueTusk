[CmdletBinding()]
param(
    [ValidateSet(
        'All',
        'OrderOperations',
        'OrderOperationsAngular',
        'ServiceTopology',
        'FraudInvestigation')]
    [string] $Application = 'All',
    [string] $PackageDirectory = 'artifacts/prerelease/live',
    [string] $Output = 'artifacts/application-web'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Output))
$packageRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PackageDirectory))
if (-not $outputRoot.StartsWith(
        $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "Web build output '$outputRoot' must be beneath '$artifactsRoot'."
}

$version = [string](
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerelease-train.json') -Raw |
        ConvertFrom-Json).version
$packageFiles = @{
    '@bluetusk/live' = "bluetusk-live-$version.tgz"
    '@bluetusk/live-react' = "bluetusk-live-react-$version.tgz"
    '@bluetusk/live-angular' = "bluetusk-live-angular-$version.tgz"
}
$candidatePackages = @{}
foreach ($name in $packageFiles.Keys)
{
    $tarball = Join-Path $packageRoot $packageFiles[$name]
    if (-not (Test-Path -LiteralPath $tarball -PathType Leaf))
    {
        throw "Locally packed npm artifact '$tarball' was not found."
    }
    $hash = (Get-FileHash -LiteralPath $tarball -Algorithm SHA512).Hash
    $candidatePackages[$name] = @{
        path = $tarball
        integrity = 'sha512-' + [Convert]::ToBase64String([Convert]::FromHexString($hash))
    }
}

$applications = [ordered]@{
    OrderOperations = 'order-operations'
    OrderOperationsAngular = 'order-operations-angular'
    ServiceTopology = 'service-topology'
    FraudInvestigation = 'fraud-investigation'
}
if ($Application -ne 'All')
{
    $applications = [ordered]@{ $Application = $applications[$Application] }
}

foreach ($entry in $applications.GetEnumerator())
{
    $source = Join-Path $repositoryRoot "applications/web/$($entry.Value)"
    $work = Join-Path $outputRoot "$($entry.Value)/work"
    if (Test-Path -LiteralPath $work)
    {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $work -Force
    Copy-Item -Path (Join-Path $source '*') -Destination $work -Recurse -Force

    $manifestPath = Join-Path $work 'package.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $localDependencies = @($manifest.dependencies.PSObject.Properties | Where-Object { $candidatePackages.ContainsKey($_.Name) })
    foreach ($dependency in $localDependencies)
    {
        $relativeTarball = [IO.Path]::GetRelativePath($work, $candidatePackages[$dependency.Name].path).Replace('\', '/')
        $dependency.Value = "file:$relativeTarball"
    }
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    & npm --prefix $work install --package-lock-only --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0)
    {
        throw "Generating the candidate npm lock failed for $($entry.Key)."
    }
    $lock = Get-Content -LiteralPath (Join-Path $work 'package-lock.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($dependency in $localDependencies)
    {
        $package = $lock.packages["node_modules/$($dependency.Name)"]
        if ($package.version -cne $version -or $package.resolved -cne $dependency.Value -or
            $package.integrity -cne $candidatePackages[$dependency.Name].integrity)
        { throw "The candidate lock for $($entry.Key) does not match the local $($dependency.Name) tarball." }
    }

    & npm --prefix $work ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0)
    {
        throw "Installing from the candidate npm lock failed for $($entry.Key)."
    }
    & npm --prefix $work run build
    if ($LASTEXITCODE -ne 0)
    {
        throw "Browser build failed for $($entry.Key)."
    }
}

Write-Output "Built $($applications.Count) browser application(s) against local $version npm artifacts."
