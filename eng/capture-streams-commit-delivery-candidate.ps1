[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $OutputPath,
    [string] $DotNetPath = 'dotnet',
    [ValidateSet(1, 1000)][int] $Changes = 1,
    [ValidateRange(1, 10000)][int] $WarmupTransactions = 20,
    [ValidateRange(1, 16000)][int] $MeasurementTransactions = 1000,
    [ValidateRange(0, 49)][int] $Trial = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$dotnet = (Get-Command $DotNetPath -CommandType Application).Source
$output = [IO.Path]::GetFullPath($OutputPath)

function Assert-CandidateSource
{
    $commit = & git -C $repository rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -cne $ExpectedCommit)
    { throw 'The benchmark checkout does not match the expected candidate commit.' }
    $status = @(& git -C $repository status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0)
    { throw 'The benchmark checkout must have a clean tracked worktree.' }
}

function Wait-CaptureMarker([string] $Path)
{
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    while (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        if ($candidate.HasExited -or [DateTimeOffset]::UtcNow -ge $deadline)
        { throw "Streams candidate stopped before reaching '$Path'. See '$candidateError'." }
        Start-Sleep -Milliseconds 50
    }
}

function Write-Transactions([int] $Start, [int] $Count)
{
    # psql sends each statement as a separate autocommitted transaction.
    & {
        for ($index = $Start; $index -lt $Start + $Count; $index++)
        {
            $first = $index * [long]$Changes + 1
            $last = $first + $Changes - 1
            "INSERT INTO public.streams_capture SELECT generate_series($first, $last);"
        }
    } | & docker exec -i $postgres.container psql -X -q -v ON_ERROR_STOP=1 -U postgres -d bluetusk_compatibility
    if ($LASTEXITCODE -ne 0) { throw 'The independent PostgreSQL workload writer failed.' }
}

Assert-CandidateSource
if (Test-Path -LiteralPath $output) { throw "Capture output already exists at '$output'." }
$null = New-Item -ItemType Directory -Path $output
$benchmark = Join-Path $repository 'benchmarks/BlueTusk.Benchmarks/BlueTusk.Benchmarks.csproj'
$build = Join-Path $output 'build'
$buildLog = Join-Path $output 'build.log'
$tfm = '-p:BlueTuskRuntimeTargetFrameworks=net10.0'
& $dotnet build $benchmark --configuration Release --artifacts-path $build $tfm '--maxcpucount:2' --verbosity minimal *> $buildLog
if ($LASTEXITCODE -ne 0) { throw "Building the Streams benchmark failed. See '$buildLog'." }
$assembly = @(& $dotnet msbuild $benchmark $tfm -p:Configuration=Release -p:UseArtifactsOutput=true "-p:ArtifactsPath=$build" -getProperty:TargetPath)
if ($LASTEXITCODE -ne 0 -or $assembly.Count -ne 1 -or -not (Test-Path -LiteralPath $assembly[0] -PathType Leaf))
{ throw 'Cannot locate the isolated Streams benchmark assembly.' }

$performancePlan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-evidence-plan.json') -Raw | ConvertFrom-Json
$compatibility = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dotnet-compatibility.json') -Raw | ConvertFrom-Json
if ($compatibility.postgreSqlFixture.linuxImage -cne $performancePlan.fixture.postgreSqlImage)
{ throw 'The diagnostic PostgreSQL fixture image no longer matches the performance plan pin.' }
Import-Module (Join-Path $PSScriptRoot 'dotnet-compatibility-postgresql.psm1') -Force
$postgres = $null
$candidate = $null
$previousConnection = $env:BLUETUSK_BENCHMARK_CONNECTION_STRING
try
{
    $fixtureOptions = @{
        Contract = $compatibility.postgreSqlFixture
        WorkDirectory = (Join-Path $output 'postgresql')
        DownloadCache = (Join-Path $repository 'artifacts/dotnet-downloads')
        UseDocker = $true
    }
    $postgres = Start-DotNetCompatibilityPostgreSql @fixtureOptions
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = $postgres.connection
    $schema = 'CREATE TABLE public.streams_capture(id bigint PRIMARY KEY); CREATE PUBLICATION streams_capture FOR TABLE public.streams_capture WITH (publish = ''insert'');'
    & docker exec $postgres.container psql -X -q -v ON_ERROR_STOP=1 -U postgres -d bluetusk_compatibility -c $schema
    if ($LASTEXITCODE -ne 0) { throw 'Creating the diagnostic source publication failed.' }

    $ready = Join-Path $output 'ready'
    $measurementReady = Join-Path $output 'measurement-ready'
    $trialPath = Join-Path $output 'candidate.json'
    $optionsPath = Join-Path $output 'candidate-options.json'
    $candidateError = Join-Path $output 'candidate-error.log'
    [ordered]@{
        sourceCommit = $ExpectedCommit
        postgreSqlImage = $postgres.identity
        trial = $Trial
        changesPerTransaction = $Changes
        warmupTransactions = $WarmupTransactions
        measurementTransactions = $MeasurementTransactions
        publication = 'streams_capture'
        slot = 'streams_capture'
        readyPath = $ready
        measurementReadyPath = $measurementReady
        outputPath = $trialPath
    } | ConvertTo-Json | Set-Content -LiteralPath $optionsPath -Encoding utf8
    $start = @{
        FilePath = $dotnet
        ArgumentList = ('"{0}" --streams-commit-delivery-candidate "{1}"' -f $assembly[0], $optionsPath)
        WorkingDirectory = $repository
        PassThru = $true
        RedirectStandardOutput = (Join-Path $output 'candidate.log')
        RedirectStandardError = $candidateError
    }
    if ($IsWindows) { $start.WindowStyle = 'Hidden' }
    $candidate = Start-Process @start

    Wait-CaptureMarker $ready
    Write-Transactions 0 $WarmupTransactions
    Wait-CaptureMarker $measurementReady
    Write-Transactions $WarmupTransactions $MeasurementTransactions
    if (-not $candidate.WaitForExit(90000) -or $candidate.ExitCode -ne 0)
    { throw "The Streams candidate failed to finish its trial. See '$candidateError'." }

    Assert-CandidateSource
    $capture = Get-Content -LiteralPath $trialPath -Raw | ConvertFrom-Json
    if ($capture.sourceCommit -cne $ExpectedCommit -or $capture.role -cne 'candidate' -or
        -not $capture.diagnostic -or $capture.synthetic -or
        $capture.measurement.completedOperations -ne [long]$Changes * $MeasurementTransactions)
    { throw "The captured candidate trial did not match its requested workload at '$trialPath'." }
    "Captured $($capture.measurement.completedOperations) changes in '$trialPath'."
}
finally
{
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = $previousConnection
    if ($candidate)
    {
        if (-not $candidate.HasExited) { $candidate.Kill($true) }
        $candidate.Dispose()
    }
    if ($postgres) { Stop-DotNetCompatibilityPostgreSql -Fixture $postgres }
}
