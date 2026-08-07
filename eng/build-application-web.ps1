[CmdletBinding()]
param(
    [ValidateSet('All', 'OrderOperations', 'ServiceTopology', 'FraudInvestigation')]
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
foreach ($packageFile in $packageFiles.Values)
{
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $packageFile) -PathType Leaf))
    {
        throw "Locally packed npm artifact '$packageFile' was not found in '$packageRoot'."
    }
}

$applications = [ordered]@{
    OrderOperations = 'order-operations'
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
    foreach ($dependency in @($manifest.dependencies.PSObject.Properties))
    {
        if ($packageFiles.ContainsKey($dependency.Name))
        {
            $tarball = (Join-Path $packageRoot $packageFiles[$dependency.Name]).Replace('\', '/')
            $dependency.Value = "file:$tarball"
        }
    }
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    & npm --prefix $work install --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0)
    {
        throw "npm install failed for $($entry.Key)."
    }
    & npm --prefix $work run build
    if ($LASTEXITCODE -ne 0)
    {
        throw "Browser build failed for $($entry.Key)."
    }
}

Write-Output "Built $($applications.Count) browser application(s) against local $version npm artifacts."
