param(
    [string]$BudgetFile = (Join-Path $PSScriptRoot "..\benchmarks\allocation-budgets.json"),
    [string]$BaselinePath = (Join-Path $PSScriptRoot "..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\results")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$configuration = Get-Content -LiteralPath $BudgetFile -Raw | ConvertFrom-Json
$actual = @{}
Get-ChildItem -LiteralPath $BaselinePath -Filter "*-report-brief.json" | ForEach-Object {
    $report = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    foreach ($benchmark in $report.Benchmarks) {
        if ($null -ne $benchmark.Memory -and $null -ne $benchmark.Memory.BytesAllocatedPerOperation) {
            $actual[$benchmark.FullName] = [long]$benchmark.Memory.BytesAllocatedPerOperation
        }
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($budget in $configuration.budgets) {
    if (-not $actual.ContainsKey($budget.benchmark)) {
        $failures.Add("Missing allocation result for $($budget.benchmark).")
        continue
    }

    $allocated = $actual[$budget.benchmark]
    $maximum = [long]$budget.maximumBytesPerOperation
    if ($allocated -gt $maximum) {
        $failures.Add("$($budget.benchmark) allocated $allocated B/op; budget is $maximum B/op.")
    }
    else {
        Write-Host "$($budget.benchmark): $allocated B/op <= $maximum B/op"
    }
}

if ($failures.Count -ne 0) {
    throw "Allocation budget verification failed:`n$($failures -join "`n")"
}

Write-Host "Verified $($configuration.budgets.Count) allocation budgets for $($configuration.environment)."
