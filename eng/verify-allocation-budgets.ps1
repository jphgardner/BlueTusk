param(
    [string]$BudgetFile = (Join-Path $PSScriptRoot "..\benchmarks\allocation-budgets.json"),
    [string]$BaselinePath = (Join-Path $PSScriptRoot "..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\results")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$configuration = Get-Content -LiteralPath $BudgetFile -Raw | ConvertFrom-Json
if ([int]$configuration.schemaVersion -ne 2) {
    throw "Unsupported allocation budget schema version '$($configuration.schemaVersion)'."
}

$actual = [System.Collections.Generic.List[object]]::new()
$reportFiles = @(
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*-report-brief.json"
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*MultiplexingComparisonBenchmarks-report-full.json"
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*-hardening.json"
)
$reportFiles | ForEach-Object {
    $report = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    foreach ($benchmark in $report.Benchmarks) {
        if ($null -ne $benchmark.Memory -and $null -ne $benchmark.Memory.BytesAllocatedPerOperation) {
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
                BytesAllocatedPerOperation = [long]$benchmark.Memory.BytesAllocatedPerOperation
                Gen2Collections = [long]$benchmark.Memory.Gen2Collections
            })
        }
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($budget in $configuration.budgets) {
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
        $failures.Add("Duplicate allocation budget for $displayName.")
        continue
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
        $failures.Add("Missing allocation result for $displayName.")
        continue
    }
    if ($matches.Count -ne 1) {
        $failures.Add(
            "Allocation result for $displayName is ambiguous; found $($matches.Count) measurements.")
        continue
    }

    $measurement = $matches[0]
    $allocated = $measurement.BytesAllocatedPerOperation
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
                "Allocation budget for $displayName has invalid normalization parameter '$divisorName'.")
            continue
        }
        $allocated = [long][math]::Ceiling(
            $allocated / [double]$divisor)
    }
    $maximum = [long]$budget.maximumBytesPerOperation
    if ($allocated -gt $maximum) {
        $failures.Add("$displayName allocated $allocated B/op; budget is $maximum B/op.")
    }
    else {
        Write-Host "${displayName}: $allocated B/op <= $maximum B/op"
    }

    if ($budget.PSObject.Properties.Name -contains "maximumGen2Collections") {
        $maximumGen2 = [long]$budget.maximumGen2Collections
        if ($measurement.Gen2Collections -gt $maximumGen2) {
            $failures.Add(
                "$displayName recorded $($measurement.Gen2Collections) Gen2 collections; " +
                "budget is $maximumGen2.")
        }
        else {
            Write-Host (
                "${displayName}: $($measurement.Gen2Collections) Gen2 collections <= $maximumGen2")
        }
    }
}

if ($failures.Count -ne 0) {
    throw "Allocation budget verification failed:`n$($failures -join "`n")"
}

Write-Host "Verified $($configuration.budgets.Count) allocation budgets for $($configuration.environment)."
