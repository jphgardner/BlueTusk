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
if (@($report.Benchmarks).Count -ne 81) {
    throw 'Portable Graph qualification requires all 81 benchmark cases.'
}
$profile = Get-Content -LiteralPath (Join-Path $CapturePath 'plans/authorization.json') -Raw | ConvertFrom-Json
if ($profile.rowLevelSecurityVerified -ne $true -or
    $profile.authorizedLease -ne $true -or $profile.postgreSqlVersion -cnotmatch '^18\.6(?:\s|$)') {
    throw 'Portable Graph qualification requires the authorized lease and active RLS on PostgreSQL 18.6.'
}
$log = Get-Content -LiteralPath (Join-Path $CapturePath 'matrix.log') -Raw
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
$rules = $contract.comparisonRules
foreach ($edges in $contract.workloads.ContinuousGraph.edgeCounts)
{
    foreach ($top in $contract.workloads.ContinuousGraph.topN)
    {
        $parameters = "EdgeCount=$edges&TopN=$top"
        $checks = [regex]::Matches($log,
            "Authorized lease verified: edges=$edges; top=$top; authorizations=(\d+)")
        if ($checks.Count -lt 2 -or @($checks | Where-Object { [long]$_.Groups[1].Value -lt 20 }).Count) {
            throw "Missing per-read subscriber authorization evidence for $parameters."
        }
        $repair = Get-Measurement 'FullRepairUpdateAsync' $parameters
        $trusted = Get-Measurement 'TrustedUpdateAsync' $parameters
        $trustedLatency = $trusted.P95 / $repair.P95
        $trustedAllocation = $trusted.Allocated / $repair.Allocated
        $trustedLimit = $rules.trustedCdcMaximumFullRequeryRatio
        if ($trustedLatency -gt $trustedLimit -or $trustedAllocation -gt $trustedLimit)
        {
            $failures.Add(('TrustedUpdateAsync ({0}) uses {1:P1} of full-repair P95 and {2:P1} of allocation, limit {3:P0}.' -f
                $parameters, $trustedLatency, $trustedAllocation, $trustedLimit))
        }
        $scoped = Get-Measurement 'ScopedUpdateAsync' $parameters
        $scopedLatency = $scoped.P95 / $repair.P95
        $scopedAllocation = $scoped.Allocated / $repair.Allocated
        if ($scopedLatency -ge $rules.authoritativeDeltaMaximumP95Ratio -or
            $scopedAllocation -gt $rules.authoritativeDeltaMaximumAllocationRatio)
        {
            $failures.Add(('ScopedUpdateAsync ({0}) uses {1:P1} of full-repair P95 and {2:P1} of allocation. P95 must be below {3:P0} and allocation at most {4:P0}.' -f
                $parameters, $scopedLatency, $scopedAllocation, $rules.authoritativeDeltaMaximumP95Ratio,
                $rules.authoritativeDeltaMaximumAllocationRatio))
        }
        if ($scopedLatency -gt $rules.authoritativeDeltaTargetP95Ratio)
        {
            Write-Output ('ScopedUpdateAsync ({0}) uses {1:P1} of full-repair P95. The tuning target is {2:P0}.' -f
                $parameters, $scopedLatency, $rules.authoritativeDeltaTargetP95Ratio)
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
