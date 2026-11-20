[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $OutputPath,
    [string] $DotNetPath = 'dotnet',
    [string] $BenchmarkAssemblyPath,
    [string] $S3FixtureImage,
    [ValidateSet(1, 100, 1000)][int] $Changes = 1,
    [ValidateSet('commit-to-delivery', 'catch-up', 'snapshot', 'spill')][string] $Scenario = 'commit-to-delivery',
    [ValidateSet('count', 'postgresql', 'redis', 'nats', 'opensearch', 'kafka', 's3', 'webhooks')][string] $Sink = 'count',
    [ValidateRange(0, 10000)][int] $WarmupTransactions = 20,
    [ValidateRange(1, 16000)][int] $MeasurementTransactions = 1000,
    [ValidateRange(0, 49)][int] $Trial = 0,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{2,40}$')][string] $RunId = ('streams-cand-' + [guid]::NewGuid().ToString('N').Substring(0, 12)),
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{1,63}$')][string] $Owner = 'streams-candidate-probe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$dotnet = (Get-Command $DotNetPath -CommandType Application -TotalCount 1).Source
$output = [IO.Path]::GetFullPath($OutputPath)
$fixturePath = "artifacts/performance-fixtures/$RunId"
$fixtureDirectory = Join-Path $repository $fixturePath
. (Join-Path $PSScriptRoot 'streams-performance/streams-fixture.ps1')
. (Join-Path $PSScriptRoot 'streams-performance/redis-fixture.ps1')
. (Join-Path $PSScriptRoot 'streams-performance/nats-fixture.ps1')
. (Join-Path $PSScriptRoot 'streams-performance/opensearch-fixture.ps1')
. (Join-Path $PSScriptRoot 'streams-performance/kafka-fixture.ps1')
. (Join-Path $PSScriptRoot 'streams-performance/s3-fixture.ps1')
. (Join-Path $PSScriptRoot 'streams-performance/webhook-fixture.ps1')
$spill = $Scenario -ceq 'spill'
$sync = $Sink -cne 'count'
if (($sync -and $Scenario -cne 'commit-to-delivery') -or (-not $sync -and $Changes -eq 100))
{ throw 'Sync sinks support live delivery and 1, 100 or 1000 mutations.' }
$suffix = if ($sync) { "destination=$Sink" } else { Get-StreamsWorkloadSuffix $Scenario }
$liveDelivery = $Scenario -ceq 'commit-to-delivery' -or $spill
if ($spill -and -not $PSBoundParameters.ContainsKey('WarmupTransactions')) { $WarmupTransactions = 2 }
if ($spill -and -not $PSBoundParameters.ContainsKey('MeasurementTransactions')) { $MeasurementTransactions = 8 }
if (-not $liveDelivery -and -not $PSBoundParameters.ContainsKey('WarmupTransactions')) { $WarmupTransactions = 0 }
if ($liveDelivery -eq ($WarmupTransactions -eq 0) -or ($spill -and $WarmupTransactions + $MeasurementTransactions -gt 32))
{ throw 'Live delivery needs warmup. A spill capture supports up to 32 total transactions.' }

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

Assert-CandidateSource
if (Test-Path -LiteralPath $output) { throw "Capture output already exists at '$output'." }
$null = New-Item -ItemType Directory -Path $output
if ($BenchmarkAssemblyPath)
{
    $assembly = (Resolve-Path -LiteralPath $BenchmarkAssemblyPath).Path
}
else
{
    $benchmark = Join-Path $repository 'benchmarks/BlueTusk.Benchmarks/BlueTusk.Benchmarks.csproj'
    $build = Join-Path $output 'build'
    $buildLog = Join-Path $output 'build.log'
    $tfm = '-p:BlueTuskRuntimeTargetFrameworks=net10.0'
    & $dotnet build $benchmark --configuration Release --artifacts-path $build $tfm '--maxcpucount:2' --verbosity minimal *> $buildLog
    if ($LASTEXITCODE -ne 0) { throw "Building the Streams benchmark failed. See '$buildLog'." }
    $target = @(& $dotnet msbuild $benchmark $tfm -p:Configuration=Release -p:UseArtifactsOutput=true "-p:ArtifactsPath=$build" -getProperty:TargetPath)
    if ($LASTEXITCODE -ne 0 -or $target.Count -ne 1 -or -not (Test-Path -LiteralPath $target[0] -PathType Leaf))
    { throw 'Cannot locate the isolated Streams benchmark assembly.' }
    $assembly = $target[0]
}

$candidate = $null
$previousConnection = $env:BLUETUSK_BENCHMARK_CONNECTION_STRING
$previousRedisConnection = $env:BLUETUSK_BENCHMARK_REDIS_ENDPOINT
$previousNatsConnection = $env:BLUETUSK_BENCHMARK_NATS_ENDPOINT
$previousOpenSearchConnection = $env:BLUETUSK_BENCHMARK_OPENSEARCH_ENDPOINT
$previousKafkaConnection = $env:BLUETUSK_BENCHMARK_KAFKA_ENDPOINT
$previousS3Environment = if ($Sink -ceq 's3') { Get-SyncS3Environment }
$previousWebhookEnvironment = if ($Sink -ceq 'webhooks') { Get-SyncWebhookEnvironment }
try
{
    & (Join-Path $PSScriptRoot 'start-performance-fixture.ps1') -RunId $RunId -Owner $Owner -CaptureProfile native `
        -OutputPath $fixturePath -SourceCommit $ExpectedCommit -LogicalReplication
    $fixture = Get-Content -LiteralPath (Join-Path $fixtureDirectory 'fixture.json') -Raw | ConvertFrom-Json
    $postgres = $fixture.containers[0].name
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = [IO.File]::ReadAllText((Join-Path $fixtureDirectory 'connection.secret'))
    New-StreamsCaptureTable $postgres $spill
    if ($Sink -ceq 'postgresql') { New-SyncPostgreSqlFixture $postgres $false }
    $destinationFixture = $null
    if ($Sink -cin @('redis', 'nats', 'opensearch', 'kafka', 's3', 'webhooks'))
    {
        $plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-evidence-plan.json') -Raw | ConvertFrom-Json
        switch -CaseSensitive ($Sink)
        {
            'redis' {
                $destinationFixture = New-SyncRedisFixture $fixture.network $RunId $Owner $ExpectedCommit $plan.families.Sync.destinationImages.redis
                $env:BLUETUSK_BENCHMARK_REDIS_ENDPOINT = $destinationFixture.Endpoint
            }
            'nats' {
                $destinationFixture = New-SyncNatsFixture $fixture.network $RunId $Owner $ExpectedCommit $plan.families.Sync.destinationImages.nats $fixtureDirectory
                $env:BLUETUSK_BENCHMARK_NATS_ENDPOINT = $destinationFixture.Endpoint
            }
            'opensearch' {
                $destinationFixture = New-SyncOpenSearchFixture $fixture.network $RunId $Owner $ExpectedCommit $plan.families.Sync.destinationImages.opensearch
                $env:BLUETUSK_BENCHMARK_OPENSEARCH_ENDPOINT = $destinationFixture.Endpoint
            }
            'kafka' {
                $destinationFixture = New-SyncKafkaFixture $fixture.network $RunId $Owner $ExpectedCommit $plan.families.Sync.destinationImages.kafka
                $env:BLUETUSK_BENCHMARK_KAFKA_ENDPOINT = $destinationFixture.Endpoint
            }
            's3' {
                $destinationFixture = New-SyncS3Fixture $fixture.network $RunId $Owner $ExpectedCommit $S3FixtureImage $dotnet $assembly
            }
            'webhooks' {
                $destinationFixture = New-SyncWebhookFixture $fixture.network $RunId $Owner $ExpectedCommit $plan.families.Sync.referenceImage
            }
        }
    }
    if ($Scenario -ceq 'catch-up')
    {
        $null = & docker exec $postgres psql -X -q -v ON_ERROR_STOP=1 -U postgres -d bluetusk_benchmark `
            -c "SELECT pg_create_logical_replication_slot('streams_capture', 'pgoutput')"
        if ($LASTEXITCODE -ne 0) { throw 'Creating the backlog replication slot failed.' }
    }
    if (-not $liveDelivery) { Write-StreamsTransactions $postgres $Changes 0 $MeasurementTransactions $spill }

    $ready = Join-Path $output 'ready'
    $measurementReady = Join-Path $output 'measurement-ready'
    $trialPath = Join-Path $fixtureDirectory 'candidate.json'
    $optionsPath = Join-Path $output 'candidate-options.json'
    $candidateError = Join-Path $output 'candidate-error.log'
    [ordered]@{
        sourceCommit = $ExpectedCommit
        postgreSqlImage = $fixture.postgreSqlImage
        scenario = $Scenario
        sink = $Sink
        trial = $Trial
        changesPerTransaction = $Changes
        warmupTransactions = $WarmupTransactions
        measurementTransactions = $MeasurementTransactions
        publication = 'streams_capture'
        slot = 'streams_capture'
        spoolDirectory = if ($spill) { Join-Path $fixtureDirectory 'spool' } else { $null }
        readyPath = $ready
        measurementReadyPath = $measurementReady
        outputPath = $trialPath
    } | ConvertTo-Json | Set-Content -LiteralPath $optionsPath -Encoding utf8
    $start = @{
        FilePath = $dotnet
        ArgumentList = ('"{0}" --streams-delivery-candidate "{1}"' -f $assembly, $optionsPath)
        WorkingDirectory = $repository
        PassThru = $true
        RedirectStandardOutput = (Join-Path $output 'candidate.log')
        RedirectStandardError = $candidateError
    }
    if ($IsWindows) { $start.WindowStyle = 'Hidden' }
    $candidate = Start-Process @start

    if ($liveDelivery)
    {
        Wait-CaptureMarker $ready
        Write-StreamsTransactions $postgres $Changes 0 $WarmupTransactions $spill
        Wait-CaptureMarker $measurementReady
        Write-StreamsTransactions $postgres $Changes $WarmupTransactions $MeasurementTransactions $spill
    }
    if (-not $candidate.WaitForExit(90000) -or $candidate.ExitCode -ne 0)
    { throw "The Streams candidate failed to finish its trial. See '$candidateError'." }

    Assert-CandidateSource
    $capture = Get-Content -LiteralPath $trialPath -Raw | ConvertFrom-Json
    if ($capture.sourceCommit -cne $ExpectedCommit -or $capture.role -cne 'candidate' -or
        -not $capture.workloadKey.EndsWith("|$suffix", [StringComparison]::Ordinal) -or
        -not $capture.diagnostic -or $capture.synthetic -or
        $capture.measurement.completedOperations -ne [long]$Changes * $MeasurementTransactions)
    { throw "The captured candidate trial did not match its requested workload at '$trialPath'." }
    if ($spill)
    {
        Assert-StreamsSpillPayload $postgres ($Changes * [long]$WarmupTransactions) $MeasurementTransactions $capture
        if ($capture.environment.verifiedSpoolTransactions -ne $MeasurementTransactions -or $capture.environment.minimumReadySpoolBytes -lt $streamsSpillBytes)
        { throw 'The candidate did not verify every measured transaction crossed the physical spool boundary.' }
    }
    $verifiedPath = Join-Path $output 'candidate.json'
    if ($sync)
    {
        $transactions = $WarmupTransactions + $MeasurementTransactions
        $destinationProof = switch -CaseSensitive ($Sink)
        {
            'redis' { Assert-SyncRedisDelivery $postgres $destinationFixture $Changes $transactions $dotnet $assembly }
            'nats' { Assert-SyncNatsDelivery $postgres $destinationFixture $Changes $transactions $dotnet $assembly candidate }
            'opensearch' { Assert-SyncOpenSearchDelivery $postgres $destinationFixture $Changes $transactions $dotnet $assembly candidate }
            'kafka' { Assert-SyncKafkaDelivery $postgres $destinationFixture $Changes $transactions $dotnet $assembly candidate }
            's3' { Assert-SyncS3Delivery $postgres $destinationFixture $Changes $transactions $dotnet $assembly }
            'webhooks' { Assert-SyncWebhookDelivery $postgres $destinationFixture $Changes $transactions }
            'postgresql' { Assert-SyncPostgreSqlDelivery $postgres $Changes $transactions }
        }
        if ($destinationFixture) { $capture.environment | Add-Member -NotePropertyName destinationImage -NotePropertyValue $destinationFixture.Image }
        $capture.environment | Add-Member -NotePropertyName destinationProof -NotePropertyValue $destinationProof
        $capture | ConvertTo-Json -Depth 12 -Compress | Set-Content -LiteralPath $verifiedPath -Encoding utf8NoBOM
    }
    else
    {
        $sourceProof = Confirm-StreamsCaptureSource $postgres $Changes $WarmupTransactions $MeasurementTransactions ($Scenario -ceq 'snapshot') $capture
        $capture.environment | Add-Member -NotePropertyName sourceFixture -NotePropertyValue $sourceProof
        $capture | ConvertTo-Json -Depth 12 -Compress | Set-Content -LiteralPath $verifiedPath -Encoding utf8NoBOM
    }
    "Captured $($capture.measurement.completedOperations) changes in '$verifiedPath'."
}
finally
{
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = $previousConnection
    $env:BLUETUSK_BENCHMARK_REDIS_ENDPOINT = $previousRedisConnection
    $env:BLUETUSK_BENCHMARK_NATS_ENDPOINT = $previousNatsConnection
    $env:BLUETUSK_BENCHMARK_OPENSEARCH_ENDPOINT = $previousOpenSearchConnection
    $env:BLUETUSK_BENCHMARK_KAFKA_ENDPOINT = $previousKafkaConnection
    if ($previousS3Environment) { Restore-SyncS3Environment $previousS3Environment }
    if ($previousWebhookEnvironment) { Restore-SyncWebhookEnvironment $previousWebhookEnvironment }
    if ($candidate)
    {
        if (-not $candidate.HasExited) { $candidate.Kill($true) }
        $candidate.Dispose()
    }
    & (Join-Path $PSScriptRoot 'stop-performance-fixture.ps1') -RunId $RunId -Owner $Owner
    foreach ($file in 'connection.secret', 'container-connection.secret', 'postgres.env.secret')
    {
        $path = Join-Path $fixtureDirectory $file
        if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
    }
}
