param(
    [string]$ReportPath = (
        Join-Path $PSScriptRoot (
            "..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\results\" +
            "BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json")),
    [string]$BudgetPath = (
        Join-Path $PSScriptRoot "multiplexing-performance-budgets.json")
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Percentile {
    param(
        [double[]]$Values,
        [double]$Percentile
    )

    if ($Values.Length -eq 0) {
        throw "Cannot calculate a percentile without measurements."
    }

    $sorted = @($Values | Sort-Object)
    $rank = ($sorted.Length - 1) * ($Percentile / 100)
    $lowerIndex = [Math]::Floor($rank)
    $upperIndex = [Math]::Ceiling($rank)
    if ($lowerIndex -eq $upperIndex) {
        return [double]$sorted[$lowerIndex]
    }

    $fraction = $rank - $lowerIndex
    return [double]$sorted[$lowerIndex] +
        (([double]$sorted[$upperIndex] - [double]$sorted[$lowerIndex]) * $fraction)
}

function Get-Metrics {
    param(
        [object]$Benchmark,
        [int]$MinimumSamples
    )

    $resultMeasurements = @(
        $Benchmark.Measurements |
            Where-Object {
                $_.IterationMode -eq 'Workload' -and
                $_.IterationStage -eq 'Result'
            })
    if ($resultMeasurements.Count -lt $MinimumSamples) {
        throw (
            "$($Benchmark.Method) has $($resultMeasurements.Count) measured result samples; " +
            "$MinimumSamples are required.")
    }

    $normalizedNanoseconds = [double[]]@(
        $resultMeasurements |
            ForEach-Object {
                [double]$_.Nanoseconds / [double]$_.Operations
            })
    return [pscustomobject]@{
        Method = [string]$Benchmark.Method
        Samples = $resultMeasurements.Count
        MeanNanoseconds = [double]$Benchmark.Statistics.Mean
        P95Nanoseconds = Get-Percentile $normalizedNanoseconds 95
        P99Nanoseconds = Get-Percentile $normalizedNanoseconds 99
        OperationsPerSecond = 1e9 / [double]$Benchmark.Statistics.Mean
        AllocatedBytes = [double]$Benchmark.Memory.BytesAllocatedPerOperation
    }
}

function Assert-MaximumRatio {
    param(
        [string]$Metric,
        [double]$Candidate,
        [double]$Reference,
        [double]$Maximum,
        [string]$CandidateName,
        [string]$ReferenceName
    )

    if ($Reference -le 0) {
        throw "$ReferenceName has a non-positive $Metric reference value."
    }

    $ratio = $Candidate / $Reference
    if ($ratio -gt $Maximum) {
        throw (
            "$CandidateName $Metric ratio against $ReferenceName is " +
            "$($ratio.ToString('F4')); maximum is $($Maximum.ToString('F4')).")
    }
}

$resolvedReport = (Resolve-Path -LiteralPath $ReportPath).Path
$resolvedBudgets = (Resolve-Path -LiteralPath $BudgetPath).Path
$report = Get-Content -LiteralPath $resolvedReport -Raw | ConvertFrom-Json
$budgets = Get-Content -LiteralPath $resolvedBudgets -Raw | ConvertFrom-Json
$minimumSamples = [int]$budgets.minimumResultSamples
$metrics = @{}

foreach ($benchmark in $report.Benchmarks) {
    if ([string]$benchmark.Type -ne 'MultiplexingComparisonBenchmarks') {
        continue
    }

    $metric = Get-Metrics $benchmark $minimumSamples
    $metrics[$metric.Method] = $metric
}

foreach ($comparison in $budgets.comparisons) {
    $candidateName = [string]$comparison.candidate
    $referenceName = [string]$comparison.reference
    if (-not $metrics.ContainsKey($candidateName)) {
        throw "The report does not contain $candidateName."
    }

    if (-not $metrics.ContainsKey($referenceName)) {
        throw "The report does not contain $referenceName."
    }

    $candidate = $metrics[$candidateName]
    $reference = $metrics[$referenceName]
    Assert-MaximumRatio `
        -Metric 'mean latency' `
        -Candidate $candidate.MeanNanoseconds `
        -Reference $reference.MeanNanoseconds `
        -Maximum ([double]$comparison.maxMeanRatio) `
        -CandidateName $candidateName `
        -ReferenceName $referenceName
    Assert-MaximumRatio `
        -Metric 'P95 latency' `
        -Candidate $candidate.P95Nanoseconds `
        -Reference $reference.P95Nanoseconds `
        -Maximum ([double]$comparison.maxP95Ratio) `
        -CandidateName $candidateName `
        -ReferenceName $referenceName
    Assert-MaximumRatio `
        -Metric 'P99 latency' `
        -Candidate $candidate.P99Nanoseconds `
        -Reference $reference.P99Nanoseconds `
        -Maximum ([double]$comparison.maxP99Ratio) `
        -CandidateName $candidateName `
        -ReferenceName $referenceName
    Assert-MaximumRatio `
        -Metric 'managed allocation' `
        -Candidate $candidate.AllocatedBytes `
        -Reference $reference.AllocatedBytes `
        -Maximum ([double]$comparison.maxAllocationRatio) `
        -CandidateName $candidateName `
        -ReferenceName $referenceName
}

$metrics.Values |
    Sort-Object Method |
    Format-Table `
        Method,
        Samples,
        @{ Label = 'Mean us'; Expression = { $_.MeanNanoseconds / 1e3 }; FormatString = 'F2' },
        @{ Label = 'P95 us'; Expression = { $_.P95Nanoseconds / 1e3 }; FormatString = 'F2' },
        @{ Label = 'P99 us'; Expression = { $_.P99Nanoseconds / 1e3 }; FormatString = 'F2' },
        @{ Label = 'Ops/s'; Expression = { $_.OperationsPerSecond }; FormatString = 'F0' },
        @{ Label = 'Allocated B'; Expression = { $_.AllocatedBytes }; FormatString = 'F0' }

Write-Host (
    "Multiplexing performance evidence satisfies all configured mean, P95, P99, " +
    "throughput-derived, and allocation budgets.")
