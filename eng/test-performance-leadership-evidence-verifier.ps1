[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$contractPath = Join-Path $PSScriptRoot 'performance-leadership-contract.json'
$verifierPath = Join-Path $PSScriptRoot 'verify-performance-leadership-evidence.ps1'
$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bluetusk-performance-verifier-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

function New-MetricSet
{
    param([Parameter(Mandatory)][string] $Mode)
    $lowerCandidate = switch ($Mode)
    {
        'graph-trusted' { 5.0 }
        'graph-authoritative' { 25.0 }
        'unique-primary' { 70.0 }
        default { 90.0 }
    }
    $lowerUpper = switch ($Mode)
    {
        'graph-trusted' { 6.0 }
        'graph-authoritative' { 30.0 }
        'unique-primary' { 75.0 }
        default { 91.0 }
    }
    $metrics = [ordered]@{}
    foreach ($metric in $contract.requiredMetrics)
    {
        if ($metric -eq 'throughput')
        {
            $metrics[$metric] = [ordered]@{
                candidate = 112.0
                reference = 100.0
                candidateCiLower = 110.0
                referenceCiUpper = 100.0
            }
        }
        elseif ($metric -eq 'gcCounters')
        {
            $metrics[$metric] = [ordered]@{ candidate = 0.0; reference = 0.0 }
        }
        else
        {
            $metrics[$metric] = [ordered]@{
                candidate = $lowerCandidate
                reference = 100.0
                candidateCiUpper = $lowerUpper
                referenceCiLower = 99.0
            }
        }
    }
    return $metrics
}

function Add-Comparison
{
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()]
        [Collections.Generic.List[object]] $List,
        [Parameter(Mandatory)][string] $Key,
        [Parameter(Mandatory)][string] $Mode
    )
    $List.Add([ordered]@{
        workloadKey = $Key
        mode = $Mode
        confidenceLevel = 0.95
        metrics = New-MetricSet $Mode
    })
}

try
{
    $comparisons = [Collections.Generic.List[object]]::new()
    foreach ($os in @($contract.environments.os | Sort-Object))
    {
        foreach ($feature in $contract.workloads.Provider.features)
        {
            foreach ($concurrency in $contract.workloads.Provider.concurrency)
            {
                foreach ($variant in $contract.workloads.Provider.variants)
                {
                    Add-Comparison $comparisons "$os|Provider|$feature|c=$concurrency|variant=$variant" 'same-runtime'
                }
            }
        }
        foreach ($changes in $contract.workloads.Streams.transactionChanges)
        {
            foreach ($scenario in $contract.workloads.Streams.scenarios)
            {
                Add-Comparison $comparisons "$os|Streams|changes=$changes|scenario=$scenario" 'cross-runtime'
            }
            foreach ($spoolBytes in $contract.workloads.Streams.spoolBytes)
            {
                Add-Comparison $comparisons "$os|Streams|changes=$changes|spoolBytes=$spoolBytes" 'cross-runtime'
            }
        }
        foreach ($mutations in $contract.workloads.Sync.mutationCounts)
        {
            foreach ($destination in $contract.workloads.Sync.destinations)
            {
                Add-Comparison $comparisons "$os|Sync|mutations=$mutations|destination=$destination" 'cross-runtime'
            }
        }
        foreach ($results in $contract.workloads.Live.resultCounts)
        {
            foreach ($subscribers in $contract.workloads.Live.subscriberCounts)
            {
                foreach ($scenario in $contract.workloads.Live.scenarios)
                {
                    Add-Comparison $comparisons "$os|Live|results=$results|subscribers=$subscribers|scenario=$scenario" 'cross-runtime'
                }
            }
        }
        foreach ($sources in $contract.workloads.ControlPlane.sourceCounts)
        {
            foreach ($clients in $contract.workloads.ControlPlane.apiClientCounts)
            {
                Add-Comparison $comparisons "$os|ControlPlane|sources=$sources|clients=$clients" 'unique'
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
                    }
                    foreach ($scenario in $contract.workloads.ContinuousGraph.scenarios)
                    {
                        Add-Comparison $comparisons "$os|ContinuousGraph|edges=$edges|topN=$topN|tier=$tier|scenario=$scenario" $mode
                    }
                }
            }
        }
        foreach ($family in @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph'))
        {
            Add-Comparison $comparisons "$os|$family|primary-hot-path" 'unique-primary'
        }
    }

    $commit = '1234567890abcdef1234567890abcdef12345678'
    $evidence = [ordered]@{
        schemaVersion = 1
        release = '1.1.0'
        sourceCommit = $commit
        confidenceLevel = 0.95
        consolidatedReportSha256 = ('a' * 64)
        verifierSelfTestsSha256 = ('b' * 64)
        environments = @($contract.environments.os | Sort-Object | ForEach-Object {
            [ordered]@{
                os = $_
                architecture = 'x64'
                sourceCommit = $commit
                environmentManifestSha256 = ('c' * 64)
                rawSamplesSha256 = ('d' * 64)
                containerImageDigests = @('postgres@sha256:' + ('e' * 64))
            }
        })
        comparisons = $comparisons
    }
    $validPath = Join-Path $temporaryRoot 'valid.json'
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $validPath -Encoding utf8
    & $verifierPath -EvidencePath $validPath -ContractPath $contractPath | Out-Null

    $evidence.comparisons[0].metrics.mean.candidate = 100.0
    $ratioPath = Join-Path $temporaryRoot 'bad-ratio.json'
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ratioPath -Encoding utf8
    $failed = $false
    try { & $verifierPath -EvidencePath $ratioPath -ContractPath $contractPath | Out-Null }
    catch { $failed = $true }
    if (-not $failed) { throw 'The evidence verifier accepted a failed comparison ratio.' }

    $evidence.comparisons[0].metrics.mean.candidate = 90.0
    $evidence.comparisons = @($evidence.comparisons | Select-Object -Skip 1)
    $coveragePath = Join-Path $temporaryRoot 'missing-workload.json'
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $coveragePath -Encoding utf8
    $failed = $false
    try { & $verifierPath -EvidencePath $coveragePath -ContractPath $contractPath | Out-Null }
    catch { $failed = $true }
    if (-not $failed) { throw 'The evidence verifier accepted an incomplete workload matrix.' }

    Write-Output 'Performance leadership evidence verifier self-test passed.'
}
finally
{
    if ((Test-Path -LiteralPath $temporaryRoot) -and
        $temporaryRoot.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase))
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
