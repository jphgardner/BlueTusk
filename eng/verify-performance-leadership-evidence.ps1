[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidencePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedCommit,

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

function Assert-EvidenceArtifact
{
    param(
        [Parameter(Mandatory)][string] $RelativePath,
        [Parameter(Mandatory)][string] $Sha256,
        [Parameter(Mandatory)][string] $Description
    )

    Assert-Sha256 $Sha256 $Description
    if ([IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('\') -or $RelativePath.Contains(':') -or
        @($RelativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0)
    {
        throw "$Description must use a normalized relative path within the evidence directory."
    }

    $path = $evidenceRoot
    foreach ($segment in $RelativePath.Split('/'))
    {
        $path = Join-Path $path $segment
        if (-not (Test-Path -LiteralPath $path))
        {
            throw "$Description artifact '$RelativePath' is missing."
        }
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        {
            throw "$Description must not traverse a symbolic link or junction."
        }
    }
    if ($item.PSIsContainer -or $item.Length -eq 0)
    {
        throw "$Description must reference a nonempty file."
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $Sha256)
    {
        throw "$Description does not match its SHA-256 digest."
    }
    return $path
}

function Get-FiniteNumber
{
    param([AllowNull()][object] $Value, [Parameter(Mandatory)][string] $Description)

    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool] -or
        $Value -isnot [ValueType])
    {
        throw "$Description must be a finite JSON number."
    }
    $number = [double]$Value
    if ([double]::IsNaN($number) -or [double]::IsInfinity($number))
    {
        throw "$Description must be a finite JSON number."
    }
    return $number
}

function Assert-LowerBetter
{
    param(
        [Parameter(Mandatory)][object] $Comparison,
        [Parameter(Mandatory)][string] $Metric,
        [Parameter(Mandatory)][double] $MaximumRatio
    )

    $value = $Comparison.metrics.PSObject.Properties[$Metric].Value
    $candidate = Get-FiniteNumber $value.candidate "$Metric candidate"
    $reference = Get-FiniteNumber $value.reference "$Metric reference"
    $candidateUpper = Get-FiniteNumber $value.candidateCiUpper "$Metric candidate upper bound"
    $referenceLower = Get-FiniteNumber $value.referenceCiLower "$Metric reference lower bound"
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
    $candidate = Get-FiniteNumber $value.candidate "$Metric candidate"
    $reference = Get-FiniteNumber $value.reference "$Metric reference"
    $candidateLower = Get-FiniteNumber $value.candidateCiLower "$Metric candidate lower bound"
    $referenceUpper = Get-FiniteNumber $value.referenceCiUpper "$Metric reference upper bound"
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
$evidenceRoot = Split-Path -Parent (Resolve-Path -LiteralPath $EvidencePath).Path
$evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
if ($evidence.schemaVersion -ne 2 -or $evidence.release -ne $contract.release -or
    [string]$evidence.sourceCommit -cne $ExpectedCommit -or
    [double]$evidence.confidenceLevel -ne [double]$contract.comparisonRules.confidenceLevel)
{
    throw 'Performance evidence identity, release, commit, or confidence level is invalid.'
}

$null = Assert-EvidenceArtifact $evidence.consolidatedReportPath `
    $evidence.consolidatedReportSha256 'Consolidated report'
$null = Assert-EvidenceArtifact $evidence.verifierSelfTestsPath `
    $evidence.verifierSelfTestsSha256 'Verifier self-tests'

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
    $manifestPath = Assert-EvidenceArtifact $matches[0].environmentManifestPath `
        $matches[0].environmentManifestSha256 "$environmentName environment manifest"
    $null = Assert-EvidenceArtifact $matches[0].rawSamplesPath `
        $matches[0].rawSamplesSha256 "$environmentName raw samples"
    $digests = @($matches[0].containerImageDigests)
    if ($digests.Count -eq 0 -or $digests.Where({ $_ -notmatch '@sha256:[0-9a-f]{64}$' }).Count -ne 0)
    {
        throw "Environment '$environmentName' has missing or mutable container image evidence."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.sourceCommit -cne $ExpectedCommit -or
        $manifest.os -cne $environmentName -or $manifest.architecture -cne 'x64' -or
        @($manifest.containerImageDigests).Count -ne $digests.Count -or
        (($manifest.containerImageDigests | Sort-Object) -join "`n") -cne
            (($digests | Sort-Object) -join "`n"))
    {
        throw "Environment manifest '$environmentName' does not match the candidate, platform, or images."
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
        $value = $comparison.metrics.PSObject.Properties[[string]$metric].Value
        foreach ($provider in @('candidate', 'reference'))
        {
            $number = Get-FiniteNumber $value.PSObject.Properties[$provider].Value "$key $metric $provider"
            if ($number -lt 0)
            {
                throw "Workload '$key' has a negative '$metric' measurement."
            }
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
    "Verified retained artifacts and $($comparisons.Count) declared BlueTusk $($contract.release) performance comparisons for " +
    "$($environmentNames -join ' and ') at commit $($evidence.sourceCommit).")
