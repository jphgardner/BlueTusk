[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][ValidatePattern('^postgres:[^@\s]+@sha256:[0-9a-f]{64}$')][string] $PostgreSqlImage,
    [Parameter(Mandatory)][string] $OutputPath,
    [string] $BenchmarkAssemblyPath = 'benchmarks/BlueTusk.Benchmarks/bin/Release/net10.0/BlueTusk.Benchmarks.dll',
    [string[]] $Features = @(),
    [ValidateRange(1, 256)][int[]] $Concurrency = @(1, 64, 256),
    [ValidateRange(1, 50)][int] $Trials = 5,
    [ValidateRange(0.1, 300)][double] $WarmupSeconds = 5,
    [ValidateRange(0.1, 300)][double] $MeasurementSeconds = 10,
    [Alias('MaximumSamples')][ValidateRange(1, 32000000)][int] $MaximumTotalSamples = 16000000,
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
if (-not $Diagnostic -and ($Trials -lt 5 -or $WarmupSeconds -lt 5 -or $MeasurementSeconds -lt 10))
{
    throw 'Shortened captures must be explicitly marked -Diagnostic.'
}
if ([string]::IsNullOrWhiteSpace($env:BLUETUSK_BENCHMARK_CONNECTION_STRING))
{
    throw 'Set BLUETUSK_BENCHMARK_CONNECTION_STRING for the dedicated database; it is never written to evidence.'
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
$assemblyPath = (Resolve-Path -LiteralPath $BenchmarkAssemblyPath).Path
$assemblyHash = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$destination = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $destination))
{
    throw 'Output must be a new directory beneath this repository artifacts directory.'
}
$null = New-Item -ItemType Directory -Path $destination
$os = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { throw 'Only Windows and Linux runners are supported.' }
$variant = if ($Tls) { 'tls' } else { $os }
$records = [Collections.Generic.List[object]]::new()

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
                [ordered]@{
                    provider = $provider
                    feature = $feature
                    concurrency = $workers
                    warmupSeconds = $WarmupSeconds
                    measurementSeconds = $MeasurementSeconds
                    maximumSamplesPerWorker = [int]$perWorker
                    requireTls = [bool]$Tls
                    sourceCommit = $ExpectedCommit
                    postgreSqlImage = $PostgreSqlImage
                    outputPath = $reportPath
                    diagnostic = [bool]$Diagnostic
                } | ConvertTo-Json | Set-Content -LiteralPath $optionsPath -Encoding utf8NoBOM
                Write-Output "Capturing $feature, concurrency $workers, trial $($trial + 1)/$Trials, $provider."
                & dotnet $assemblyPath --provider-request-capture $optionsPath
                if ($LASTEXITCODE -ne 0)
                {
                    throw "Capture failed: $stem. Partial artifacts are retained, not marked as completed evidence."
                }
                $capture = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
                if ($capture.sourceCommit -cne $ExpectedCommit -or $capture.provider -cne $provider -or
                    $capture.feature -cne $feature -or $capture.concurrency -ne $workers -or
                    $capture.environment.tlsActive -ne [bool]$Tls -or
                    $capture.environment.referenceAssembly -notmatch '^10\.0\.3(?:\+|$)' -or
                    $capture.measurement.completedOperations -le 0 -or
                    @($capture.measurement.workers).Count -ne $workers)
                {
                    throw "Capture identity or coverage is invalid: $stem."
                }
                if (-not $Diagnostic -and
                    (-not $capture.environment.harnessAssembly.Contains("+$ExpectedCommit") -or
                     -not $capture.environment.candidateAssembly.Contains("+$ExpectedCommit")))
                {
                    throw "Measured assemblies were not built from $ExpectedCommit."
                }
                $count = 0L
                foreach ($worker in $capture.measurement.workers)
                {
                    if ($worker.count -le 0 -or @($worker.requestTicks).Count -ne $worker.count -or
                        @($worker.requestTicks | Where-Object { $_ -le 0 }).Count -ne 0)
                    {
                        throw "Raw request samples are incomplete: $stem."
                    }
                    $count += $worker.count
                }
                if ($count -ne $capture.measurement.completedOperations)
                {
                    throw "Raw request sample count does not match completed operations: $stem."
                }
                $records.Add([ordered]@{
                    workloadKey = "$os|Provider|$feature|c=$workers|variant=$variant"
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
if ((Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $assemblyHash)
{
    throw 'The benchmark assembly changed during capture.'
}
[ordered]@{
    schemaVersion = 1
    sourceCommit = $ExpectedCommit
    sourceDirty = $dirty
    diagnostic = [bool]$Diagnostic
    capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    harnessSha256 = $assemblyHash
    os = $os
    variant = $variant
    postgresqlImage = $PostgreSqlImage
    trials = $Trials
    warmupSeconds = $WarmupSeconds
    measurementSeconds = $MeasurementSeconds
    records = $records
    leadershipGatePassed = $false
    remainingVerification = 'Image/runtime provenance, isolated-runner attestation, paired statistics/confidence intervals, and the rest of the full cross-product matrix remain mandatory.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'capture-index.json') -Encoding utf8NoBOM
Write-Output "Retained $($records.Count) raw process captures in '$destination'. This is capture evidence, not a leadership verdict."
