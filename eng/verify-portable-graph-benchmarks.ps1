[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CapturePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][ValidateSet('windows', 'linux')][string] $ExpectedOs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$environment = Get-Content -LiteralPath (Join-Path $CapturePath 'environment.json') -Raw | ConvertFrom-Json
$tracks = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-tracks.json') -Raw | ConvertFrom-Json
if ($environment.sourceCommit -cne $ExpectedCommit -or
    $environment.os -cne $ExpectedOs -or $environment.architecture -cne 'x64' -or
    $tracks.graph.portablePostgreSqlImage -cnotin @($environment.containerImageDigests))
{
    throw 'The portable Graph measurements must identify the exact candidate, platform, and pinned fixture.'
}
$results = Join-Path $CapturePath 'benchmarks/results'
$reportPath = Join-Path $results 'BlueTusk.Benchmarks.ContinuousGraphBenchmarks-report-full.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$contract = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-leadership-contract.json') -Raw |
    ConvertFrom-Json
$resources = Get-Content -LiteralPath (Join-Path $CapturePath 'plans/benchmark-process-resources.json') -Raw |
    ConvertFrom-Json
if (-not [double]::IsFinite([double]$resources.benchmarkProcessCpuMilliseconds) -or
    [double]$resources.benchmarkProcessCpuMilliseconds -le 0 -or
    [long]$resources.benchmarkProcessPeakRssBytes -le 0)
{
    throw 'Missing benchmark process CPU or peak resident memory evidence.'
}

function Get-Measurement([string] $Method, [string] $Parameters)
{
    $matches = @($report.Benchmarks | Where-Object { $_.Method -ceq $Method -and $_.Parameters -ceq $Parameters })
    if ($matches.Count -ne 1 -or $null -eq $matches[0].Statistics -or $matches[0].Statistics.N -lt 3)
    {
        throw "Missing or insufficient benchmark samples for '$Method' ($Parameters)."
    }
    $samples = @($matches[0].Measurements | Where-Object {
        $_.IterationMode -ceq 'Workload' -and $_.IterationStage -ceq 'Actual'
    })
    if ($samples.Count -lt 3 -or @($samples | Where-Object {
        $_.Operations -le 0 -or -not [double]::IsFinite([double]$_.Nanoseconds) -or $_.Nanoseconds -le 0
    }).Count -gt 0)
    {
        throw "Missing or invalid raw workload samples for '$Method' ($Parameters)."
    }

    $p95 = [double]$matches[0].Statistics.Percentiles.P95
    $allocated = [double]$matches[0].Memory.BytesAllocatedPerOperation
    if (-not [double]::IsFinite($p95) -or $p95 -le 0 -or -not [double]::IsFinite($allocated) -or $allocated -le 0)
    {
        throw "Invalid P95 or allocation for '$Method' ($Parameters)."
    }
    return @{ P95 = $p95; Allocated = $allocated }
}

function Get-PlanNodes($Node)
{
    $Node
    if ($null -ne $Node.PSObject.Properties['Plans'])
    {
        foreach ($child in $Node.Plans) { Get-PlanNodes $child }
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($edges in $contract.workloads.ContinuousGraph.edgeCounts)
{
    foreach ($top in $contract.workloads.ContinuousGraph.topN)
    {
        $parameters = "EdgeCount=$edges&TopN=$top"
        $repair = Get-Measurement 'FullRepairUpdateAsync' $parameters
        foreach ($tier in @(
            @{ Method = 'TrustedUpdateAsync'; Limit = $contract.comparisonRules.trustedCdcMaximumFullRequeryRatio },
            @{ Method = 'ScopedUpdateAsync'; Limit = $contract.comparisonRules.authoritativeDeltaMaximumFullRequeryRatio }))
        {
            $delta = Get-Measurement $tier.Method $parameters
            $latencyRatio = $delta.P95 / $repair.P95
            $allocationRatio = $delta.Allocated / $repair.Allocated
            if ($latencyRatio -gt $tier.Limit -or $allocationRatio -gt $tier.Limit)
            {
                $failures.Add(('{0} ({1}) uses {2:P1} of full-repair P95 and {3:P1} of allocation, limit {4:P0}.' -f
                    $tier.Method, $parameters, $latencyRatio, $allocationRatio, $tier.Limit))
            }
        }
        foreach ($method in @('PreparedSelectiveTraversalAsync', 'TypedSelectiveTraversalAsync', 'TypedBoundedTraversalAsync'))
        {
            $null = Get-Measurement $method $parameters
        }
        foreach ($shape in @('selective', 'selective-undirected', 'high-degree', 'bounded'))
        {
            $plan = Get-Content -LiteralPath (Join-Path $CapturePath "plans/$shape-$edges-$top.json") -Raw |
                ConvertFrom-Json -NoEnumerate
            if ($plan.Count -ne 1 -or $null -eq $plan[0].Plan -or $null -eq $plan[0].PSObject.Properties['Execution Time'])
            {
                throw "Invalid PostgreSQL execution plan for '$shape' ($parameters)."
            }
            if ($edges -ge 100000 -and $shape.StartsWith('selective', [StringComparison]::Ordinal))
            {
                $edgeScans = @(Get-PlanNodes $plan[0].Plan | Where-Object {
                    $null -ne $_.PSObject.Properties['Relation Name'] -and
                    $_.'Relation Name' -ceq 'bluetusk_benchmark_continuous_friendships'
                })
                if ($edgeScans.Count -eq 0 -or @($edgeScans | Where-Object {
                    $_.'Node Type' -cnotin @('Index Scan', 'Index Only Scan', 'Bitmap Heap Scan') -or
                    $_.'Actual Rows' * $_.'Actual Loops' -gt 32
                }).Count -ne 0)
                {
                    throw "Selective traversal '$shape' must use an endpoint index for $parameters."
                }
            }
        }
    }
}

foreach ($kind in @('latency', 'allocation'))
{
    $budget = Get-Content -LiteralPath (Join-Path $root "benchmarks/$kind-budgets.json") -Raw | ConvertFrom-Json
    $budget.budgets = @($budget.budgets | Where-Object {
        $_.benchmark.StartsWith('BlueTusk.Benchmarks.ContinuousGraphBenchmarks.', [StringComparison]::Ordinal)
    })
    if ($budget.budgets.Count -ne 3) { throw "Expected three existing Graph $kind budgets." }
    foreach ($entry in $budget.budgets)
    {
        $null = Get-Measurement $entry.benchmark.Split('.')[-1] $entry.parameters
    }
    $graphBudget = New-TemporaryFile
    try
    {
        $budget | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $graphBudget.FullName -Encoding utf8NoBOM
        try
        {
            & (Join-Path $PSScriptRoot "verify-$kind-budgets.ps1") -BudgetFile $graphBudget.FullName -ReportPath $reportPath
        }
        catch
        {
            $failures.Add($_.Exception.Message)
        }
    }
    finally
    {
        Remove-Item -LiteralPath $graphBudget.FullName
    }
}
if ($failures.Count -gt 0)
{
    throw ($failures -join [Environment]::NewLine)
}
Write-Output 'Verified portable Graph traversal plans, delta cost ratios, and existing absolute budgets.'
