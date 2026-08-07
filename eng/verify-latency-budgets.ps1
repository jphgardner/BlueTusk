param(
    [string]$BudgetFile = (Join-Path $PSScriptRoot "..\benchmarks\latency-budgets.json"),
    [string]$BaselinePath = (Join-Path $PSScriptRoot "..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\results")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$configuration = Get-Content -LiteralPath $BudgetFile -Raw | ConvertFrom-Json
if ([int]$configuration.schemaVersion -ne 1) {
    throw "Unsupported latency budget schema version '$($configuration.schemaVersion)'."
}

$actual = @{}
$reports = @(
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*-report-brief.json"
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

        $actual[[string]$benchmark.FullName] = $benchmark
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($budget in @($configuration.budgets)) {
    $benchmarkName = [string]$budget.benchmark
    if (-not $seen.Add($benchmarkName)) {
        $failures.Add("Duplicate latency budget for $benchmarkName.")
        continue
    }

    if (-not $actual.ContainsKey($benchmarkName)) {
        $failures.Add("Missing latency result for $benchmarkName.")
        continue
    }

    $benchmark = $actual[$benchmarkName]
    $samples = if ($null -ne $benchmark.Statistics.PSObject.Properties['N']) {
        [int]$benchmark.Statistics.N
    }
    elseif ($null -ne $benchmark.Statistics.PSObject.Properties['OriginalValues']) {
        @($benchmark.Statistics.OriginalValues).Count
    }
    else {
        0
    }
    $minimumSamples = [int]$configuration.policy.minimumSamples
    if ($samples -lt $minimumSamples) {
        $failures.Add("$benchmarkName has $samples samples; at least $minimumSamples are required.")
    }

    $mean = [double]$benchmark.Statistics.Mean
    $p95 = [double]$benchmark.Statistics.Percentiles.P95
    $maximumMean = [double]$budget.maximumMeanNanoseconds
    $maximumP95 = [double]$budget.maximumP95Nanoseconds
    if (-not [double]::IsFinite($mean) -or $mean -le 0) {
        $failures.Add("$benchmarkName has an invalid mean '$mean'.")
    }
    elseif ($mean -gt $maximumMean) {
        $failures.Add("$benchmarkName mean is $([math]::Round($mean, 3)) ns; budget is $maximumMean ns.")
    }

    if (-not [double]::IsFinite($p95) -or $p95 -le 0) {
        $failures.Add("$benchmarkName has an invalid P95 '$p95'.")
    }
    elseif ($p95 -gt $maximumP95) {
        $failures.Add("$benchmarkName P95 is $([math]::Round($p95, 3)) ns; budget is $maximumP95 ns.")
    }

    if ($mean -le $maximumMean -and $p95 -le $maximumP95) {
        Write-Host (
            "{0}: mean {1:N3}/{2:N3} ns; P95 {3:N3}/{4:N3} ns" -f
            $benchmarkName,
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
