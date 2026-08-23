[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-multiplexing-performance.ps1'
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) "bluetusk-multiplexing-self-test-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $temporaryRoot

function New-Samples
{
    param([double] $Value)

    return [double[]]@(0..500 | ForEach-Object {
        $Value * (1 + ((($_ % 5) - 2) / 1000))
    })
}

function New-Trials
{
    param(
        [double] $Candidate,
        [double] $Reference
    )

    return @(0..4 | ForEach-Object {
        [ordered]@{
            candidateFirst = ($_ % 2) -eq 0
            candidateNanosecondsPerOperation = New-Samples $Candidate
            referenceNanosecondsPerOperation = New-Samples $Reference
        }
    })
}

function New-Benchmark
{
    param(
        [string] $Method,
        [double] $Nanoseconds,
        [double] $AllocatedBytes
    )

    return [ordered] @{
        Type = 'MultiplexingComparisonBenchmarks'
        Method = $Method
        Measurements = @(0..19 | ForEach-Object {
            [ordered] @{
                IterationMode = 'Workload'
                IterationStage = 'Result'
                Nanoseconds = $Nanoseconds * (1 + ((($_ % 5) - 2) / 1000))
                Operations = 1
            }
        })
        Statistics = [ordered] @{ Mean = $Nanoseconds }
        Memory = [ordered] @{ BytesAllocatedPerOperation = $AllocatedBytes }
    }
}

function New-BenchmarkFixture
{
    return [ordered] @{
        Benchmarks = @(
            New-Benchmark 'BlueTuskConcurrentScalarBurstAsync' 20 50
            New-Benchmark 'NpgsqlConcurrentScalarBurstAsync' 25 100
            New-Benchmark 'BlueTuskPooledConcurrentScalarBurstAsync' 100 90
            New-Benchmark 'NpgsqlPooledConcurrentScalarBurstAsync' 105 100
            New-Benchmark 'BlueTuskReusedScalarBurstAsync' 15 40
            New-Benchmark 'NpgsqlReusedScalarBurstAsync' 20 100
            New-Benchmark 'BlueTuskPooledReusedScalarBurstAsync' 100 90
            New-Benchmark 'NpgsqlPooledReusedScalarBurstAsync' 105 100
        )
    }
}

function New-Fixture
{
    return [ordered]@{
        schemaVersion = 1
        method = 'alternating-provider-blocks'
        capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        stopwatchFrequency = 10000000
        operationsPerBurst = 64
        warmupBurstsPerProvider = 64
        trialCount = 5
        blocksPerTrial = 501
        burstsPerBlock = 4
        workloads = @(
            [ordered]@{
                candidate = 'BlueTuskConcurrentScalarBurstAsync'
                reference = 'NpgsqlConcurrentScalarBurstAsync'
                trials = New-Trials 99 100
            },
            [ordered]@{
                candidate = 'BlueTuskReusedScalarBurstAsync'
                reference = 'NpgsqlReusedScalarBurstAsync'
                trials = New-Trials 80 100
            },
            [ordered]@{
                candidate = 'BlueTuskPooledConcurrentScalarBurstAsync'
                reference = 'NpgsqlPooledConcurrentScalarBurstAsync'
                trials = New-Trials 99 100
            },
            [ordered]@{
                candidate = 'BlueTuskPooledReusedScalarBurstAsync'
                reference = 'NpgsqlPooledReusedScalarBurstAsync'
                trials = New-Trials 95 100
            }
        )
    }
}

function Copy-Fixture
{
    param([object] $Fixture)

    return $Fixture | ConvertTo-Json -Depth 12 | ConvertFrom-Json
}

function Write-Fixture
{
    param(
        [object] $Fixture,
        [string] $Name
    )

    $path = Join-Path $temporaryRoot "$Name.json"
    $Fixture | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $path -Encoding utf8NoBOM
    return $path
}

function Assert-Rejected
{
    param(
        [scriptblock] $Mutate,
        [string] $Name
    )

    $fixture = Copy-Fixture (New-Fixture)
    & $Mutate $fixture
    $path = Write-Fixture $fixture $Name
    try
    {
        & $verifier -ReportPath $reportPath -PairedReportPath $path *> $null
        throw "Negative paired multiplexing fixture '$Name' was accepted."
    }
    catch
    {
        if ($_.Exception.Message -eq
            "Negative paired multiplexing fixture '$Name' was accepted.")
        {
            throw
        }
    }
}

try
{
    $reportPath = Write-Fixture (New-BenchmarkFixture) 'benchmark-report'
    $positivePath = Write-Fixture (New-Fixture) 'positive'
    & $verifier -ReportPath $reportPath -PairedReportPath $positivePath *> $null

    Assert-Rejected -Name 'schema' -Mutate {
        param($fixture)
        $fixture.schemaVersion = 2
    }
    Assert-Rejected -Name 'method' -Mutate {
        param($fixture)
        $fixture.method = 'sequential-providers'
    }
    Assert-Rejected -Name 'unexpected-property' -Mutate {
        param($fixture)
        $fixture | Add-Member -NotePropertyName accepted -NotePropertyValue $true
    }
    Assert-Rejected -Name 'future' -Mutate {
        param($fixture)
        $fixture.capturedUtc = [DateTimeOffset]::UtcNow.AddDays(1).ToString('O')
    }
    Assert-Rejected -Name 'trial-count' -Mutate {
        param($fixture)
        $fixture.trialCount = 4
    }
    Assert-Rejected -Name 'sample-count' -Mutate {
        param($fixture)
        $fixture.workloads[0].trials[0].candidateNanosecondsPerOperation =
            @($fixture.workloads[0].trials[0].candidateNanosecondsPerOperation)[0..499]
    }
    Assert-Rejected -Name 'non-positive-sample' -Mutate {
        param($fixture)
        $fixture.workloads[0].trials[0].candidateNanosecondsPerOperation[0] = 0
    }
    Assert-Rejected -Name 'provider-order' -Mutate {
        param($fixture)
        $fixture.workloads[0].trials[1].candidateFirst = $true
    }
    Assert-Rejected -Name 'duplicate-workload' -Mutate {
        param($fixture)
        $fixture.workloads[1].candidate = $fixture.workloads[0].candidate
        $fixture.workloads[1].reference = $fixture.workloads[0].reference
    }
    Assert-Rejected -Name 'latency-regression' -Mutate {
        param($fixture)
        foreach ($trial in $fixture.workloads[0].trials)
        {
            $trial.candidateNanosecondsPerOperation = New-Samples 106
            $trial.referenceNanosecondsPerOperation = New-Samples 100
        }
    }
}
finally
{
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}

Write-Output (
    'Multiplexing performance verifier self-test passed: one paired positive ' +
    'fixture and ten fail-closed mutations.')
