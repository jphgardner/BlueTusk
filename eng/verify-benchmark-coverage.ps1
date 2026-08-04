param(
    [string]$BenchmarkSourcePath = (Join-Path $PSScriptRoot "..\benchmarks\BlueTusk.Benchmarks"),
    [string]$BaselinePath = (Join-Path $PSScriptRoot "..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\results"),
    [int]$MinimumFixtureCount = 21,
    [int]$MinimumBenchmarkCount = 89
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$sourceBenchmarks = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$fixtureNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$benchmarkPattern = '(?ms)\[Benchmark(?:\([^\]]*\))?\](?:(?!\[Benchmark(?:\(|\])).)*?public\s+(?:async\s+)?[^\r\n(=]+\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\('

Get-ChildItem -LiteralPath $BenchmarkSourcePath -Filter "*Benchmarks.cs" | ForEach-Object {
    $source = Get-Content -LiteralPath $_.FullName -Raw
    $classMatch = [regex]::Match(
        $source,
        '(?m)^public\s+(?:sealed\s+)?class\s+(?<class>[A-Za-z_][A-Za-z0-9_]*Benchmarks)\b')
    if (-not $classMatch.Success) {
        throw "Could not find a public benchmark fixture in '$($_.FullName)'."
    }

    $className = $classMatch.Groups['class'].Value
    $null = $fixtureNames.Add($className)
    $methodMatches = [regex]::Matches($source, $benchmarkPattern)
    if ($methodMatches.Count -eq 0) {
        throw "Benchmark fixture '$className' declares no [Benchmark] methods."
    }

    foreach ($match in $methodMatches) {
        $null = $sourceBenchmarks.Add("BlueTusk.Benchmarks.$className.$($match.Groups['method'].Value)")
    }
}

$reported = @{}
$reportFiles = @(
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*-report-brief.json"
    Get-ChildItem -LiteralPath $BaselinePath -Filter "*MultiplexingComparisonBenchmarks-report-full.json"
)
$failures = [System.Collections.Generic.List[string]]::new()
foreach ($file in $reportFiles) {
    $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    $benchmarks = @($report.Benchmarks)
    if ($benchmarks.Count -eq 0) {
        $failures.Add("Benchmark report '$($file.Name)' is empty.")
        continue
    }

    foreach ($benchmark in $benchmarks) {
        $fullName = [string]$benchmark.FullName
        if ([string]::IsNullOrWhiteSpace($fullName)) {
            $failures.Add("Benchmark report '$($file.Name)' contains a result without FullName.")
            continue
        }

        if ($reported.ContainsKey($fullName)) {
            $failures.Add("Duplicate result for '$fullName' in '$($file.Name)' and '$($reported[$fullName])'.")
            continue
        }

        $statistics = $benchmark.Statistics
        $hasMean = $null -ne $statistics -and
            $null -ne $statistics.PSObject.Properties['Mean'] -and
            $null -ne $statistics.Mean
        $sampleCount = 0
        if ($null -ne $statistics -and
            $null -ne $statistics.PSObject.Properties['N'] -and
            $null -ne $statistics.N) {
            $sampleCount = [int]$statistics.N
        }
        elseif ($null -ne $statistics -and
            $null -ne $statistics.PSObject.Properties['OriginalValues'] -and
            $null -ne $statistics.OriginalValues) {
            $sampleCount = @($statistics.OriginalValues).Count
        }

        if (-not $hasMean -or
            $sampleCount -le 0 -or
            [double]$statistics.Mean -le 0 -or
            -not [double]::IsFinite([double]$statistics.Mean)) {
            $failures.Add("Benchmark result '$fullName' in '$($file.Name)' has no valid measured statistics.")
            continue
        }

        $reported[$fullName] = $file.Name
    }
}

$reportedMethods = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($actual in $reported.Keys) {
    $null = $reportedMethods.Add(([regex]::Replace([string]$actual, '\s*\(.*\)$', '')))
}

foreach ($expected in $sourceBenchmarks) {
    if (-not $reportedMethods.Contains($expected)) {
        $failures.Add("No checked-in measured result exists for '$expected'.")
    }
}

foreach ($actual in $reported.Keys) {
    $methodName = [regex]::Replace([string]$actual, '\s*\(.*\)$', '')
    if (-not $sourceBenchmarks.Contains($methodName)) {
        $failures.Add("Checked-in result '$actual' has no matching [Benchmark] method.")
    }
}

if ($fixtureNames.Count -lt $MinimumFixtureCount) {
    $failures.Add("Found $($fixtureNames.Count) benchmark fixtures; at least $MinimumFixtureCount are required.")
}

if ($reported.Count -lt $MinimumBenchmarkCount) {
    $failures.Add("Found $($reported.Count) valid benchmark results; at least $MinimumBenchmarkCount are required.")
}

if ($failures.Count -ne 0) {
    throw "Benchmark coverage verification failed:`n$($failures -join "`n")"
}

Write-Host (
    "Verified {0} measured benchmark results cover all [Benchmark] methods across {1} fixtures; empty and stale reports are rejected." -f
    $reported.Count,
    $fixtureNames.Count)
