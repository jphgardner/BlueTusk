[CmdletBinding()]
param(
    [string] $OutputPath = (
        Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/v1-performance'),

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit,

    [Parameter(Mandatory)]
    [ValidatePattern('^postgres:(?:15|16|17|18|19)[^@\s]+@sha256:[0-9a-f]{64}$')]
    [string] $PostgreSqlImage,

    [ValidateSet('Legacy', 'Core')]
    [string] $ReleaseTrack = 'Legacy',

    [string] $ConnectionString = $env:BLUETUSK_BENCHMARK_CONNECTION_STRING,

    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$previewFixtures = @('SqlPgqBenchmarks', 'ContinuousGraphBenchmarks')
if ($ReleaseTrack -eq 'Core')
{
    $coreContract = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'v1.2-candidate-readiness.json') -Raw | ConvertFrom-Json
    if ($PostgreSqlImage -cne [string]$coreContract.endurancePostgreSqlImage)
    { throw 'Core performance capture requires the configured stable PostgreSQL image.' }
}
elseif ($PostgreSqlImage -notmatch '^postgres:19')
{ throw 'Legacy performance capture requires the historical PostgreSQL 19 fixture.' }
$sourceStatus = @(& git -C $repositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $sourceStatus.Count -ne 0)
{ throw 'Performance capture requires a clean tracked candidate.' }
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
$providerPairedReport = Join-Path $fullOutputPath 'provider-paired-evidence.json'

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
    $benchmarkFilters = if ($ReleaseTrack -eq 'Core')
    {
        @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'benchmarks/BlueTusk.Benchmarks') -Filter '*Benchmarks.cs' |
            Where-Object BaseName -notin $previewFixtures |
            ForEach-Object { "BlueTusk.Benchmarks.$($_.BaseName).*" })
    }
    else { @('*') }
    $runArguments += @(
        '--',
        '--job',
        'medium',
        '--inProcess',
        '--filter'
    ) + $benchmarkFilters

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

    $providerPairedArguments = @(
        'run',
        '--project',
        'benchmarks/BlueTusk.Benchmarks/BlueTusk.Benchmarks.csproj',
        '--configuration',
        'Release'
    )
    if ($NoBuild)
    {
        $providerPairedArguments += '--no-build'
    }
    $providerPairedArguments += @(
        '--',
        '--provider-extended-paired-evidence',
        $providerPairedReport
    )

    # Capture provider-relative latency before the long BenchmarkDotNet suite so
    # its thread-pool, database and thermal state cannot contaminate paired tails.
    & dotnet @providerPairedArguments 2>&1 | Tee-Object -LiteralPath $logPath
    if ($LASTEXITCODE -ne 0)
    {
        throw "Paired provider evidence capture exited with code $LASTEXITCODE."
    }

    & dotnet @pairedArguments 2>&1 | Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0)
    {
        throw "Paired multiplexing evidence capture exited with code $LASTEXITCODE."
    }

    & dotnet @runArguments 2>&1 | Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0)
    {
        throw "BenchmarkDotNet exited with code $LASTEXITCODE."
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
$coverageArguments = @{ BaselinePath = $resultsPath }
$allocationArguments = @{ BaselinePath = $resultsPath }
$latencyArguments = @{ BaselinePath = $resultsPath }
if ($ReleaseTrack -eq 'Core')
{
    # Keep every stable fixture and its existing ceilings; only the separately
    # qualified SQL/PGQ preview fixtures are outside the Core release track.
    $scopedSource = Join-Path $fullOutputPath 'core-fixtures'
    [IO.Directory]::CreateDirectory($scopedSource) | Out-Null
    $coreFixtures = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'benchmarks/BlueTusk.Benchmarks') -Filter '*Benchmarks.cs' |
        Where-Object BaseName -notin $previewFixtures)
    $coreFixtureNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($fixture in $coreFixtures)
    {
        $source = Get-Content -LiteralPath $fixture.FullName -Raw
        $classMatch = [regex]::Match($source,
            '(?m)^public\s+(?:(?:sealed|partial)\s+)*class\s+(?<class>[A-Za-z_][A-Za-z0-9_]*Benchmarks)\b')
        if (-not $classMatch.Success) { throw "Could not find a public benchmark fixture in '$($fixture.FullName)'." }
        $null = $coreFixtureNames.Add($classMatch.Groups['class'].Value)
        Copy-Item -LiteralPath $fixture.FullName -Destination $scopedSource
    }
    $coverageArguments.BenchmarkSourcePath = $scopedSource
    # Partial fixture declarations share one class and one measured report.
    $coverageArguments.MinimumFixtureCount = $coreFixtureNames.Count
    $coverageArguments.MinimumBenchmarkCount = (@($coreFixtures | ForEach-Object { [regex]::Matches((Get-Content -LiteralPath $_.FullName -Raw), '\[Benchmark(?:\([^\]]*\))?\]').Count }) | Measure-Object -Sum).Sum
    foreach ($budgetKind in @('allocation', 'latency'))
    {
        $budget = Get-Content -LiteralPath (Join-Path $repositoryRoot "benchmarks/$budgetKind-budgets.json") -Raw | ConvertFrom-Json
        $budget.budgets = @($budget.budgets | Where-Object {
            [string]$_.benchmark -notmatch '^BlueTusk\.Benchmarks\.(?:SqlPgqBenchmarks|ContinuousGraphBenchmarks)\.'
        })
        $budgetPath = Join-Path $fullOutputPath "core-$budgetKind-budgets.json"
        $budget | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $budgetPath -Encoding utf8NoBOM
        if ($budgetKind -eq 'allocation') { $allocationArguments.BudgetFile = $budgetPath }
        else { $latencyArguments.BudgetFile = $budgetPath }
    }
}
& (Join-Path $PSScriptRoot 'verify-benchmark-coverage.ps1') `
    @coverageArguments
& (Join-Path $PSScriptRoot 'verify-allocation-budgets.ps1') `
    @allocationArguments
