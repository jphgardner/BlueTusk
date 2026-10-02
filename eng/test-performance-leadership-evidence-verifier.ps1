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

function Test-RejectedEvidence
{
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][scriptblock] $Mutate,
        [Parameter(Mandatory)][string] $ExpectedMessage,
        [ValidateSet('Core', 'ContinuousGraphPreview')][string] $Scope = 'Core'
    )

    $changed = $(if ($Scope -eq 'Core') { $validJson } else { $graphJson }) | ConvertFrom-Json -AsHashtable
    & $Mutate $changed
    $path = Join-Path $temporaryRoot "$Name.json"
    $changed | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
    $failure = $null
    try
    {
        & $verifierPath -EvidencePath $path -ContractPath $contractPath -ExpectedCommit $commit -Scope $Scope | Out-Null
    }
    catch
    {
        $failure = $_.Exception.Message
    }
    if ($null -eq $failure -or -not $failure.Contains($ExpectedMessage))
    {
        throw "Case '$Name' expected '$ExpectedMessage'; received '$failure'."
    }
    $script:rejectionCount++
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
    # These are synthetic verifier fixtures, never captured benchmark evidence.
    'Synthetic consolidated report fixture.' |
        Set-Content -LiteralPath (Join-Path $temporaryRoot 'report.md') -Encoding utf8
    'Synthetic verifier self-test log fixture.' |
        Set-Content -LiteralPath (Join-Path $temporaryRoot 'self-tests.log') -Encoding utf8
    $environments = @($contract.environments.os | Sort-Object | ForEach-Object {
        $os = $_
        $manifestPath = Join-Path $temporaryRoot "$os-environment.json"
        $samplesPath = Join-Path $temporaryRoot "$os-samples.json"
        $images = @('postgres@sha256:' + ('e' * 64))
        [ordered]@{
            sourceCommit = $commit
            os = $os
            architecture = 'x64'
            containerImageDigests = $images
        } | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
        '{"fixture":"synthetic verifier test, not benchmark measurements"}' |
            Set-Content -LiteralPath $samplesPath -Encoding utf8
        [ordered]@{
            os = $os
            architecture = 'x64'
            sourceCommit = $commit
            environmentManifestPath = "$os-environment.json"
            environmentManifestSha256 = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
            rawSamplesPath = "$os-samples.json"
            rawSamplesSha256 = (Get-FileHash $samplesPath -Algorithm SHA256).Hash.ToLowerInvariant()
            containerImageDigests = $images
        }
    })
    $allComparisons = $comparisons.ToArray()
    $graphComparisons = @($allComparisons | Where-Object { $_.workloadKey.Split('|')[1] -eq 'ContinuousGraph' })
    $evidence = [ordered]@{
        schemaVersion = 3
        release = $contract.release
        scope = 'Core'
        sourceCommit = $commit
        confidenceLevel = 0.95
        consolidatedReportPath = 'report.md'
        consolidatedReportSha256 = (Get-FileHash (Join-Path $temporaryRoot 'report.md') -Algorithm SHA256).Hash.ToLowerInvariant()
        verifierSelfTestsPath = 'self-tests.log'
        verifierSelfTestsSha256 = (Get-FileHash (Join-Path $temporaryRoot 'self-tests.log') -Algorithm SHA256).Hash.ToLowerInvariant()
        environments = $environments
        comparisons = @($allComparisons | Where-Object { $_.workloadKey.Split('|')[1] -ne 'ContinuousGraph' })
    }
    $validPath = Join-Path $temporaryRoot 'valid.json'
    $validJson = $evidence | ConvertTo-Json -Depth 12
    $validJson | Set-Content -LiteralPath $validPath -Encoding utf8
    & $verifierPath -EvidencePath $validPath -ContractPath $contractPath -ExpectedCommit $commit | Out-Null
    $graphEvidence = $validJson | ConvertFrom-Json -AsHashtable
    $graphEvidence.scope = 'ContinuousGraphPreview'
    $graphEvidence.comparisons = $graphComparisons
    $graphPath = Join-Path $temporaryRoot 'graph-preview.json'
    $graphJson = $graphEvidence | ConvertTo-Json -Depth 12
    $graphJson | Set-Content -LiteralPath $graphPath -Encoding utf8
    & $verifierPath -EvidencePath $graphPath -ContractPath $contractPath -ExpectedCommit $commit -Scope ContinuousGraphPreview | Out-Null

    $script:rejectionCount = 0
    Test-RejectedEvidence 'wrong-scope' {
        param($changed)
        $changed.scope = 'ContinuousGraphPreview'
    } 'scope, or confidence'
    Test-RejectedEvidence 'graph-cannot-substitute-for-core' {
        param($changed)
        $changed.comparisons[0] = $graphComparisons[0]
    } 'duplicate or outside'
    Test-RejectedEvidence 'missing-graph-workload' {
        param($changed)
        $changed.comparisons = @($changed.comparisons | Select-Object -Skip 1)
    } 'Expected exactly' -Scope ContinuousGraphPreview
    Test-RejectedEvidence 'graph-trusted-cost-regression' {
        param($changed)
        $changed.comparisons[0].metrics.p95.candidate = 20.0
        $changed.comparisons[0].metrics.p95.candidateCiUpper = 21.0
    } "failed 'p95'" -Scope ContinuousGraphPreview
    Test-RejectedEvidence 'bad-ratio' {
        param($changed)
        $changed.comparisons[0].metrics.mean.candidate = 100.0
        $changed.comparisons[0].metrics.mean.candidateCiUpper = 101.0
    } "failed 'mean'"
    Test-RejectedEvidence 'missing-workload' {
        param($changed)
        $changed.comparisons = @($changed.comparisons | Select-Object -Skip 1)
    } 'Expected exactly'
    Test-RejectedEvidence 'duplicate-workload' {
        param($changed)
        $changed.comparisons[1] = $changed.comparisons[0]
    } 'duplicate or outside'
    Test-RejectedEvidence 'old-schema' {
        param($changed)
        $changed.schemaVersion = 1
    } 'identity, release, commit'
    Test-RejectedEvidence 'previous-release' {
        param($changed)
        $changed.release = '1.0.0'
    } 'identity, release, commit'
    Test-RejectedEvidence 'wrong-commit' {
        param($changed)
        $changed.sourceCommit = ('f' * 40)
    } 'identity, release, commit'
    Test-RejectedEvidence 'missing-artifact' {
        param($changed)
        $changed.environments[0].rawSamplesPath = 'absent.json'
    } 'is missing'
    Test-RejectedEvidence 'incorrect-digest' {
        param($changed)
        $changed.environments[0].rawSamplesSha256 = ('a' * 64)
    } 'does not match its SHA-256'
    Test-RejectedEvidence 'relative-traversal' {
        param($changed)
        $changed.consolidatedReportPath = '../report.md'
    } 'normalized relative path'
    Test-RejectedEvidence 'absolute-path' {
        param($changed)
        $changed.consolidatedReportPath = $validPath
    } 'normalized relative path'
    Test-RejectedEvidence 'alternate-path-separator' {
        param($changed)
        $changed.consolidatedReportPath = 'nested\report.md'
    } 'normalized relative path'
    Test-RejectedEvidence 'environment-images-mismatch' {
        param($changed)
        $changed.environments[0].containerImageDigests = @('postgres@sha256:' + ('f' * 64))
    } 'does not match the candidate, platform, or images'
    foreach ($invalid in @('NaN', 'Infinity', '-Infinity', '90', $null, $true))
    {
        Test-RejectedEvidence "invalid-number-$script:rejectionCount" {
            param($changed)
            $changed.comparisons[0].metrics.mean.candidate = $invalid
        } 'finite JSON number'
    }
    Test-RejectedEvidence 'negative-ungated-counter' {
        param($changed)
        $changed.comparisons[0].metrics.gcCounters.candidate = -1
    } "negative 'gcCounters'"
    Test-RejectedEvidence 'invalid-confidence-bound' {
        param($changed)
        $changed.comparisons[0].metrics.mean.candidateCiUpper = 'NaN'
    } 'finite JSON number'
    Test-RejectedEvidence 'confidence-tie' {
        param($changed)
        $changed.comparisons[0].metrics.mean.candidateCiUpper = 99.0
    } "failed 'mean'"

    Write-Output "Performance leadership evidence verifier self-test passed: independent Core and Graph preview synthetic fixtures and $script:rejectionCount rejected fixtures."
}
finally
{
    if ((Test-Path -LiteralPath $temporaryRoot) -and
        $temporaryRoot.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase))
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
