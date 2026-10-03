[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][ValidatePattern('^postgres:[^@\s]+@sha256:[0-9a-f]{64}$')][string] $PostgreSqlImage,
    [Parameter(Mandatory)][string] $OutputPath,
    [string] $BenchmarkAssemblyPath = 'benchmarks/BlueTusk.Benchmarks/bin/Release/net10.0/BlueTusk.Benchmarks.dll',
    [string[]] $Features = @(),
    [ValidateRange(1, 256)][int[]] $Concurrency = @(1, 64, 256),
    [ValidateRange(1, 50)][int] $Trials = 10,
    [ValidateRange(0.1, 300)][double] $WarmupSeconds = 5,
    [ValidateRange(0.1, 300)][double] $MeasurementSeconds = 10,
    [Alias('MaximumSamples')][ValidateRange(1, 32000000)][int] $MaximumTotalSamples = 16000000,
    # Contract variant name (windows, linux, tls, constrained-network). It is resolved through
    # eng/performance-variant-map.json; an unresolved cross-OS variant fails closed.
    [ValidateSet('', 'windows', 'linux', 'tls', 'constrained-network')][string] $Variant = '',
    # Directory written by start-performance-fixture.ps1. Required for profiled (pipeline) captures.
    [string] $FixturePath = '',
    # Legacy switch: equivalent to -Variant tls without a fixture.
    [switch] $Tls,
    [switch] $Diagnostic
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedCommit)
{
    throw 'The requested source SHA does not match the checkout.'
}
$dirty = @(& git -C $root status --porcelain --untracked-files=normal).Count -ne 0
if ($dirty -and -not $Diagnostic)
{
    throw 'Non-diagnostic captures require a clean checkout, including untracked sources.'
}
$plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-evidence-plan.json') -Raw | ConvertFrom-Json
if (-not $Diagnostic -and ($Trials -lt $plan.statistics.minimumQualificationTrials -or
    $WarmupSeconds -lt $plan.timing.qualificationWarmupSeconds -or $MeasurementSeconds -lt $plan.timing.qualificationMeasurementSeconds))
{
    throw "Captures with fewer than $($plan.statistics.minimumQualificationTrials) trials or shortened windows must be explicitly marked -Diagnostic."
}
$contract = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-leadership-contract.json') -Raw | ConvertFrom-Json
if ($Features.Count -eq 0) { $Features = @($contract.workloads.Provider.features) }
$captureFeatures = @($contract.workloads.Provider.features) + @(
    'pooled-scalar', 'pooled-reused-scalar', 'multiplexed-scalar', 'multiplexed-reused-scalar')
if (@($Features | Select-Object -Unique).Count -ne $Features.Count -or
    @($Features | Where-Object { $_ -cnotin $captureFeatures }).Count -ne 0 -or
    @($Concurrency | Select-Object -Unique).Count -ne $Concurrency.Count)
{
    throw 'Features and concurrency must contain unique, recognized cases.'
}
$os = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { throw 'Only Windows and Linux runners are supported.' }

# Resolve the contract variant through the single data-driven table.
if ($Tls -and $Variant -and $Variant -cne 'tls') { throw '-Tls conflicts with the requested variant.' }
if (-not $Variant) { $Variant = if ($Tls) { 'tls' } else { $os } }
$map = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-variant-map.json') -Raw | ConvertFrom-Json
$profileName = [string]$map.variants.$os.$Variant
if ($profileName -ceq 'crossOsProfile') { $profileName = [string]$map.crossOsProfile }
if (-not $profileName -or $profileName -ceq 'unresolved')
{
    throw "Variant '$Variant' on a $os runner has no adopted meaning (see $($map.proposal)). It fails closed; same-OS data is never relabelled."
}
$captureProfile = $map.profiles.$profileName
if ($null -eq $captureProfile -or $os -cnotin @($captureProfile.hostOs)) { throw "Capture profile '$profileName' cannot run on a $os runner." }
$clientOs = switch ([string]$captureProfile.clientOs) { 'host' { $os } 'other' { if ($os -eq 'windows') { 'linux' } else { 'windows' } } default { [string]$captureProfile.clientOs } }
$requireTls = [bool]$captureProfile.tls
$containerClient = $clientOs -cne $os

