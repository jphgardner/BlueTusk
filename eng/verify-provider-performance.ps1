[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportPath,

    [Parameter(Mandatory)]
    [string] $PairedReportPath,

    [string] $BudgetPath = (Join-Path $PSScriptRoot 'provider-performance-budgets.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Percentile {
    param(
        [double[]] $Values,
        [double] $Percentile
    )

    if ($Values.Length -eq 0) {
        throw 'Cannot calculate a percentile without measurements.'
    }

    $sorted = @($Values | Sort-Object)
    $rank = ($sorted.Length - 1) * ($Percentile / 100)
    $lowerIndex = [Math]::Floor($rank)
    $upperIndex = [Math]::Ceiling($rank)
    if ($lowerIndex -eq $upperIndex) {
        return [double] $sorted[$lowerIndex]
    }

    $fraction = $rank - $lowerIndex
    return [double] $sorted[$lowerIndex] +
        (([double] $sorted[$upperIndex] - [double] $sorted[$lowerIndex]) * $fraction)
}

function Assert-ExactProperties {
    param(
        [object] $Value,
        [string[]] $Expected,
        [string] $Label
    )

    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($property in $Value.PSObject.Properties) {
        $null = $actual.Add([string] $property.Name)
    }

    if ($actual.Count -ne $Expected.Count -or
        @($Expected | Where-Object { -not $actual.Contains($_) }).Count -ne 0) {
        throw "$Label does not match its closed property schema."
    }
}

function Get-PositiveSamples {
    param(
        [object[]] $Values,
        [int] $ExpectedCount,
        [string] $Label
    )

    if ($Values.Count -ne $ExpectedCount) {
        throw "$Label contains $($Values.Count) samples; expected $ExpectedCount."
    }

    $samples = [double[]] @($Values | ForEach-Object { [double] $_ })
    foreach ($sample in $samples) {
        if ($sample -le 0 -or
            [double]::IsNaN($sample) -or
            [double]::IsInfinity($sample)) {
            throw "$Label contains a non-positive or non-finite sample."
        }
    }

    return $samples
}

function Assert-MaximumRatio {
    param(
        [string] $Metric,
        [double] $Actual,
        [double] $Maximum,
        [string] $Candidate,
        [string] $Reference
    )

    if ($Actual -gt $Maximum) {
        throw (
            "$Candidate median-of-trials $Metric ratio against $Reference is " +
            "$($Actual.ToString('F4')); maximum is $($Maximum.ToString('F4')).")
    }
}

$resolvedReport = (Resolve-Path -LiteralPath $ReportPath).Path
$resolvedPairedReport = (Resolve-Path -LiteralPath $PairedReportPath).Path
$resolvedBudget = (Resolve-Path -LiteralPath $BudgetPath).Path
$report = Get-Content -LiteralPath $resolvedReport -Raw | ConvertFrom-Json
$pairedReport = Get-Content -LiteralPath $resolvedPairedReport -Raw | ConvertFrom-Json
$budget = Get-Content -LiteralPath $resolvedBudget -Raw | ConvertFrom-Json

Assert-ExactProperties `
    -Value $pairedReport `
    -Expected @(
        'schemaVersion',
        'method',
        'capturedUtc',
        'stopwatchFrequency',
        'trialCount',
        'blocksPerTrial',
        'workloads') `
    -Label 'The paired provider report'

if ([int] $pairedReport.schemaVersion -ne 1 -or
    [string] $pairedReport.method -ne 'alternating-provider-blocks' -or
    [long] $pairedReport.stopwatchFrequency -le 0) {
    throw 'The paired provider report header is invalid.'
}
if ([int] $budget.schemaVersion -ne 1 -or
    [int] $pairedReport.trialCount -ne [int] $budget.trialCount -or
    [int] $pairedReport.blocksPerTrial -ne [int] $budget.blocksPerTrial) {
    throw 'The paired provider report does not match the checked-in trial programme.'
}

$capturedUtc = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse(
        [string] $pairedReport.capturedUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref] $capturedUtc) -or
    $capturedUtc -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
    throw 'The paired provider report capturedUtc is invalid or in the future.'
}

$pairedWorkloads = @($pairedReport.workloads)
$budgets = @($budget.workloads)
if ($pairedWorkloads.Count -ne $budgets.Count) {
    throw (
        "The paired provider report contains $($pairedWorkloads.Count) workloads; " +
        "$($budgets.Count) are required.")
}

$benchmarksByMethod = @{}
foreach ($benchmark in @($report.Benchmarks)) {
    $method = [string] $benchmark.Method
    if ($benchmarksByMethod.ContainsKey($method)) {
        throw "The provider BenchmarkDotNet report repeats '$method'."
    }
    $benchmarksByMethod[$method] = $benchmark
}

$metrics = @()
foreach ($workloadBudget in $budgets) {
    $candidate = [string] $workloadBudget.candidate
    $reference = [string] $workloadBudget.reference
    $matches = @($pairedWorkloads | Where-Object {
        [string] $_.candidate -eq $candidate -and
        [string] $_.reference -eq $reference
    })
    if ($matches.Count -ne 1) {
        throw "The paired provider report must contain exactly one '$candidate|$reference' workload."
    }

    $workload = $matches[0]
    Assert-ExactProperties `
        -Value $workload `
        -Expected @(
            'candidate',
            'reference',
            'warmupOperationsPerProvider',
            'operationsPerBlock',
            'trials') `
        -Label "$candidate|$reference"
    if ([int] $workload.warmupOperationsPerProvider -ne
            [int] $workloadBudget.warmupOperationsPerProvider -or
        [int] $workload.operationsPerBlock -ne [int] $workloadBudget.operationsPerBlock) {
        throw "$candidate|$reference does not match its checked-in workload programme."
    }

    $trials = @($workload.trials)
    if ($trials.Count -ne [int] $budget.trialCount) {
        throw "$candidate|$reference contains $($trials.Count) trials; expected $($budget.trialCount)."
    }

    $meanRatios = [double[]]::new($trials.Count)
    $p95Ratios = [double[]]::new($trials.Count)
    $p99Ratios = [double[]]::new($trials.Count)
    for ($trialIndex = 0; $trialIndex -lt $trials.Count; $trialIndex++) {
        $trial = $trials[$trialIndex]
        Assert-ExactProperties `
            -Value $trial `
            -Expected @(
                'candidateFirst',
                'candidateNanosecondsPerOperation',
                'referenceNanosecondsPerOperation') `
            -Label "$candidate|$reference trial $trialIndex"
        if ([bool] $trial.candidateFirst -ne (($trialIndex % 2) -eq 0)) {
            throw "$candidate|$reference trial $trialIndex has an invalid provider start order."
        }

        $candidateSamples = Get-PositiveSamples `
            -Values @($trial.candidateNanosecondsPerOperation) `
            -ExpectedCount ([int] $budget.blocksPerTrial) `
            -Label "$candidate|$reference trial $trialIndex candidate"
        $referenceSamples = Get-PositiveSamples `
            -Values @($trial.referenceNanosecondsPerOperation) `
            -ExpectedCount ([int] $budget.blocksPerTrial) `
            -Label "$candidate|$reference trial $trialIndex reference"

        $candidateMean = ($candidateSamples | Measure-Object -Average).Average
        $referenceMean = ($referenceSamples | Measure-Object -Average).Average
        $meanRatios[$trialIndex] = $candidateMean / $referenceMean
        $p95Ratios[$trialIndex] =
            (Get-Percentile $candidateSamples 95) /
            (Get-Percentile $referenceSamples 95)
        $p99Ratios[$trialIndex] =
            (Get-Percentile $candidateSamples 99) /
            (Get-Percentile $referenceSamples 99)
    }

    $meanRatio = Get-Percentile $meanRatios 50
    $p95Ratio = Get-Percentile $p95Ratios 50
    $p99Ratio = Get-Percentile $p99Ratios 50
    Assert-MaximumRatio mean $meanRatio ([double] $workloadBudget.maximumMeanRatio) $candidate $reference
    Assert-MaximumRatio P95 $p95Ratio ([double] $workloadBudget.maximumP95Ratio) $candidate $reference
    Assert-MaximumRatio P99 $p99Ratio ([double] $workloadBudget.maximumP99Ratio) $candidate $reference

    if (-not $benchmarksByMethod.ContainsKey($candidate) -or
        -not $benchmarksByMethod.ContainsKey($reference)) {
        throw "The provider BenchmarkDotNet report is missing '$candidate' or '$reference'."
    }
    $candidateAllocated = [double] $benchmarksByMethod[$candidate].Memory.BytesAllocatedPerOperation
    $referenceAllocated = [double] $benchmarksByMethod[$reference].Memory.BytesAllocatedPerOperation
    if ($referenceAllocated -le 0) {
        throw "$reference has a non-positive allocation reference."
    }
    $allocationRatio = $candidateAllocated / $referenceAllocated
    Assert-MaximumRatio `
        allocation `
        $allocationRatio `
        ([double] $workloadBudget.maximumAllocationRatio) `
        $candidate `
        $reference

    $metrics += [pscustomobject] @{
        Workload = $candidate -replace '^BlueTusk', '' -replace 'Async$', ''
        MeanRatio = $meanRatio
        P95Ratio = $p95Ratio
        P99Ratio = $p99Ratio
        AllocationRatio = $allocationRatio
    }
}

$metrics | Format-Table -AutoSize | Out-String | Write-Output
Write-Output (
    "Provider performance gate passed for $($metrics.Count) workloads using " +
    "$($budget.trialCount) trials and $($budget.blocksPerTrial) alternating blocks per trial.")
