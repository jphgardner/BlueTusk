[CmdletBinding()]
param(
    [string] $OutputPath = (
        Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/v1-performance'),

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit,

    [Parameter(Mandatory)]
    [ValidatePattern('^postgres:19[^@\s]+@sha256:[0-9a-f]{64}$')]
    [string] $PostgreSqlImage,

    [string] $ConnectionString = $env:BLUETUSK_BENCHMARK_CONNECTION_STRING,

    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$artifactsPrefix = $artifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $fullOutputPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase))
{
    throw "Performance output '$fullOutputPath' must be a child of '$artifactsRoot'."
}

$headCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
$ExpectedCommit = $ExpectedCommit.ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $headCommit -ne $ExpectedCommit)
{
    throw "Checked-out commit '$headCommit' does not match '$ExpectedCommit'."
}
if ([string]::IsNullOrWhiteSpace($ConnectionString))
{
    throw 'BLUETUSK_BENCHMARK_CONNECTION_STRING or -ConnectionString is required.'
}

if (Test-Path -LiteralPath $fullOutputPath)
{
    throw (
        "Performance output '$fullOutputPath' already exists. Preserve it as immutable " +
        'evidence or choose a new empty -OutputPath.')
}
New-Item -ItemType Directory -Path $fullOutputPath -Force | Out-Null

$previousArtifacts = $env:BLUETUSK_BENCHMARK_ARTIFACTS
$previousConnection = $env:BLUETUSK_BENCHMARK_CONNECTION_STRING
$env:BLUETUSK_BENCHMARK_ARTIFACTS = $fullOutputPath
$env:BLUETUSK_BENCHMARK_CONNECTION_STRING = $ConnectionString
$logPath = Join-Path $fullOutputPath 'benchmark.log'
$pairedReport = Join-Path $fullOutputPath 'multiplexing-paired-evidence.json'

try
{
    $runArguments = @(
        'run',
        '--project',
        'benchmarks/BlueTusk.Benchmarks/BlueTusk.Benchmarks.csproj',
        '--configuration',
        'Release'
    )
    if ($NoBuild)
    {
        $runArguments += '--no-build'
    }
    $runArguments += @(
        '--',
        '--job',
        'medium',
        '--inProcess',
        '--filter',
        '*'
    )

    & dotnet @runArguments 2>&1 | Tee-Object -LiteralPath $logPath
    if ($LASTEXITCODE -ne 0)
    {
        throw "BenchmarkDotNet exited with code $LASTEXITCODE."
    }

    $pairedArguments = @(
        'run',
        '--project',
        'benchmarks/BlueTusk.Benchmarks/BlueTusk.Benchmarks.csproj',
        '--configuration',
        'Release'
    )
    if ($NoBuild)
    {
        $pairedArguments += '--no-build'
    }
    $pairedArguments += @(
        '--',
        '--multiplexing-paired-evidence',
        $pairedReport
    )

    & dotnet @pairedArguments 2>&1 |
        Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0)
    {
        throw "Paired multiplexing evidence capture exited with code $LASTEXITCODE."
    }
}
finally
{
    $env:BLUETUSK_BENCHMARK_ARTIFACTS = $previousArtifacts
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = $previousConnection
}

$log = Get-Content -LiteralPath $logPath -Raw
$invalidLogPatterns = @(
    '// \* Exceptions \*',
    '// \* Build Error \*',
    'No benchmarks were found',
    'BuildResult: Failure'
)
foreach ($pattern in $invalidLogPatterns)
{
    if ($log -match [regex]::Escape($pattern))
    {
        throw "BenchmarkDotNet log contains a failure marker: '$pattern'."
    }
}

$resultsPath = Join-Path $fullOutputPath 'results'
& (Join-Path $PSScriptRoot 'verify-benchmark-coverage.ps1') `
    -BaselinePath $resultsPath
& (Join-Path $PSScriptRoot 'verify-allocation-budgets.ps1') `
    -BaselinePath $resultsPath
& (Join-Path $PSScriptRoot 'verify-latency-budgets.ps1') `
    -BaselinePath $resultsPath

$multiplexingReport = Join-Path $resultsPath (
    'BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json')
& (Join-Path $PSScriptRoot 'verify-multiplexing-performance.ps1') `
    -ReportPath $multiplexingReport `
    -PairedReportPath $pairedReport

$report = Get-Content -LiteralPath $multiplexingReport -Raw | ConvertFrom-Json
$reportHash = (
    Get-FileHash -LiteralPath $multiplexingReport -Algorithm SHA256
).Hash.ToLowerInvariant()
$pairedReportHash = (
    Get-FileHash -LiteralPath $pairedReport -Algorithm SHA256
).Hash.ToLowerInvariant()
$artifactRecords = @(
    Get-ChildItem -LiteralPath $fullOutputPath -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath(
                    $fullOutputPath,
                    $_.FullName).Replace('\', '/')
                sha256 = (
                    Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
                ).Hash.ToLowerInvariant()
                bytes = $_.Length
            }
        })
$digest = ([regex]::Match($PostgreSqlImage, '@(?<digest>sha256:[0-9a-f]{64})$')).
    Groups['digest'].Value
$evidence = [ordered]@{
    schemaVersion = 1
    sourceCommit = $ExpectedCommit
    capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    benchmark = [ordered]@{
        framework = "BenchmarkDotNet $($report.HostEnvironmentInfo.BenchmarkDotNetVersion)"
        job = 'MediumRun'
        toolchain = 'InProcessEmitToolchain'
        launchCount = 2
        warmupCount = 10
        iterationCount = 15
        providerLatencyMethod = 'median of five alternating-provider trials'
        pairedBlocksPerTrial = 101
        burstsPerBlock = 32
        operationsPerBurst = 64
    }
    environment = [ordered]@{
        os = [string]$report.HostEnvironmentInfo.OsVersion
        architecture = [string]$report.HostEnvironmentInfo.Architecture
        processor = [string]$report.HostEnvironmentInfo.ProcessorName
        dotnetSdk = [string]$report.HostEnvironmentInfo.DotNetCliVersion
        dotnetRuntime = (
            [regex]::Match(
                [string]$report.HostEnvironmentInfo.RuntimeVersion,
                '^\.NET (?<version>[0-9.]+)')).Groups['version'].Value
        postgresqlMajor = 19
        postgresqlImage = $PostgreSqlImage
        postgresqlImageDigest = "postgres@$digest"
        topology = 'Dedicated loopback PostgreSQL; four physical lanes for provider comparisons.'
    }
    command = (
        "dotnet run --project benchmarks/BlueTusk.Benchmarks/BlueTusk.Benchmarks.csproj " +
        "-c Release --no-build -- --job medium --inProcess --filter '*'")
    report = [ordered]@{
        path = 'results/BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json'
        sha256 = $reportHash
    }
    pairedReport = [ordered]@{
        path = 'multiplexing-paired-evidence.json'
        sha256 = $pairedReportHash
    }
    artifacts = $artifactRecords
    verification = [ordered]@{
        allocationBudgets = 'passed'
        latencyBudgets = 'passed'
        coverage = 'passed'
        multiplexingComparison = 'passed'
    }
}
$evidencePath = Join-Path $fullOutputPath 'multiplexing-evidence.json'
$evidence | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM

& (Join-Path $PSScriptRoot 'verify-multiplexing-performance.ps1') `
    -ReportPath $multiplexingReport `
    -PairedReportPath $pairedReport `
    -EvidencePath $evidencePath

Write-Output (
    "V1 performance gate passed for $ExpectedCommit. Evidence: '$fullOutputPath'.")
