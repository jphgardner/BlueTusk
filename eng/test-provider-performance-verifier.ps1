[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-provider-performance.ps1'
$budgetPath = Join-Path $PSScriptRoot 'provider-performance-budgets.json'
$budget = Get-Content -LiteralPath $budgetPath -Raw | ConvertFrom-Json
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) "bluetusk-provider-self-test-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $temporaryRoot

function New-Samples
{
    param([double] $Value)

    return [double[]] @(0..500 | ForEach-Object {
        $Value * (1 + ((($_ % 5) - 2) / 1000))
    })
}

function New-Trials
{
    return @(0..4 | ForEach-Object {
        [ordered] @{
            candidateFirst = ($_ % 2) -eq 0
            candidateNanosecondsPerOperation = New-Samples 99
            referenceNanosecondsPerOperation = New-Samples 100
        }
    })
}

function New-PairedFixture
{
    return [ordered] @{
        schemaVersion = 1
        method = 'alternating-provider-blocks'
        capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        stopwatchFrequency = 10000000
        trialCount = [int] $budget.trialCount
        blocksPerTrial = [int] $budget.blocksPerTrial
        workloads = @($budget.workloads | ForEach-Object {
            [ordered] @{
                candidate = [string] $_.candidate
                reference = [string] $_.reference
                warmupOperationsPerProvider = [int] $_.warmupOperationsPerProvider
                operationsPerBlock = [int] $_.operationsPerBlock
                trials = New-Trials
            }
        })
    }
}

function New-BenchmarkFixture
{
    $benchmarks = @()
    foreach ($workload in $budget.workloads)
    {
        $benchmarks += [ordered] @{
            Method = [string] $workload.candidate
            Memory = [ordered] @{ BytesAllocatedPerOperation = 90 }
        }
        $benchmarks += [ordered] @{
            Method = [string] $workload.reference
            Memory = [ordered] @{ BytesAllocatedPerOperation = 100 }
        }
    }

    return [ordered] @{ Benchmarks = $benchmarks }
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

function Invoke-Verification
{
    param(
        [object] $Paired,
        [object] $Report,
        [string] $Name
    )

    $pairedPath = Write-Fixture $Paired "$Name-paired"
    $reportPath = Write-Fixture $Report "$Name-report"
    & $verifier `
        -ReportPath $reportPath `
        -PairedReportPath $pairedPath `
        -BudgetPath $budgetPath *> $null
}

function Assert-Rejected
{
    param(
        [scriptblock] $Mutate,
        [string] $Name
    )

    $paired = Copy-Fixture (New-PairedFixture)
    $report = Copy-Fixture (New-BenchmarkFixture)
    & $Mutate $paired $report
    try
    {
        Invoke-Verification $paired $report $Name
        throw "Negative paired provider fixture '$Name' was accepted."
    }
    catch
    {
        if ($_.Exception.Message -eq
            "Negative paired provider fixture '$Name' was accepted.")
        {
            throw
        }
    }
}

try
{
    Invoke-Verification (New-PairedFixture) (New-BenchmarkFixture) 'positive'

    Assert-Rejected -Name 'schema' -Mutate {
        param($paired, $report)
        $paired.schemaVersion = 2
    }
    Assert-Rejected -Name 'method' -Mutate {
        param($paired, $report)
        $paired.method = 'sequential-providers'
    }
    Assert-Rejected -Name 'unexpected-property' -Mutate {
        param($paired, $report)
        $paired | Add-Member -NotePropertyName accepted -NotePropertyValue $true
    }
    Assert-Rejected -Name 'future' -Mutate {
        param($paired, $report)
        $paired.capturedUtc = [DateTimeOffset]::UtcNow.AddDays(1).ToString('O')
    }
    Assert-Rejected -Name 'workload-count' -Mutate {
        param($paired, $report)
        $paired.workloads = @($paired.workloads)[0..3]
    }
    Assert-Rejected -Name 'trial-count' -Mutate {
        param($paired, $report)
        $paired.workloads[0].trials = @($paired.workloads[0].trials)[0..3]
    }
    Assert-Rejected -Name 'sample-count' -Mutate {
        param($paired, $report)
        $paired.workloads[0].trials[0].candidateNanosecondsPerOperation =
            @($paired.workloads[0].trials[0].candidateNanosecondsPerOperation)[0..499]
    }
    Assert-Rejected -Name 'non-positive-sample' -Mutate {
        param($paired, $report)
        $paired.workloads[0].trials[0].candidateNanosecondsPerOperation[0] = 0
    }
    Assert-Rejected -Name 'provider-order' -Mutate {
        param($paired, $report)
        $paired.workloads[0].trials[1].candidateFirst = $true
    }
    Assert-Rejected -Name 'duplicate-workload' -Mutate {
        param($paired, $report)
        $paired.workloads[1].candidate = $paired.workloads[0].candidate
        $paired.workloads[1].reference = $paired.workloads[0].reference
    }
    Assert-Rejected -Name 'workload-programme' -Mutate {
        param($paired, $report)
        $paired.workloads[0].operationsPerBlock++
    }
    Assert-Rejected -Name 'latency-regression' -Mutate {
        param($paired, $report)
        foreach ($trial in $paired.workloads[0].trials)
        {
            $trial.candidateNanosecondsPerOperation = New-Samples 101
            $trial.referenceNanosecondsPerOperation = New-Samples 100
        }
    }
    Assert-Rejected -Name 'allocation-regression' -Mutate {
        param($paired, $report)
        $candidate = [string] $paired.workloads[0].candidate
        ($report.Benchmarks | Where-Object Method -eq $candidate).Memory.
            BytesAllocatedPerOperation = 101
    }
}
finally
{
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}

Write-Output (
    'Provider performance verifier self-test passed: one positive fixture and ' +
    'thirteen fail-closed mutations.')