$fixture = $null
$networkProfile = $null
$images = @($PostgreSqlImage)
$connectionVariable = 'BLUETUSK_BENCHMARK_CONNECTION_STRING'
$previousConnection = [Environment]::GetEnvironmentVariable($connectionVariable)
if ($FixturePath)
{
    $fixtureRoot = (Resolve-Path -LiteralPath $FixturePath).Path
    $fixture = Get-Content -LiteralPath (Join-Path $fixtureRoot 'fixture.json') -Raw | ConvertFrom-Json
    if ($fixture.profile -cne $profileName -or $fixture.postgreSqlImage -cne $PostgreSqlImage -or [bool]$fixture.tls -ne $requireTls)
    { throw "Fixture '$FixturePath' was started for another profile, image or transport." }
    $images = @($fixture.containerImageDigests)
    # max_connections preflight before any long capture. Mirrors ProviderRequestFixture: notification
    # delivery needs 2c+10, the four-slot contention probes 14, and every other feature c+10.
    $peak = ($Concurrency | Measure-Object -Maximum).Maximum
    $required = ($Features | ForEach-Object {
        if ($_ -in @('pooled-scalar', 'pooled-reused-scalar', 'multiplexed-scalar', 'multiplexed-reused-scalar')) { 14 }
        elseif ($_ -eq 'notification-delivery') { 2 * $peak + 10 }
        else { $peak + 10 } } | Measure-Object -Maximum).Maximum
    if ([int]$fixture.maxConnections -lt $required)
    { throw "Fixture max_connections=$($fixture.maxConnections) is below the $required this matrix needs." }
    $secret = if ($containerClient) { 'container-connection.secret' } else { 'connection.secret' }
    [Environment]::SetEnvironmentVariable($connectionVariable, [IO.File]::ReadAllText((Join-Path $fixtureRoot $secret)))
    if ($null -ne $captureProfile.networkProfile)
    {
        if ($fixture.networkProfile -cnotmatch ('^' + [regex]::Escape([string]$captureProfile.networkProfile) + '@sha256:[0-9a-f]{64}$'))
        { throw 'The fixture does not retain the network profile this variant requires.' }
        $networkProfile = [string]$fixture.networkProfile
    }
}
elseif ($null -ne $captureProfile.networkProfile -or $containerClient)
{
    throw "Variant '$Variant' needs a fixture from start-performance-fixture.ps1 (network shaping or a client container)."
}
if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($connectionVariable)))
{
    throw 'Set BLUETUSK_BENCHMARK_CONNECTION_STRING for the dedicated database, or pass -FixturePath; it is never written to evidence.'
}

