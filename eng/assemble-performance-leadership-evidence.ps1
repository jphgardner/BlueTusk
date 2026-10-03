[CmdletBinding()]
param(
    # The evidence directory: it holds windows/ and linux/, each generated and independently checked.
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $VerifierSelfTestLogPath,
    [string] $ContractPath = (Join-Path $PSScriptRoot 'performance-leadership-contract.json'),
    [string] $VariantMapPath = (Join-Path $PSScriptRoot 'performance-variant-map.json'),
    # Diagnostic assembly accepts partial coverage, diagnostic or synthetic inputs and a non-committed variant
    # map. It writes diagnostic-performance-leadership-evidence.json, never the readiness file name.
    [switch] $Diagnostic
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-Json([string] $Path)
{
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or $item.Length -le 0 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    { throw "Evidence file '$Path' must be a nonempty regular file." }
    return [IO.File]::ReadAllText($item.FullName) | ConvertFrom-Json -Depth 64
}
function Format-Ratio([double] $Value) { $Value.ToString('0.0000', [Globalization.CultureInfo]::InvariantCulture) }

$root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
if (((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
{ throw 'The evidence root must not be a link or junction.' }
$evidenceName = if ($Diagnostic) { 'diagnostic-performance-leadership-evidence.json' } else { 'performance-leadership-evidence.json' }
foreach ($name in @($evidenceName, 'consolidated-report.md', 'verifier-self-tests.log'))
{
    if (Test-Path -LiteralPath (Join-Path $root $name)) { throw "Assembly never overwrites '$name'." }
}
$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
& (Join-Path $PSScriptRoot 'verify-performance-leadership-contract.ps1') -ContractPath $ContractPath | Out-Null
$contractHash = Get-Sha256 $ContractPath
$mapHash = Get-Sha256 $VariantMapPath
$committedMapHash = Get-Sha256 (Join-Path $PSScriptRoot 'performance-variant-map.json')
$rules = $contract.comparisonRules
$tracks = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-tracks.json') -Raw | ConvertFrom-Json
$coreFamilies = @($tracks.stableFamilies)

# Mirror of the Core expansion in verify-performance-leadership-evidence.ps1.
function Get-ExpectedWorkloads([string] $Os)
{
    $expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $w = $contract.workloads
    foreach ($f in $w.Provider.features) { foreach ($c in $w.Provider.concurrency) { foreach ($v in $w.Provider.variants) {
        $expected.Add("$Os|Provider|$f|c=$c|variant=$v", 'same-runtime') } } }
    foreach ($changes in $w.Streams.transactionChanges)
    {
        foreach ($s in $w.Streams.scenarios) { $expected.Add("$Os|Streams|changes=$changes|scenario=$s", 'cross-runtime') }
        foreach ($b in $w.Streams.spoolBytes) { $expected.Add("$Os|Streams|changes=$changes|spoolBytes=$b", 'cross-runtime') }
    }
    foreach ($m in $w.Sync.mutationCounts) { foreach ($d in $w.Sync.destinations) {
        $expected.Add("$Os|Sync|mutations=$m|destination=$d", 'cross-runtime') } }
    foreach ($r in $w.Live.resultCounts) { foreach ($s in $w.Live.subscriberCounts) { foreach ($x in $w.Live.scenarios) {
        $expected.Add("$Os|Live|results=$r|subscribers=$s|scenario=$x", 'cross-runtime') } } }
    foreach ($s in $w.ControlPlane.sourceCounts) { foreach ($c in $w.ControlPlane.apiClientCounts) {
        $expected.Add("$Os|ControlPlane|sources=$s|clients=$c", 'unique') } }
    foreach ($family in $coreFamilies) { $expected.Add("$Os|$family|primary-hot-path", 'unique-primary') }
    return $expected
}

function Test-Comparison([object] $Comparison)
{
    # Same rules as the verifier; used only to write a readable verdict into the consolidated report.
    $failures = [Collections.Generic.List[string]]::new()
    function Lower([string] $Metric, [double] $Maximum)
    {
        $v = $Comparison.metrics.$Metric
        $ratio = [double]$v.candidate / [double]$v.reference
        $bound = [double]$v.candidateCiUpper / [double]$v.referenceCiLower
        if ([double]$v.reference -le 0 -or [double]$v.referenceCiLower -le 0 -or $ratio -gt $Maximum -or $bound -gt $Maximum)
        { $failures.Add("$Metric $(Format-Ratio $ratio) (95% bound $(Format-Ratio $bound)) > $Maximum") }
    }
    function Higher([string] $Metric, [double] $Minimum)
    {
        $v = $Comparison.metrics.$Metric
        $ratio = [double]$v.candidate / [double]$v.reference
        $bound = [double]$v.candidateCiLower / [double]$v.referenceCiUpper
        if ($ratio -lt $Minimum -or $bound -lt $Minimum)
        { $failures.Add("$Metric $(Format-Ratio $ratio) (95% bound $(Format-Ratio $bound)) < $Minimum") }
    }
    switch ($Comparison.mode)
    {
        'same-runtime' { foreach ($m in 'mean', 'p95', 'p99', 'allocatedBytes') { Lower $m ([double]$rules.sameRuntimeMaximumRatio) } }
        'cross-runtime'
        {
            Higher 'throughput' ([double]$rules.crossRuntimeMinimumThroughputRatio)
            foreach ($m in 'p95', 'p99', 'cpuPerEvent', 'peakRss') { Lower $m ([double]$rules.crossRuntimeMaximumCostRatio) }
        }
        'unique' { foreach ($m in 'mean', 'p95', 'p99', 'allocatedBytes', 'cpuPerEvent', 'peakRss') { Lower $m ([double]$rules.uniqueWorkloadMaximumRegressionRatio) } }
        'unique-primary'
        {
            Lower 'p95' ([double]$rules.primaryHotPathMaximumP95Ratio)
            Lower 'allocatedBytes' ([double]$rules.primaryHotPathMaximumAllocationRatio)
        }
        default { $failures.Add("unknown mode '$($Comparison.mode)'") }
    }
    return ,$failures.ToArray()
}

$environmentNames = @($contract.environments.os | Sort-Object)
$present = @($environmentNames | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Container })
if ($present.Count -eq 0 -or (-not $Diagnostic -and $present.Count -ne $environmentNames.Count))
{ throw 'Qualification assembly needs checked windows and linux evidence; diagnostic assembly needs at least one.' }

$environments = [Collections.Generic.List[object]]::new()
$comparisons = [Collections.Generic.List[object]]::new()
$reportRows = [Collections.Generic.List[string]]::new()
$failing = [Collections.Generic.List[string]]::new()
$missing = [Collections.Generic.List[string]]::new()
$anyDiagnostic = $false
$anySynthetic = $false
$crossOsProfiles = [Collections.Generic.HashSet[string]]::new()
foreach ($os in $present)
{
    $osRoot = Join-Path $root $os
    $summaryPath = Join-Path $osRoot 'summary.json'
    $rawPath = Join-Path $osRoot 'raw-samples.json'
    $checkPath = Join-Path $osRoot 'check-report.json'
    $manifestPath = Join-Path $osRoot 'environment-manifest.json'
    $summary = Read-Json $summaryPath
    $check = Read-Json $checkPath
    $manifest = Read-Json $manifestPath
    $raw = Read-Json $rawPath
    $summaryHash = Get-Sha256 $summaryPath
    $rawHash = Get-Sha256 $rawPath
    if ($check.schemaVersion -ne 1 -or $check.evidenceKind -cne 'bluetusk-performance-check' -or $check.result -cne 'consistent' -or
        $check.sourceCommit -cne $ExpectedCommit -or $check.os -cne $os -or
        $check.summary.sha256 -cne $summaryHash -or $check.rawSamples.sha256 -cne $rawHash -or
        $check.comparisons -ne @($summary.comparisons).Count -or $check.variantMapSha256 -cne $summary.variantMap.sha256)
    { throw "The independent check for '$os' is missing, failed, or bound to a different summary." }
    if ($summary.schemaVersion -ne 1 -or $summary.evidenceKind -cne 'bluetusk-performance-summary' -or
        $summary.sourceCommit -cne $ExpectedCommit -or $summary.os -cne $os -or $summary.release -cne $contract.release -or
        $summary.scope -cne 'Core' -or $summary.contract.sha256 -cne $contractHash -or $summary.rawSamples.sha256 -cne $rawHash -or
        $summary.diagnostic -isnot [bool] -or $summary.synthetic -isnot [bool] -or
        $check.diagnostic -ne $summary.diagnostic -or $check.synthetic -ne $summary.synthetic)
    { throw "Summary '$os' identity, contract, or raw-sample binding is invalid." }
    if ($summary.variantMap.sha256 -cne $mapHash)
    { throw "Summary '$os' was generated with a variant map other than '$VariantMapPath'." }
    $anyDiagnostic = $anyDiagnostic -or $summary.diagnostic
    $anySynthetic = $anySynthetic -or $summary.synthetic
    $null = $crossOsProfiles.Add([string]$summary.variantMap.crossOsProfile)
    if (-not $Diagnostic -and ($summary.diagnostic -or $summary.synthetic -or $mapHash -cne $committedMapHash))
    { throw "Summary '$os' is diagnostic, synthetic, or uses an uncommitted variant map; use -Diagnostic." }

    $images = @($raw.inputs | ForEach-Object { @($_.containerImageDigests) } | Sort-Object -Unique -CaseSensitive)
    if ($manifest.sourceCommit -cne $ExpectedCommit -or $manifest.os -cne $os -or $manifest.architecture -cne 'x64' -or
        $manifest.diagnostic -ne $summary.diagnostic -or $manifest.synthetic -ne $summary.synthetic -or
        (@($manifest.containerImageDigests | Sort-Object -CaseSensitive) -join "`n") -cne ($images -join "`n") -or
        $manifest.rawSamplesSha256 -cne $rawHash)
    { throw "Environment manifest '$os' does not match its commit, platform, labels, raw samples, or measured images." }

    $expected = Get-ExpectedWorkloads $os
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($comparison in @($summary.comparisons))
    {
        $key = [string]$comparison.workloadKey
        if (-not $seen.Add($key) -or -not $expected.ContainsKey($key) -or $expected[$key] -cne $comparison.mode)
        { throw "Workload '$key' is duplicated, outside the Core contract, or has the wrong mode." }
        $metrics = [ordered]@{}
        foreach ($metric in $contract.requiredMetrics)
        {
            $v = $comparison.metrics.$metric
            $metrics[$metric] = [ordered]@{
                candidate = $v.candidate; candidateCiLower = $v.candidateCiLower; candidateCiUpper = $v.candidateCiUpper
                reference = $v.reference; referenceCiLower = $v.referenceCiLower; referenceCiUpper = $v.referenceCiUpper
            }
        }
        $entry = [ordered]@{
            workloadKey = $key; mode = $comparison.mode; confidenceLevel = $comparison.confidenceLevel
            trials = $comparison.trials; metrics = $metrics
        }
        $comparisons.Add($entry)
        $problems = Test-Comparison ([pscustomobject]@{ mode = $comparison.mode; metrics = $comparison.metrics })
        if ($problems.Count -ne 0) { $failing.Add("| ``$key`` | $($problems -join '; ') |") }
    }
    foreach ($key in $expected.Keys) { if (-not $seen.Contains($key)) { $missing.Add($key) } }
    $reportRows.Add("| $os | $($seen.Count) | $($expected.Count - $seen.Count) | $(@($summary.comparisons | Select-Object -ExpandProperty trials | Sort-Object -Unique) -join ', ') | ``$($summary.variantMap.crossOsProfile)`` | $(if ($summary.diagnostic) { 'yes' } else { 'no' }) |")
    $environments.Add([ordered]@{
        os = $os; architecture = 'x64'; sourceCommit = $ExpectedCommit
        environmentManifestPath = "$os/environment-manifest.json"; environmentManifestSha256 = Get-Sha256 $manifestPath
        rawSamplesPath = "$os/raw-samples.json"; rawSamplesSha256 = $rawHash
        summaryPath = "$os/summary.json"; summarySha256 = $summaryHash
        checkReportPath = "$os/check-report.json"; checkReportSha256 = Get-Sha256 $checkPath
        containerImageDigests = $images
    })
}
if (-not $Diagnostic -and $missing.Count -ne 0)
{
    throw "Qualification assembly is missing $($missing.Count) contract workloads, for example '$($missing[0])'. Missing evidence fails; it is never filled."
}
$isDiagnostic = $Diagnostic.IsPresent -or $anyDiagnostic -or $anySynthetic

Copy-Item -LiteralPath $VerifierSelfTestLogPath -Destination (Join-Path $root 'verifier-self-tests.log')
if ((Get-Item -LiteralPath (Join-Path $root 'verifier-self-tests.log')).Length -le 0) { throw 'The verifier self-test log is empty.' }

$report = [Text.StringBuilder]::new()
$null = $report.AppendLine('# BlueTusk 1.1 Core performance-leadership consolidation').AppendLine()
if ($isDiagnostic)
{
    $null = $report.AppendLine('> **DIAGNOSTIC' + $(if ($anySynthetic) { ' / SYNTHETIC' } else { '' }) +
        ' OUTPUT. This is not performance evidence and must never be relabelled as qualification.** It proves the pipeline only.').AppendLine()
}
$null = $report.AppendLine("Candidate: ``$ExpectedCommit``. Release $($contract.release), scope Core. Contract SHA-256 ``$contractHash``.")
$null = $report.AppendLine("Variant map SHA-256 ``$mapHash`` (committed map: $(if ($mapHash -ceq $committedMapHash) { 'yes' } else { 'NO' })).").AppendLine()
$null = $report.AppendLine('Pipeline: raw captures -> generator (seeded studentized bootstrap of per-trial means) -> independent checker (recomputed from raw) -> this assembler -> `verify-performance-leadership-evidence.ps1`.')
$null = $report.AppendLine('Bounds are separate nominal 99% studentized intervals per role, with at least 30 independent qualification trials. The contract remains at 95%; the verifier gates candidate upper / reference lower (lower-better) and candidate lower / reference upper (throughput). Ties fail.').AppendLine()
$null = $report.AppendLine('| OS | Comparisons | Missing | Trials | Cross-OS profile | Diagnostic |').AppendLine('|---|---:|---:|---|---|---|')
foreach ($row in $reportRows) { $null = $report.AppendLine($row) }
$null = $report.AppendLine().AppendLine("## Comparisons not meeting their gate ($($failing.Count))").AppendLine()
if ($failing.Count -eq 0) { $null = $report.AppendLine('None of the assembled comparisons fails its numerical gate.') }
else { $null = $report.AppendLine('| Workload | Failure |').AppendLine('|---|---|'); foreach ($row in $failing) { $null = $report.AppendLine($row) } }
$null = $report.AppendLine().AppendLine("## Missing contract workloads ($($missing.Count))").AppendLine()
if ($missing.Count -eq 0) { $null = $report.AppendLine('None.') }
else
{
    $null = $report.AppendLine('Missing evidence fails the verifier. Cross-OS Provider variants stay missing until the owner adopts a meaning (see `docs/qualification/provider-cross-os-variant-proposal.md`).').AppendLine()
    foreach ($group in @($missing | Group-Object { $_.Split('|')[0] + '|' + $_.Split('|')[1] } | Sort-Object Name))
    { $null = $report.AppendLine("- ``$($group.Name)``: $($group.Count)") }
}
$null = $report.AppendLine().AppendLine('Per-trial values, raw hashes and every bound are in each OS `summary.json`; the independent check is in `check-report.json`.')
[IO.File]::WriteAllText((Join-Path $root 'consolidated-report.md'), $report.ToString(), [Text.UTF8Encoding]::new($false))

$evidence = [ordered]@{
    schemaVersion = 3
    release = $contract.release
    scope = 'Core'
    sourceCommit = $ExpectedCommit
    confidenceLevel = [double]$rules.confidenceLevel
    diagnostic = $isDiagnostic
    synthetic = $anySynthetic
    consolidatedReportPath = 'consolidated-report.md'
    consolidatedReportSha256 = Get-Sha256 (Join-Path $root 'consolidated-report.md')
    verifierSelfTestsPath = 'verifier-self-tests.log'
    verifierSelfTestsSha256 = Get-Sha256 (Join-Path $root 'verifier-self-tests.log')
    pipeline = [ordered]@{
        statisticsMethod = 'conservative-studentized-bootstrap-of-trial-means/2'
        contractSha256 = $contractHash
        variantMapSha256 = $mapHash
        crossOsProfile = @($crossOsProfiles)
        assembler = 'eng/assemble-performance-leadership-evidence.ps1'
        missingWorkloads = $missing.Count
        failingComparisons = $failing.Count
    }
    environments = $environments.ToArray()
    comparisons = $comparisons.ToArray()
}
$path = Join-Path $root $evidenceName
$stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try
{
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($evidence | ConvertTo-Json -Depth 12))
    $stream.Write($bytes, 0, $bytes.Length)
}
finally { $stream.Dispose() }
Write-Output ("Assembled $($comparisons.Count) Core comparisons from $($present -join ' and ') into '$evidenceName'" +
    "$(if ($isDiagnostic) { ' (DIAGNOSTIC: never release evidence)' } else { '' }); $($missing.Count) missing, $($failing.Count) failing their gate. " +
    'Run verify-performance-leadership-evidence.ps1 for the verdict.')
