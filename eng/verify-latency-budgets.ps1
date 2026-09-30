param(
    [string]$BudgetFile = (Join-Path $PSScriptRoot "..\benchmarks\latency-budgets.json"),
    [string]$BaselinePath = (Join-Path $PSScriptRoot "..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\results")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$configuration = Get-Content -LiteralPath $BudgetFile -Raw | ConvertFrom-Json
if ([int]$configuration.schemaVersion -ne 2) {
    throw "Unsupported latency budget schema version '$($configuration.schemaVersion)'."
}

$maximumMeanRegressionPercent = [double]$configuration.policy.maximumMeanRegressionPercent
$maximumP95RegressionPercent = [double]$configuration.policy.maximumP95RegressionPercent
$minimumSamples = [int]$configuration.policy.minimumSamples
$minimumCalibrationObservations = [int]$configuration.policy.minimumCalibrationObservations
$calibratedBudgetRoundingNanoseconds = [double]$configuration.policy.calibratedBudgetRoundingNanoseconds
if (-not [double]::IsFinite($maximumMeanRegressionPercent) -or
    $maximumMeanRegressionPercent -lt 0 -or
    $maximumMeanRegressionPercent -gt 100 -or
    -not [double]::IsFinite($maximumP95RegressionPercent) -or
    $maximumP95RegressionPercent -lt 0 -or
    $maximumP95RegressionPercent -gt 100 -or
    $minimumSamples -lt 3 -or
    $minimumCalibrationObservations -lt 2 -or
    -not [double]::IsFinite($calibratedBudgetRoundingNanoseconds) -or
    $calibratedBudgetRoundingNanoseconds -le 0) {
    throw 'Latency budget policy contains invalid limits.'
}

$actual = [System.Collections.Generic.List[object]]::new()
$reports = @(
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*-report-brief.json"
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*-report-full.json"
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*-hardening.json"
)
$reports | ForEach-Object {
    $report = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    foreach ($benchmark in @($report.Benchmarks)) {
        if ($null -eq $benchmark.Statistics -or
            $null -eq $benchmark.Statistics.Mean -or
            $null -eq $benchmark.Statistics.Percentiles.P95) {
            continue
        }

        $benchmarkName = if (
            -not [string]::IsNullOrWhiteSpace([string]$benchmark.Namespace) -and
            -not [string]::IsNullOrWhiteSpace([string]$benchmark.Type) -and
            -not [string]::IsNullOrWhiteSpace([string]$benchmark.Method)) {
            "$($benchmark.Namespace).$($benchmark.Type).$($benchmark.Method)"
        }
        else {
            [regex]::Replace([string]$benchmark.FullName, '\(.*\)$', '')
        }
        $actual.Add([pscustomobject]@{
            Benchmark = $benchmarkName
            Parameters = [string]$benchmark.Parameters
            Result = $benchmark
        })
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($budget in @($configuration.budgets)) {
    $benchmarkName = [string]$budget.benchmark
    $parameters = if ($null -ne $budget.PSObject.Properties['parameters']) {
        [string]$budget.parameters
    }
    else {
        ''
    }
    $budgetKey = "$benchmarkName|$parameters"
    $displayName = if ([string]::IsNullOrWhiteSpace($parameters)) {
        $benchmarkName
    }
    else {
        "$benchmarkName [$parameters]"
    }
    if (-not $seen.Add($budgetKey)) {
        $failures.Add("Duplicate latency budget for $displayName.")
        continue
    }

    if ([string]::IsNullOrWhiteSpace([string]$budget.reason)) {
        $failures.Add("Latency budget for $displayName has no reason.")
    }

    $maximumMean = [double]$budget.maximumMeanNanoseconds
    $maximumP95 = [double]$budget.maximumP95Nanoseconds
    if (-not [double]::IsFinite($maximumMean) -or $maximumMean -le 0 -or
        -not [double]::IsFinite($maximumP95) -or $maximumP95 -le 0 -or
        $maximumP95 -lt $maximumMean) {
        $failures.Add("Latency budget for $displayName has invalid mean/P95 ceilings.")
    }

    if ($null -ne $budget.PSObject.Properties['calibration']) {
        $calibration = $budget.calibration
        $observations = @($calibration.observations)
        if ([string]::IsNullOrWhiteSpace([string]$calibration.method)) {
            $failures.Add("Latency calibration for $benchmarkName has no method.")
        }
        if ($observations.Count -lt $minimumCalibrationObservations) {
            $failures.Add(
                "Latency calibration for $benchmarkName has $($observations.Count) observations; " +
                "at least $minimumCalibrationObservations are required.")
        }

        $calibrationRuns = [System.Collections.Generic.HashSet[long]]::new()
        $observedMeans = [System.Collections.Generic.List[double]]::new()
        $observedP95s = [System.Collections.Generic.List[double]]::new()
        foreach ($observation in $observations) {
            $sourceCommit = [string]$observation.sourceCommit
            $workflowRunId = [long]$observation.workflowRunId
            $observedMean = [double]$observation.meanNanoseconds
            $observedP95 = [double]$observation.p95Nanoseconds
            if ($sourceCommit -notmatch '^[0-9a-f]{40}$') {
                $failures.Add(
                    "Latency calibration for $benchmarkName has invalid source commit '$sourceCommit'.")
            }
            if ($workflowRunId -le 0 -or -not $calibrationRuns.Add($workflowRunId)) {
                $failures.Add(
                    "Latency calibration for $benchmarkName has invalid or duplicate workflow run '$workflowRunId'.")
            }
            if (-not [double]::IsFinite($observedMean) -or $observedMean -le 0 -or
                -not [double]::IsFinite($observedP95) -or $observedP95 -le 0 -or
                $observedP95 -lt $observedMean) {
                $failures.Add(
                    "Latency calibration for $benchmarkName has invalid mean/P95 evidence in run '$workflowRunId'.")
                continue
            }
            $observedMeans.Add($observedMean)
            $observedP95s.Add($observedP95)
        }

        if ($observedMeans.Count -ne 0 -and $observedP95s.Count -ne 0) {
            $maximumObservedMean = ($observedMeans | Measure-Object -Maximum).Maximum
            $maximumObservedP95 = ($observedP95s | Measure-Object -Maximum).Maximum
            $declaredMaximumMean = [double]$calibration.maximumObservedMeanNanoseconds
            $declaredMaximumP95 = [double]$calibration.maximumObservedP95Nanoseconds
            if ([math]::Abs($maximumObservedMean - $declaredMaximumMean) -gt 0.001 -or
                [math]::Abs($maximumObservedP95 - $declaredMaximumP95) -gt 0.001) {
                $failures.Add(
                    "Latency calibration for $benchmarkName does not declare the maximum observed values.")
            }

            $meanHeadroom = 1 + ($maximumMeanRegressionPercent / 100)
            $p95Headroom = 1 + ($maximumP95RegressionPercent / 100)
            $maximumCalibratedMean = [math]::Ceiling(
                ($maximumObservedMean * $meanHeadroom) / $calibratedBudgetRoundingNanoseconds) *
                $calibratedBudgetRoundingNanoseconds
            $maximumCalibratedP95 = [math]::Ceiling(
                ($maximumObservedP95 * $p95Headroom) / $calibratedBudgetRoundingNanoseconds) *
                $calibratedBudgetRoundingNanoseconds
            if ($maximumMean -lt $maximumObservedMean -or
                $maximumP95 -lt $maximumObservedP95 -or
                $maximumMean -gt $maximumCalibratedMean -or
                $maximumP95 -gt $maximumCalibratedP95) {
                $failures.Add(
                    "Latency budget for $benchmarkName is not within its evidence-derived ceilings " +
                    "($maximumCalibratedMean ns mean, $maximumCalibratedP95 ns P95).")
            }
        }
    }

    $matches = @($actual | Where-Object {
        $_.Benchmark -eq $benchmarkName -and $_.Parameters -eq $parameters
    })
    $usedLegacyFallback = $false
    if ($matches.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($parameters)) {
        $matches = @($actual | Where-Object {
            $_.Benchmark -eq $benchmarkName -and
            [string]::IsNullOrWhiteSpace($_.Parameters)
        })
        $usedLegacyFallback = $matches.Count -eq 1
    }
    if ($matches.Count -eq 0) {
        $failures.Add("Missing latency result for $displayName.")
        continue
    }
    if ($matches.Count -ne 1) {
        $failures.Add("Latency result for $displayName is ambiguous; found $($matches.Count) measurements.")
        continue
    }

    $benchmark = $matches[0].Result
    $samples = if ($null -ne $benchmark.Statistics.PSObject.Properties['N']) {
        [int]$benchmark.Statistics.N
    }
    elseif ($null -ne $benchmark.Statistics.PSObject.Properties['OriginalValues']) {
        @($benchmark.Statistics.OriginalValues).Count
    }
    else {
        0
    }
    if ($samples -lt $minimumSamples) {
        $failures.Add("$displayName has $samples samples; at least $minimumSamples are required.")
    }

    $mean = [double]$benchmark.Statistics.Mean
    $p95 = [double]$benchmark.Statistics.Percentiles.P95
    if (-not $usedLegacyFallback -and
        $null -ne $budget.PSObject.Properties['normalizationDivisorParameter']) {
        $divisorName = [string]$budget.normalizationDivisorParameter
        $parameterValues = @{}
        foreach ($part in @($parameters -split '&')) {
            $pair = @($part -split '=', 2)
            if ($pair.Count -eq 2) {
                $parameterValues[$pair[0]] = $pair[1]
            }
        }
        $divisor = 0L
        if (-not $parameterValues.ContainsKey($divisorName) -or
            -not [long]::TryParse(
                [string]$parameterValues[$divisorName],
                [ref]$divisor) -or
            $divisor -le 0) {
            $failures.Add(
                "Latency budget for $displayName has invalid normalization parameter '$divisorName'.")
            continue
        }
        $mean /= $divisor
        $p95 /= $divisor
    }
    if (-not [double]::IsFinite($mean) -or $mean -le 0) {
        $failures.Add("$displayName has an invalid mean '$mean'.")
    }
    elseif ($mean -gt $maximumMean) {
        $failures.Add("$displayName mean is $([math]::Round($mean, 3)) ns; budget is $maximumMean ns.")
    }

    if (-not [double]::IsFinite($p95) -or $p95 -le 0) {
        $failures.Add("$displayName has an invalid P95 '$p95'.")
    }
    elseif ($p95 -gt $maximumP95) {
        $failures.Add("$displayName P95 is $([math]::Round($p95, 3)) ns; budget is $maximumP95 ns.")
    }

    if ($mean -le $maximumMean -and $p95 -le $maximumP95) {
        Write-Host (
            "{0}: mean {1:N3}/{2:N3} ns; P95 {3:N3}/{4:N3} ns" -f
            $displayName,
            $mean,
            $maximumMean,
            $p95,
            $maximumP95)
    }
}

if ($failures.Count -ne 0) {
    throw "Latency budget verification failed:`n$($failures -join "`n")"
}

Write-Host (
    "Verified {0} reference-machine latency budgets for {1}; these are regression gates, not production SLOs." -f
    @($configuration.budgets).Count,
    [string]$configuration.environment)
