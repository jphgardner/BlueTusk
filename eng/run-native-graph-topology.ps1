[CmdletBinding()]
param(
    [TimeSpan] $Duration = '00:02:00',
    [ValidateRange(10, 10000000)]
    [int] $MinimumEvaluations = 1000,
    [ValidateSet('Both', 'Canary', 'Endurance')]
    [string] $Phase = 'Both',
    [string] $DotNetPath = 'dotnet',
    [string] $OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# A starting PostgreSQL fixture may refuse connections until pg_isready succeeds.
$PSNativeCommandUseErrorActionPreference = $false
if ($Duration -le [TimeSpan]::Zero) { throw 'Duration must be positive.' }
$root = Split-Path $PSScriptRoot -Parent
$run = [Guid]::NewGuid().ToString('N')
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = "artifacts/native-topology-$run" }
$output = [IO.Path]::GetFullPath($OutputRoot, $root)
$artifacts = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (-not $output.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) {
    throw 'Choose a new directory under repository artifacts for the native evidence.'
}
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the graph candidate commit.' }
$status = @(& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Could not determine source cleanliness.' }
$image = (Get-Content -Raw (Join-Path $root 'eng/release-tracks.json') | ConvertFrom-Json).graph.portablePostgreSqlImage
if ($image -cnotmatch '@sha256:[a-f0-9]{64}$') { throw 'The PostgreSQL base image must be digest-pinned.' }

$null = New-Item -ItemType Directory -Path $output
$tag = "bluetusk-native-topology:$run"
& docker build --build-arg "POSTGRES_IMAGE=$image" --tag $tag (Join-Path $root 'extensions/bluetusk_graph')
if ($LASTEXITCODE -ne 0) { throw 'Could not build the pinned native PostgreSQL fixture.' }
$imageId = (& docker image inspect --format '{{.Id}}' $tag).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the native image.' }

$cases = [ordered]@{
    Canary = 'Service_topology_shadow_matches_application_and_excludes_other_tenants'
    Endurance = 'Native_topology_endurance_reconciles_authorized_live_replay'
}
$environment = @(
    'BLUETUSK_GRAPH_ENGINE_TEST_CONNECTION_STRING',
    'BLUETUSK_NATIVE_GRAPH_DURATION',
    'BLUETUSK_NATIVE_GRAPH_MIN_EVALUATIONS',
    'BLUETUSK_NATIVE_GRAPH_REPORT_PATH'
)
$previous = @{}
foreach ($name in $environment) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$results = [System.Collections.Generic.List[object]]::new()
$started = [DateTimeOffset]::UtcNow
Push-Location -LiteralPath $root
try {
    foreach ($entry in $cases.GetEnumerator()) {
        if ($Phase -ne 'Both' -and $Phase -ne $entry.Key) { continue }
        $caseName = $entry.Key.ToLowerInvariant()
        $container = (& docker run --detach --rm --name "bluetusk-native-$caseName-$run" --env POSTGRES_HOST_AUTH_METHOD=trust --env POSTGRES_DB=bluetusk_native_topology --publish '127.0.0.1::5432' $tag).Trim()
        if ($LASTEXITCODE -ne 0 -or $container -cnotmatch '^[a-f0-9]{64}$') {
            throw "Could not start the disposable $caseName fixture."
        }
        try {
            $ready = $false
            foreach ($attempt in 1..30) {
                & docker exec $container pg_isready -h 127.0.0.1 -U postgres -d bluetusk_native_topology *> $null
                if ($LASTEXITCODE -eq 0) { $ready = $true; break }
                Start-Sleep -Seconds 1
            }
            if (-not $ready) { throw "$caseName PostgreSQL fixture did not become ready." }
            $bound = (& docker port $container 5432/tcp).Trim()
            if ($LASTEXITCODE -ne 0 -or $bound -notmatch '^127\.0\.0\.1:(\d+)$') {
                throw 'The native fixture must use a loopback PostgreSQL port.'
            }

            $env:BLUETUSK_GRAPH_ENGINE_TEST_CONNECTION_STRING = "Host=127.0.0.1;Port=$($Matches[1]);Username=postgres;Database=bluetusk_native_topology;SSL Mode=Disable;Channel Binding=Disable"
            $env:BLUETUSK_NATIVE_GRAPH_DURATION = $Duration.ToString('c')
            $env:BLUETUSK_NATIVE_GRAPH_MIN_EVALUATIONS = $MinimumEvaluations.ToString([Globalization.CultureInfo]::InvariantCulture)
            $env:BLUETUSK_NATIVE_GRAPH_REPORT_PATH = Join-Path $output 'endurance.json'
            $trxName = "$caseName.trx"
            $testArguments = @(
                'test', 'tests/BlueTusk.ContinuousGraph.Tests/BlueTusk.ContinuousGraph.Tests.csproj',
                '--configuration', 'Release', '--framework', 'net10.0', '-m:2',
                '-p:BlueTuskRuntimeTargetFrameworks=net10.0', '-p:BlueTuskTopologyCanaryTests=true',
                '--filter', "FullyQualifiedName~$($entry.Value)",
                '--logger', "trx;LogFileName=$trxName", '--results-directory', $output
            )
            & $DotNetPath @testArguments
            if ($LASTEXITCODE -ne 0) { throw "$caseName native Topology validation failed." }

            $trxPath = Join-Path $output $trxName
            [xml] $trx = Get-Content -Raw -LiteralPath $trxPath
            $counters = $trx.TestRun.ResultSummary.Counters
            if ([int]$counters.total -ne 1 -or [int]$counters.passed -ne 1) {
                throw "$caseName must pass its one expected test."
            }
            $results.Add([ordered]@{
                phase = $entry.Key
                test = $entry.Value
                trx = $trxName
                trxSha256 = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash.ToLowerInvariant()
            })
        }
        finally {
            & docker logs --timestamps $container *> (Join-Path $output "$caseName.postgres.log")
            & docker stop $container *> $null
        }
    }
}
finally {
    Pop-Location
    foreach ($name in $environment) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    [ordered]@{
        schemaVersion = 1
        qualification = 'local-native-topology-only'
        sourceCommit = $commit
        sourceClean = $status.Count -eq 0
        postgresBaseImage = $image
        nativeImageId = $imageId
        startedAt = $started
        completedAt = [DateTimeOffset]::UtcNow
        completedCases = @($results.ToArray())
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $output 'report.json') -Encoding utf8NoBOM
}
Write-Output "Retained native Topology evidence at $output"
