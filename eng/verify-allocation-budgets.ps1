param(
    [string]$BudgetFile = (Join-Path $PSScriptRoot "..\benchmarks\allocation-budgets.json"),
    [string]$BaselinePath = (Join-Path $PSScriptRoot "..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\results")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$configuration = Get-Content -LiteralPath $BudgetFile -Raw | ConvertFrom-Json
$actual = @{}
$reportFiles = @(
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*-report-brief.json"
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*MultiplexingComparisonBenchmarks-report-full.json"
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*-hardening.json"
)
$reportFiles | ForEach-Object {
    $report = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    foreach ($benchmark in $report.Benchmarks) {
        if ($null -ne $benchmark.Memory -and $null -ne $benchmark.Memory.BytesAllocatedPerOperation) {
            $actual[$benchmark.FullName] = [pscustomobject]@{
                BytesAllocatedPerOperation = [long]$benchmark.Memory.BytesAllocatedPerOperation
                Gen2Collections = [long]$benchmark.Memory.Gen2Collections
            }
        }
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($budget in $configuration.budgets) {
    if (-not $actual.ContainsKey($budget.benchmark)) {
        $failures.Add("Missing allocation result for $($budget.benchmark).")
        continue
    }

    $measurement = $actual[$budget.benchmark]
    $allocated = $measurement.BytesAllocatedPerOperation
    $maximum = [long]$budget.maximumBytesPerOperation
    if ($allocated -gt $maximum) {
        $failures.Add("$($budget.benchmark) allocated $allocated B/op; budget is $maximum B/op.")
    }
    else {
        Write-Host "$($budget.benchmark): $allocated B/op <= $maximum B/op"
    }

    if ($budget.PSObject.Properties.Name -contains "maximumGen2Collections") {
        $maximumGen2 = [long]$budget.maximumGen2Collections
        if ($measurement.Gen2Collections -gt $maximumGen2) {
            $failures.Add(
                "$($budget.benchmark) recorded $($measurement.Gen2Collections) Gen2 collections; " +
                "budget is $maximumGen2.")
        }
        else {
            Write-Host (
                "$($budget.benchmark): $($measurement.Gen2Collections) Gen2 collections <= $maximumGen2")
        }
    }
}

if ($failures.Count -ne 0) {
    throw "Allocation budget verification failed:`n$($failures -join "`n")"
}

Write-Host "Verified $($configuration.budgets.Count) allocation budgets for $($configuration.environment)."
