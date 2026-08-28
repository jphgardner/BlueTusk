[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidencePath,

    [string] $ContractPath = (Join-Path $PSScriptRoot 'performance-leadership-contract.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Sha256
{
    param([Parameter(Mandatory)][string] $Value, [Parameter(Mandatory)][string] $Description)
    if ($Value -notmatch '^[0-9a-f]{64}$')
    {
        throw "$Description must be a lowercase SHA-256 digest."
    }
}

function Assert-LowerBetter
{
    param(
        [Parameter(Mandatory)][object] $Comparison,
        [Parameter(Mandatory)][string] $Metric,
        [Parameter(Mandatory)][double] $MaximumRatio
    )

    $value = $Comparison.metrics.PSObject.Properties[$Metric].Value
    $candidate = [double]$value.candidate
    $reference = [double]$value.reference
    $candidateUpper = [double]$value.candidateCiUpper
    $referenceLower = [double]$value.referenceCiLower
    if ($candidate -lt 0 -or $reference -le 0 -or
        $candidateUpper -lt $candidate -or $referenceLower -le 0 -or
        $referenceLower -gt $reference)
    {
        throw "Workload '$($Comparison.workloadKey)' has invalid '$Metric' samples or confidence bounds."
    }

    $ratio = $candidate / $reference
    $confidenceRatio = $candidateUpper / $referenceLower
    if ($ratio -gt $MaximumRatio -or $confidenceRatio -gt $MaximumRatio)
    {
        throw (
            "Workload '$($Comparison.workloadKey)' failed '$Metric': " +
            "ratio=$([Math]::Round($ratio, 6)); 95% confidence ratio=" +
            "$([Math]::Round($confidenceRatio, 6)); maximum=$MaximumRatio.")
    }
}

function Assert-HigherBetter
{
    param(
        [Parameter(Mandatory)][object] $Comparison,
        [Parameter(Mandatory)][string] $Metric,
        [Parameter(Mandatory)][double] $MinimumRatio
    )

    $value = $Comparison.metrics.PSObject.Properties[$Metric].Value
    $candidate = [double]$value.candidate
    $reference = [double]$value.reference
    $candidateLower = [double]$value.candidateCiLower
    $referenceUpper = [double]$value.referenceCiUpper
    if ($candidate -le 0 -or $reference -le 0 -or
        $candidateLower -le 0 -or $candidateLower -gt $candidate -or
        $referenceUpper -lt $reference)
    {
        throw "Workload '$($Comparison.workloadKey)' has invalid '$Metric' samples or confidence bounds."
    }

    $ratio = $candidate / $reference
    $confidenceRatio = $candidateLower / $referenceUpper
    if ($ratio -lt $MinimumRatio -or $confidenceRatio -lt $MinimumRatio)
    {
        throw (
            "Workload '$($Comparison.workloadKey)' failed '$Metric': " +
            "ratio=$([Math]::Round($ratio, 6)); 95% confidence ratio=" +
            "$([Math]::Round($confidenceRatio, 6)); minimum=$MinimumRatio.")
    }
}

function Add-Expected
{
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()]
        [Collections.Generic.Dictionary[string, string]] $Expected,
        [Parameter(Mandatory)][string] $Key,
        [Parameter(Mandatory)][string] $Mode
    )
    if (-not $Expected.TryAdd($Key, $Mode))
    {
        throw "Duplicate generated performance workload '$Key'."
    }
}

$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
$evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
if ($evidence.schemaVersion -ne 1 -or $evidence.release -ne $contract.release -or
    [string]$evidence.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
    [double]$evidence.confidenceLevel -ne [double]$contract.comparisonRules.confidenceLevel)
{
    throw 'Performance evidence identity, release, commit, or confidence level is invalid.'
}

Assert-Sha256 ([string]$evidence.consolidatedReportSha256) 'Consolidated report'
Assert-Sha256 ([string]$evidence.verifierSelfTestsSha256) 'Verifier self-tests'

$environmentNames = @($contract.environments.os | Sort-Object)
$evidenceEnvironments = @($evidence.environments)
if ($evidenceEnvironments.Count -ne $environmentNames.Count)
{
    throw 'Evidence must contain exactly one manifest for each required environment.'
}
foreach ($environmentName in $environmentNames)
{
    $matches = @($evidenceEnvironments | Where-Object { $_.os -eq $environmentName })
    if ($matches.Count -ne 1 -or $matches[0].architecture -ne 'x64' -or
        $matches[0].sourceCommit -ne $evidence.sourceCommit)
    {
        throw "Environment evidence for '$environmentName' is missing or bound to another candidate."
    }
    Assert-Sha256 ([string]$matches[0].environmentManifestSha256) "$environmentName environment manifest"
    Assert-Sha256 ([string]$matches[0].rawSamplesSha256) "$environmentName raw samples"
    $digests = @($matches[0].containerImageDigests)
    if ($digests.Count -eq 0 -or $digests.Where({ $_ -notmatch '@sha256:[0-9a-f]{64}$' }).Count -ne 0)
    {
        throw "Environment '$environmentName' has missing or mutable container image evidence."
    }
}

$expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($os in $environmentNames)
{
    foreach ($feature in $contract.workloads.Provider.features)
    {
        foreach ($concurrency in $contract.workloads.Provider.concurrency)
        {
            foreach ($variant in $contract.workloads.Provider.variants)
            {
                Add-Expected $expected "$os|Provider|$feature|c=$concurrency|variant=$variant" 'same-runtime'
            }
        }
    }
    foreach ($changes in $contract.workloads.Streams.transactionChanges)
    {
        foreach ($scenario in $contract.workloads.Streams.scenarios)
        {
            Add-Expected $expected "$os|Streams|changes=$changes|scenario=$scenario" 'cross-runtime'
        }
        foreach ($spoolBytes in $contract.workloads.Streams.spoolBytes)
        {
            Add-Expected $expected "$os|Streams|changes=$changes|spoolBytes=$spoolBytes" 'cross-runtime'
        }
    }
    foreach ($mutations in $contract.workloads.Sync.mutationCounts)
    {
        foreach ($destination in $contract.workloads.Sync.destinations)
        {
            Add-Expected $expected "$os|Sync|mutations=$mutations|destination=$destination" 'cross-runtime'
        }
    }
    foreach ($results in $contract.workloads.Live.resultCounts)
    {
        foreach ($subscribers in $contract.workloads.Live.subscriberCounts)
        {
            foreach ($scenario in $contract.workloads.Live.scenarios)
            {
                Add-Expected $expected "$os|Live|results=$results|subscribers=$subscribers|scenario=$scenario" 'cross-runtime'
            }
        }
    }
    foreach ($sources in $contract.workloads.ControlPlane.sourceCounts)
    {
        foreach ($clients in $contract.workloads.ControlPlane.apiClientCounts)
        {
            Add-Expected $expected "$os|ControlPlane|sources=$sources|clients=$clients" 'unique'
        }
    }
    foreach ($edges in $contract.workloads.ContinuousGraph.edgeCounts)
    {
        foreach ($topN in $contract.workloads.ContinuousGraph.topN)
        {
            foreach ($tier in $contract.workloads.ContinuousGraph.tiers)
            {
                $mode = switch ($tier)
                {
                    'trusted-cdc' { 'graph-trusted' }
                    'authoritative-delta' { 'graph-authoritative' }
                    'authoritative-repair' { 'same-runtime' }
                    default { throw "Unknown graph tier '$tier'." }
                }
                foreach ($scenario in $contract.workloads.ContinuousGraph.scenarios)
                {
                    Add-Expected $expected "$os|ContinuousGraph|edges=$edges|topN=$topN|tier=$tier|scenario=$scenario" $mode
                }
            }
        }
    }
    foreach ($family in @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph'))
    {
        Add-Expected $expected "$os|$family|primary-hot-path" 'unique-primary'
    }
}

$comparisons = @($evidence.comparisons)
if ($comparisons.Count -ne $expected.Count)
{
    throw "Expected exactly $($expected.Count) workload comparisons; found $($comparisons.Count)."
}
$observed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$rules = $contract.comparisonRules
foreach ($comparison in $comparisons)
{
    $key = [string]$comparison.workloadKey
    if (-not $observed.Add($key) -or -not $expected.ContainsKey($key))
    {
        throw "Performance workload '$key' is duplicate or outside the exact contract."
    }
    $mode = $expected[$key]
    if ([string]$comparison.mode -ne $mode -or
        [double]$comparison.confidenceLevel -ne [double]$rules.confidenceLevel)
    {
        throw "Workload '$key' has the wrong comparison mode or confidence level."
    }
    foreach ($metric in $contract.requiredMetrics)
    {
        if ($null -eq $comparison.metrics.PSObject.Properties[[string]$metric])
        {
            throw "Workload '$key' is missing required metric '$metric'."
        }
    }

    switch ($mode)
    {
        'same-runtime'
        {
            foreach ($metric in @('mean', 'p95', 'p99', 'allocatedBytes'))
            {
                Assert-LowerBetter $comparison $metric ([double]$rules.sameRuntimeMaximumRatio)
            }
        }
        'cross-runtime'
        {
            Assert-HigherBetter $comparison 'throughput' ([double]$rules.crossRuntimeMinimumThroughputRatio)
            foreach ($metric in @('p95', 'p99', 'cpuPerEvent', 'peakRss'))
            {
                Assert-LowerBetter $comparison $metric ([double]$rules.crossRuntimeMaximumCostRatio)
            }
        }
        'unique'
        {
            foreach ($metric in @('mean', 'p95', 'p99', 'allocatedBytes', 'cpuPerEvent', 'peakRss'))
            {
                Assert-LowerBetter $comparison $metric ([double]$rules.uniqueWorkloadMaximumRegressionRatio)
            }
        }
        'unique-primary'
        {
            Assert-LowerBetter $comparison 'p95' ([double]$rules.primaryHotPathMaximumP95Ratio)
            Assert-LowerBetter $comparison 'allocatedBytes' ([double]$rules.primaryHotPathMaximumAllocationRatio)
        }
        'graph-trusted'
        {
            Assert-LowerBetter $comparison 'p95' ([double]$rules.trustedCdcMaximumFullRequeryRatio)
            Assert-LowerBetter $comparison 'allocatedBytes' ([double]$rules.trustedCdcMaximumFullRequeryRatio)
        }
        'graph-authoritative'
        {
            Assert-LowerBetter $comparison 'p95' ([double]$rules.authoritativeDeltaMaximumFullRequeryRatio)
            Assert-LowerBetter $comparison 'allocatedBytes' ([double]$rules.authoritativeDeltaMaximumFullRequeryRatio)
        }
    }
}

Write-Output (
    "Verified $($comparisons.Count) BlueTusk 1.1 performance comparisons for " +
    "$($environmentNames -join ' and ') at commit $($evidence.sourceCommit).")
