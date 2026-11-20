[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][ValidateSet('windows', 'linux')][string] $Platform,
    [Parameter(Mandatory)][string] $ServerEnvironment,
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($env:BLUETUSK_BENCHMARK_CONNECTION_STRING)) {
    throw 'Configure the dedicated PostgreSQL fixture connection before running the matrix.'
}
$commit = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -or $commit -cnotmatch '^[0-9a-f]{40}$' -or
    @(& git -C $repository status --porcelain).Count) {
    throw 'Portable Graph qualification requires a clean candidate checkout.'
}
$capture = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $capture) {
    throw 'Use a fresh output directory to preserve previous qualification evidence.'
}
$null = New-Item -ItemType Directory -Path $capture
$tracks = Get-Content -LiteralPath "$PSScriptRoot/release-tracks.json" -Raw | ConvertFrom-Json
[ordered]@{
    sourceCommit = $commit
    os = $Platform
    architecture = 'x64'
    containerImageDigests = @($tracks.graph.portablePostgreSqlImage)
    serverEnvironment = $ServerEnvironment
    authorization = 'restricted PostgreSQL login, active RLS, exclusive authorized lease'
    job = 'MediumRun'
    warmupCount = 10
    iterationCount = 15
    outliers = 'DontRemove'
} | ConvertTo-Json | Set-Content -LiteralPath "$capture/environment.json"
Push-Location $repository
try {
    if (!$NoBuild) {
        dotnet build benchmarks/BlueTusk.Benchmarks -c Release -p:RestoreLockedMode=true -p:ContinuousIntegrationBuild=true -m:2 *> "$capture/build.log"
        if ($LASTEXITCODE) { throw 'Graph benchmark build failed.' }
    }
    dotnet --info *> "$capture/dotnet-info.txt"
    $previousPlans = $env:BLUETUSK_GRAPH_PLAN_DIRECTORY
    try {
        $env:BLUETUSK_GRAPH_PLAN_DIRECTORY = "$capture/plans"
        dotnet benchmarks/BlueTusk.Benchmarks/bin/Release/net10.0/BlueTusk.Benchmarks.dll --job medium --inProcess --outliers DontRemove --filter '*ContinuousGraphBenchmarks*' --artifacts "$capture/benchmarks" *> "$capture/matrix.log"
        if ($LASTEXITCODE) { throw 'Graph benchmark process failed.' }
    }
    finally {
        $env:BLUETUSK_GRAPH_PLAN_DIRECTORY = $previousPlans
    }
    & "$PSScriptRoot/verify-portable-graph-benchmarks.ps1" -CapturePath $capture -ExpectedCommit $commit -ExpectedOs $Platform *> "$capture/gate.log"
}
finally {
    Pop-Location
}