& (Join-Path $PSScriptRoot 'verify-latency-budgets.ps1') `
    @latencyArguments

$multiplexingReport = Join-Path $resultsPath (
    'BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json')
$providerReport = Join-Path $resultsPath (
    'BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-brief.json')
& (Join-Path $PSScriptRoot 'verify-provider-performance.ps1') `
    -ReportPath $providerReport `
    -PairedReportPath $providerPairedReport
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
$providerReportHash = (
    Get-FileHash -LiteralPath $providerReport -Algorithm SHA256
).Hash.ToLowerInvariant()
$providerPairedReportHash = (
    Get-FileHash -LiteralPath $providerPairedReport -Algorithm SHA256
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
    schemaVersion = 2
    releaseTrack = $ReleaseTrack
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
        providerPairedBlocksPerTrial = 501
        pairedBlocksPerTrial = 501
        burstsPerBlock = 4
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
        postgresqlMajor = [int]([regex]::Match($PostgreSqlImage, '^postgres:(\d+)').Groups[1].Value)
        postgresqlImage = $PostgreSqlImage
        postgresqlImageDigest = "postgres@$digest"
        topology = 'Dedicated loopback PostgreSQL; four physical lanes for provider comparisons.'
    }
    command = (
        "dotnet run --project benchmarks/BlueTusk.Benchmarks/BlueTusk.Benchmarks.csproj " +
        "-c Release --no-build -- --job medium --inProcess --filter " + ($benchmarkFilters -join ' '))
    report = [ordered]@{
        path = 'results/BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json'
        sha256 = $reportHash
    }
    pairedReport = [ordered]@{
        path = 'multiplexing-paired-evidence.json'
        sha256 = $pairedReportHash
    }
    providerReport = [ordered]@{
        path = 'results/BlueTusk.Benchmarks.ProviderComparisonBenchmarks-report-brief.json'
        sha256 = $providerReportHash
    }
    providerPairedReport = [ordered]@{
        path = 'provider-paired-evidence.json'
        sha256 = $providerPairedReportHash
    }
    artifacts = $artifactRecords
    verification = [ordered]@{
        allocationBudgets = 'passed'
        latencyBudgets = 'passed'
        coverage = 'passed'
        providerComparison = 'passed'
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
    "$ReleaseTrack reference performance gate passed for $ExpectedCommit. Evidence: '$fullOutputPath'.")