$assemblyPath = (Resolve-Path -LiteralPath $BenchmarkAssemblyPath).Path
$assemblyHash = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$destination = [IO.Path]::GetFullPath($OutputPath, $root)
$allowedRoots = @([IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar)
if ($env:BLUETUSK_EVIDENCE_STORE)
{
    $allowedRoots += [IO.Path]::GetFullPath($env:BLUETUSK_EVIDENCE_STORE).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
}
if (@($allowedRoots | Where-Object { $destination.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0 -or
    (Test-Path -LiteralPath $destination))
{
    throw 'Output must be a new directory beneath repository artifacts or the configured evidence store.'
}
$null = New-Item -ItemType Directory -Path $destination
if ($null -ne $networkProfile)
{
    Copy-Item -LiteralPath (Join-Path $fixtureRoot 'network-profile.json') -Destination (Join-Path $destination 'network-profile.json')
    if ("$($captureProfile.networkProfile)@sha256:$((Get-FileHash -LiteralPath (Join-Path $destination 'network-profile.json') -Algorithm SHA256).Hash.ToLowerInvariant())" -cne $networkProfile)
    { throw 'The retained network profile changed after the fixture started.' }
}
$records = [Collections.Generic.List[object]]::new()
$startedUtc = [DateTimeOffset]::UtcNow.ToString('o')

function Test-Capture([string] $Path, [string] $Stem, [string] $Provider, [string] $Feature, [int] $Workers)
{
    # Light identity checks without materializing millions of samples in PowerShell.
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllBytes($Path))
    try
    {
        $capture = $document.RootElement
        $environment = $capture.GetProperty('environment')
        $method = $capture.GetProperty('method')
        $measurement = $capture.GetProperty('measurement')
        $shaping = $method.GetProperty('networkShaping').GetString()
        if ($capture.GetProperty('sourceCommit').GetString() -cne $ExpectedCommit -or
            $capture.GetProperty('provider').GetString() -cne $Provider -or
            $capture.GetProperty('feature').GetString() -cne $Feature -or
            $capture.GetProperty('concurrency').GetInt32() -ne $Workers -or
            $environment.GetProperty('os').GetString() -cne $clientOs -or
            $environment.GetProperty('tlsActive').GetBoolean() -ne $requireTls -or
            $shaping -cne $(if ($networkProfile) { $networkProfile } else { 'not configured by this adapter' }) -or
            $environment.GetProperty('referenceAssembly').GetString() -notmatch '^10\.0\.3(?:\+|$)' -or
            $measurement.GetProperty('completedOperations').GetInt64() -le 0 -or
            $measurement.GetProperty('workers').GetArrayLength() -ne $Workers)
        {
            throw "Capture identity, transport, network profile or coverage is invalid: $Stem."
        }
        if (-not $Diagnostic -and
            (-not $environment.GetProperty('harnessAssembly').GetString().Contains("+$ExpectedCommit") -or
             -not $environment.GetProperty('candidateAssembly').GetString().Contains("+$ExpectedCommit")))
        {
            throw "Measured assemblies were not built from $ExpectedCommit."
        }
        $count = 0L
        foreach ($worker in $measurement.GetProperty('workers').EnumerateArray())
        {
            $workerCount = $worker.GetProperty('count').GetInt64()
            if ($workerCount -le 0 -or $worker.GetProperty('requestTicks').GetArrayLength() -ne $workerCount)
            {
                throw "Raw request samples are incomplete: $Stem."
            }
            $count += $workerCount
        }
        if ($count -ne $measurement.GetProperty('completedOperations').GetInt64())
        {
            throw "Raw request sample count does not match completed operations: $Stem."
        }
        return $count
    }
    finally { $document.Dispose() }
}

try
{
    foreach ($workers in $Concurrency)
    {
        $perWorker = [Math]::Min(16000000, [Math]::Floor($MaximumTotalSamples / $workers))
        if ($perWorker -lt 1) { throw 'The sample budget must allow at least one sample per worker.' }
        foreach ($feature in $Features)
        {
            for ($trial = 0; $trial -lt $Trials; $trial++)
            {
                # Providers never run concurrently. Alternate process order across independent trials.
                $providers = if ($trial % 2 -eq 0) { @('bluetusk', 'npgsql') } else { @('npgsql', 'bluetusk') }
                foreach ($provider in $providers)
                {
                    $stem = "$feature-c$workers-trial$trial-$provider"
                    $reportPath = Join-Path $destination "$stem.json"
                    $optionsPath = Join-Path $destination "$stem.options.json"
                    $options = [ordered]@{
                        provider = $provider
                        feature = $feature
                        concurrency = $workers
                        warmupSeconds = $WarmupSeconds
                        measurementSeconds = $MeasurementSeconds
                        maximumSamplesPerWorker = [int]$perWorker
                        requireTls = $requireTls
                        sourceCommit = $ExpectedCommit
                        postgreSqlImage = $PostgreSqlImage
                        outputPath = $(if ($containerClient) { "/out/$stem.json" } else { $reportPath })
                        diagnostic = [bool]$Diagnostic
                    }
                    if ($networkProfile) { $options.networkProfile = $networkProfile }
                    $options | ConvertTo-Json | Set-Content -LiteralPath $optionsPath -Encoding utf8NoBOM
                    Write-Output "Capturing $feature, concurrency $workers, trial $($trial + 1)/$Trials, $provider ($Variant -> $profileName)."
                    if ($containerClient)
                    {
                        # Proposal profile: the same framework-dependent build runs in a digest-pinned Linux .NET runtime
                        # container on the fixture network. The connection string is inherited by name, never printed.
                        & docker run --rm --network $fixture.network --label "bluetusk.owner=$($fixture.owner)" --label "bluetusk.run=$($fixture.runId)" `
                            --env $connectionVariable --volume "$(Split-Path $assemblyPath -Parent):/bench:ro" --volume "${destination}:/out" `
                            $plan.fixture.linuxClientImage dotnet /bench/BlueTusk.Benchmarks.dll --provider-request-capture "/out/$stem.options.json"
                    }
                    else
                    {
                        & dotnet $assemblyPath --provider-request-capture $optionsPath
                    }
                    if ($LASTEXITCODE -ne 0)
                    {
                        throw "Capture failed: $stem. Partial artifacts are retained, not marked as completed evidence."
                    }
                    $count = Test-Capture $reportPath $stem $provider $feature $workers
                    $records.Add([ordered]@{
                        workloadKey = "$os|Provider|$feature|c=$workers|variant=$Variant"
                        trial = $trial
                        provider = $provider
                        path = "$stem.json"
                        sha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
                        completedOperations = $count
                    })
                }
            }
        }
    }
}
finally
{
    [Environment]::SetEnvironmentVariable($connectionVariable, $previousConnection)
}
if ((Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $assemblyHash)
{
    throw 'The benchmark assembly changed during capture.'
}
[ordered]@{
    schemaVersion = 1
    sourceCommit = $ExpectedCommit
    sourceDirty = $dirty
    diagnostic = [bool]$Diagnostic
    synthetic = $false
    startedUtc = $startedUtc
    capturedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    harnessSha256 = $assemblyHash
    os = $os
    variant = $Variant
    captureProfile = $profileName
    clientOs = $clientOs
    requireTls = $requireTls
    networkProfile = $networkProfile
    postgreSqlImage = $PostgreSqlImage
    containerImageDigests = @($images | Sort-Object -Unique -CaseSensitive)
    trials = $Trials
    warmupSeconds = $WarmupSeconds
    measurementSeconds = $MeasurementSeconds
    records = $records
    leadershipGatePassed = $false
    remainingVerification = 'Generate with --performance-evidence-generate, check independently with eng/PerformanceEvidenceChecker, assemble, then run verify-performance-leadership-evidence.ps1.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'capture-index.json') -Encoding utf8NoBOM
Write-Output "Retained $($records.Count) raw process captures in '$destination' ($Variant -> $profileName). This is capture evidence, not a leadership verdict."
