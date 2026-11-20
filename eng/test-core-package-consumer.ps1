[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packages = 'artifacts/v1.2-core-packages'
$feed = "$packages/packages"
$cache = 'artifacts/core-package-consumer-cache'
$result = 'artifacts/core-package-consumer'
$config = Join-Path $root 'artifacts/core-package-consumer.nuget.config'
$manifest = Join-Path $root "$packages/package-manifest.json"
$manifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
if ((& git -C $root rev-parse HEAD).Trim() -cne $ExpectedCommit -or
    @(& git -C $root status --porcelain --untracked-files=no).Count -ne 0)
{ throw 'Core package consumers require the clean exact candidate checkout.' }

[xml]$nuget = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'nuget/applications-candidate.config') -Raw
$source = @($nuget.configuration.packageSources.add | Where-Object key -ceq 'BlueTuskCandidate')
if ($source.Count -ne 1) { throw 'Expected the isolated BlueTusk candidate source mapping.' }
$source[0].SetAttribute('value', (Join-Path $root $feed))
$nuget.Save($config)

$projects = @(
    'applications/src/OrderOperations/BlueTusk.OrderOperations.Api/BlueTusk.OrderOperations.Api.csproj',
    'applications/src/OrderOperations/BlueTusk.OrderOperations.Worker/BlueTusk.OrderOperations.Worker.csproj',
    'applications/tests/BlueTusk.Applications.ArchitectureTests/BlueTusk.Applications.ArchitectureTests.csproj'
)
$oldVersion = $env:BlueTuskApplicationPackageVersion
$oldPackagesOnly = $env:BlueTuskCorePackagesOnly
try
{
    $env:BlueTuskApplicationPackageVersion = '1.2.0'
    $env:BlueTuskCorePackagesOnly = 'true'
    foreach ($project in $projects)
    {
        $path = Join-Path $root $project
        dotnet restore $path --configfile $config --packages (Join-Path $root $cache) --no-http-cache
        if ($LASTEXITCODE -ne 0) { throw "Candidate package restore failed for '$project'." }
        dotnet build $path --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Candidate package build failed for '$project'." }
    }

    $arguments = @{
        PackageCache = $cache; CandidateFeed = $feed; ExpectedCommit = $ExpectedCommit
        ExpectedVersion = '1.2.0'; CorePackages = $true
    }
    & (Join-Path $PSScriptRoot 'verify-application-candidate-restore.ps1') @arguments
    & (Join-Path $PSScriptRoot 'test-applications-postgresql.ps1') -ReleaseTrack Core -OutputRoot $result
    [ordered]@{
        schemaVersion = 1; scope = 'CorePackages'; releaseVersion = '1.2.0'; sourceCommit = $ExpectedCommit
        packageManifestSha256 = $manifestHash
        projects = $projects
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $root "$result/candidate.json") -Encoding utf8NoBOM
}
finally
{
    $env:BlueTuskApplicationPackageVersion = $oldVersion
    $env:BlueTuskCorePackagesOnly = $oldPackagesOnly
}
