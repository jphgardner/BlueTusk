[CmdletBinding()]
param(
    [string] $BenchmarkAssemblyPath = 'benchmarks/BlueTusk.Benchmarks/bin/Release/net10.0/BlueTusk.Benchmarks.dll',
    [string] $CheckerAssemblyPath = 'eng/PerformanceEvidenceChecker/bin/Release/net10.0/PerformanceEvidenceChecker.dll',
    [switch] $NoCheckerBuild
)

# End-to-end self-test of generator -> independent checker -> assembler -> verifier.
# Every fixture here is SYNTHETIC and labelled diagnostic: it exercises guards, never performance.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$benchmark = (Resolve-Path -LiteralPath (Join-Path $root $BenchmarkAssemblyPath)).Path
$contractPath = Join-Path $PSScriptRoot 'performance-leadership-contract.json'
$mapPath = Join-Path $PSScriptRoot 'performance-variant-map.json'
$verifier = Join-Path $PSScriptRoot 'verify-performance-leadership-evidence.ps1'
$checkerProject = Join-Path $PSScriptRoot 'PerformanceEvidenceChecker/PerformanceEvidenceChecker.csproj'
$checker = Join-Path $root $CheckerAssemblyPath
$commit = '3333333333333333333333333333333333333333'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-performance-pipeline-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporary
$script:rejections = 0

function Assert-Workflow
{
    $workflow = Get-Content -LiteralPath (Join-Path $root '.github/workflows/core-performance-evidence.yml') -Raw
    foreach ($required in @(
        'CAPTURE-1.1-PERFORMANCE-EVIDENCE', 'max-parallel: 1', 'group: bluetusk-reference-host',
        '["self-hosted","windows","x64","bluetusk-benchmark"]', 'get-core-performance-capture-plan.ps1',
        'invoke-core-performance-capture.ps1', '--performance-evidence-generate', 'PerformanceEvidenceChecker.dll check',
        'write-performance-environment-manifest.ps1', 'assemble-performance-leadership-evidence.ps1',
        'verify-performance-leadership-evidence.ps1', 'test-performance-evidence-pipeline.ps1', 'BLUETUSK_EVIDENCE_STORE'))
    {
        if (-not $workflow.Contains($required)) { throw "core-performance-evidence.yml lost '$required'." }
    }
    if ($workflow -notmatch '(?m)^\s*workflow_dispatch\s*:' -or $workflow -match '(?m)^\s*(push|pull_request|schedule)\s*:' -or
        $workflow.Contains('--performance-evidence-generate-synthetic'))
    { throw 'core-performance-evidence.yml must stay manual and must never use the synthetic generator path.' }
    $plan = & (Join-Path $PSScriptRoot 'get-core-performance-capture-plan.ps1') | ConvertFrom-Json
    $legs = @($plan.capture.include)
    $contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
    $levels = @($contract.workloads.Provider.concurrency)
    $expectedLegs = 2 * (@($contract.workloads.Provider.variants).Count * $levels.Count + 5)
    if ($legs.Count -ne $expectedLegs -or @($legs | Where-Object { $_.status -eq 'unresolved' }).Count -ne 2 * $levels.Count -or
        @($legs | Where-Object { $_.leg -eq 'windows-provider-linux-c1' -and $_.status -eq 'unresolved' }).Count -ne 1)
    { throw 'The qualification plan must keep every leg, with unresolved cross-OS legs failing closed.' }
    foreach ($os in @('windows', 'linux')) {
        foreach ($variant in $contract.workloads.Provider.variants) {
            $chunks = @($legs | Where-Object { $_.os -ceq $os -and $_.kind -ceq 'provider' -and $_.variant -ceq $variant })
            $actualLevels = ($chunks.concurrency | Sort-Object) -join ','
            $expectedLevels = ($levels | Sort-Object) -join ','
            if ($chunks.Count -ne $levels.Count -or $actualLevels -cne $expectedLevels)
            { throw 'Provider concurrency chunks must cover each committed level exactly once.' }
        }
    }
    if (-not $workflow.Contains('Qualification producers are incomplete:') -or -not $workflow.Contains('No measurement jobs will start.'))
    { throw 'Incomplete qualification must fail before reserving runners or starting measurements.' }
}

function Invoke-Dotnet([string[]] $Arguments)
{
    $output = & dotnet @Arguments 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String) }
}

function New-Fixture([string] $Name, [string] $Outcome, [string] $Map, [string] $Contract = $contractPath)
{
    $performance = Join-Path $temporary "$Name/performance"
    foreach ($os in @('windows', 'linux'))
    {
        $osRoot = Join-Path $performance $os
        $result = Invoke-Dotnet @($benchmark, '--performance-evidence-synthesize', $osRoot, $commit, $os, $Outcome, $Contract, $Map)
        if ($result.ExitCode -ne 0) { throw "Synthesis failed: $($result.Output)" }
        $result = Invoke-Dotnet @($benchmark, '--performance-evidence-generate-synthetic', $osRoot, $commit, $os, $Contract, $Map)
        if ($result.ExitCode -ne 0) { throw "Synthetic generation failed: $($result.Output)" }
        & (Join-Path $PSScriptRoot 'write-performance-environment-manifest.ps1') -OsRoot $osRoot -ExpectedCommit $commit -Os $os `
            -Topology 'synthetic self-test fixture' -VariantMapPath $Map -Synthetic | Out-Null
        $result = Invoke-Dotnet @($checker, 'check', $osRoot, $commit, $os, $Map, '--contract', $Contract)
        if ($result.ExitCode -ne 0) { throw "Independent check failed on a valid synthetic fixture: $($result.Output)" }
    }
    return $performance
}

function Copy-Fixture([string] $Source, [string] $Name)
{
    $target = Join-Path $temporary "$Name/performance"
    $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent)
    Copy-Item -LiteralPath $Source -Destination $target -Recurse
    return $target
}

function Assert-Rejected([string] $Name, [scriptblock] $Action, [string] $Expected)
{
    $message = $null
    try
    {
        $result = & $Action
        if ($result -is [pscustomobject] -and $null -ne $result.PSObject.Properties['ExitCode'])
        {
            if ($result.ExitCode -ne 0) { $message = $result.Output }
        }
    }
    catch { $message = $_.Exception.Message }
    if ($null -eq $message -or -not $message.Contains($Expected))
    { throw "Rejection case '$Name' expected '$Expected'; received '$message'." }
    $script:rejections++
}

function Remove-DiagnosticLabelsForShapeProbe([string] $Performance, [string] $Source)
{
    # TEST-ONLY schema probe inside a temporary directory: proves that generated comparisons have the
    # exact shape the verifier evaluates. The labels exist precisely so this can never happen to output.
    $evidence = Get-Content -LiteralPath (Join-Path $Performance $Source) -Raw | ConvertFrom-Json -AsHashtable
    $evidence.diagnostic = $false
    $evidence.synthetic = $false
    foreach ($environment in $evidence.environments)
    {
        $path = Join-Path $Performance $environment.environmentManifestPath
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        $manifest.diagnostic = $false
        $manifest.synthetic = $false
        $probe = "$($environment.os)/shape-probe-environment.json"
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Performance $probe) -Encoding utf8NoBOM
        $environment.environmentManifestPath = $probe
        $environment.environmentManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $Performance $probe) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($evidence.release -ceq '1.2.0')
        {
            $summary = Get-Content -LiteralPath (Join-Path $Performance $environment.summaryPath) -Raw | ConvertFrom-Json -AsHashtable
            $summary.diagnostic = $false
            $summary.synthetic = $false
            $summaryProbe = "$($environment.os)/shape-probe-summary.json"
            $summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $Performance $summaryProbe) -Encoding utf8NoBOM
            $environment.summaryPath = $summaryProbe
            $environment.summarySha256 = (Get-FileHash -LiteralPath (Join-Path $Performance $summaryProbe) -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $path = Join-Path $Performance 'shape-probe.json'
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    return $path
}

try
{
    Assert-Workflow
    if (-not $NoCheckerBuild)
    {
        $build = Invoke-Dotnet @('build', $checkerProject, '--configuration', 'Release', '--nologo', '--verbosity', 'quiet')
        if ($build.ExitCode -ne 0) { throw "Checker build failed: $($build.Output)" }
    }
    $selfTest = Invoke-Dotnet @($checker, 'self-test')
    if ($selfTest.ExitCode -ne 0) { throw "Checker self-test failed: $($selfTest.Output)" }

    # A test-only map in which the cross-OS variants resolve, so a complete synthetic matrix exists.
    $testMap = Join-Path $temporary 'test-variant-map.json'
    $mapText = (Get-Content -LiteralPath $mapPath -Raw).Replace('"crossOsProfile": "unresolved"', '"crossOsProfile": "synthetic-foreign-client"').Replace(
        '"profiles": {', '"profiles": { "synthetic-foreign-client": { "clientOs": "other", "tls": false, "networkProfile": null, "hostOs": ["windows", "linux"] },')
    [IO.File]::WriteAllText($testMap, $mapText)
    $log = Join-Path $temporary 'self-tests.log'
    'Synthetic pipeline self-test log; not verifier evidence.' | Set-Content -LiteralPath $log -Encoding utf8NoBOM

    # 1. Complete synthetic "win" matrix: assembles as diagnostic; the verifier must reject the label.
    $win = New-Fixture 'win' 'win' $testMap
    & (Join-Path $PSScriptRoot 'assemble-performance-leadership-evidence.ps1') -EvidenceRoot $win -ExpectedCommit $commit `
        -VerifierSelfTestLogPath $log -VariantMapPath $testMap -Diagnostic | Out-Null
    $diagnosticEvidence = Join-Path $win 'diagnostic-performance-leadership-evidence.json'
    if (Test-Path -LiteralPath (Join-Path $win 'performance-leadership-evidence.json')) { throw 'Diagnostic assembly wrote the readiness file name.' }
    Assert-Rejected 'diagnostic-label' { & $verifier -EvidencePath $diagnosticEvidence -ExpectedCommit $commit } 'never release evidence'
    $probe = Remove-DiagnosticLabelsForShapeProbe $win 'diagnostic-performance-leadership-evidence.json'
    $verdict = & $verifier -EvidencePath $probe -ExpectedCommit $commit
    if (-not ([string]$verdict).Contains('536 declared BlueTusk 1.1.0 Core performance comparisons'))
    { throw "Generated comparisons do not have the verifier's exact shape: $verdict" }

    # 2. Synthetic tie: identical candidate and reference must fail the gate, even with labels removed.
    $tie = New-Fixture 'tie' 'tie' $testMap
    & (Join-Path $PSScriptRoot 'assemble-performance-leadership-evidence.ps1') -EvidenceRoot $tie -ExpectedCommit $commit `
        -VerifierSelfTestLogPath $log -VariantMapPath $testMap -Diagnostic | Out-Null
    $tieProbe = Remove-DiagnosticLabelsForShapeProbe $tie 'diagnostic-performance-leadership-evidence.json'
    Assert-Rejected 'statistical-tie' { & $verifier -EvidencePath $tieProbe -ExpectedCommit $commit } 'failed'
    # Ties fail every leadership gate. Only Control Plane "unique" workloads may legitimately tie (2% allowance).
    $failedRows = @((Get-Content -LiteralPath (Join-Path $tie 'consolidated-report.md')) | Where-Object { $_.StartsWith('| `', [StringComparison]::Ordinal) })
    if (@($failedRows | Where-Object { $_ -notmatch '\|ControlPlane\|sources=' }).Count -ne 536 - 12)
    { throw 'The consolidated report must list every tied leadership comparison as failing.' }

    # 3. Rejected substitutions.
    Assert-Rejected 'synthetic-as-qualification' {
        $copy = Copy-Fixture $win 'qualification'
        Get-ChildItem $copy -File | ForEach-Object { [IO.File]::Delete($_.FullName) }
        & (Join-Path $PSScriptRoot 'assemble-performance-leadership-evidence.ps1') -EvidenceRoot $copy -ExpectedCommit $commit `
            -VerifierSelfTestLogPath $log -VariantMapPath $testMap
    } 'diagnostic, synthetic, or uses an uncommitted variant map'
    Assert-Rejected 'uncommitted-variant-map-for-checker' {
        $copy = Copy-Fixture $win 'committed-map'
        [IO.File]::Delete((Join-Path $copy 'windows/check-report.json'))
        Invoke-Dotnet @($checker, 'check', (Join-Path $copy 'windows'), $commit, 'windows', $mapPath)
    } 'different variant map'
    Assert-Rejected 'summary-edited-after-check' {
        $copy = Copy-Fixture $win 'edited-after-check'
        Get-ChildItem $copy -File | ForEach-Object { [IO.File]::Delete($_.FullName) }
        $summary = Join-Path $copy 'windows/summary.json'
        [IO.File]::WriteAllText($summary, ([IO.File]::ReadAllText($summary) -replace '"candidate": ([0-9.]+),', '"candidate": 1$1,'))
        & (Join-Path $PSScriptRoot 'assemble-performance-leadership-evidence.ps1') -EvidenceRoot $copy -ExpectedCommit $commit `
            -VerifierSelfTestLogPath $log -VariantMapPath $testMap -Diagnostic
    } 'bound to a different summary'
    Assert-Rejected 'summary-value-not-recomputable' {
        $copy = Copy-Fixture $win 'unrecomputable'
        [IO.File]::Delete((Join-Path $copy 'windows/check-report.json'))
        $summary = Join-Path $copy 'windows/summary.json'
        $pattern = [regex]::new('"candidateCiUpper": ([0-9.Ee+-]+)')
        $text = $pattern.Replace([IO.File]::ReadAllText($summary), {
            param($match) '"candidateCiUpper": ' + ([double]$match.Groups[1].Value * 1.5).ToString('R', [Globalization.CultureInfo]::InvariantCulture) }, 1)
        [IO.File]::WriteAllText($summary, $text)
        Invoke-Dotnet @($checker, 'check', (Join-Path $copy 'windows'), $commit, 'windows', $testMap)
    } 'differs from recomputed'
    foreach ($mutation in @(
        @{ Name = 'obsolete-percentile-method'; Property = 'method'; Value = 'expanded-percentile-bootstrap-of-trial-means/1' },
        @{ Name = 'narrower-interval-confidence'; Property = 'intervalConfidenceLevel'; Value = 0.95 },
        @{ Name = 'edited-interval-tail'; Property = 'tailProbability'; Value = 0.025 }))
    {
        Assert-Rejected $mutation.Name {
            $copy = Copy-Fixture $win $mutation.Name
            [IO.File]::Delete((Join-Path $copy 'windows/check-report.json'))
            $path = Join-Path $copy 'windows/summary.json'
            $summary = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
            $summary.statistics[$mutation.Property] = $mutation.Value
            $summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
            Invoke-Dotnet @($checker, 'check', (Join-Path $copy 'windows'), $commit, 'windows', $testMap)
        } $(if ($mutation.Property -eq 'tailProbability') { 'tail probability differs' } else { 'statistics method, confidence' })
    }
    Assert-Rejected 'raw-sample-replaced' {
        $copy = Copy-Fixture $win 'raw-replaced'
        [IO.File]::Delete((Join-Path $copy 'windows/check-report.json'))
        $rawFile = Get-ChildItem (Join-Path $copy 'windows/raw/streams') -Filter '*-candidate.json' | Select-Object -First 1
        [IO.File]::WriteAllText($rawFile.FullName, ([IO.File]::ReadAllText($rawFile.FullName)).Replace('"elapsedTicks":', '"elapsedTicks": 1'))
        Invoke-Dotnet @($checker, 'check', (Join-Path $copy 'windows'), $commit, 'windows', $testMap)
    } 'differs from its binding'
    Assert-Rejected 'unbound-raw-file' {
        $copy = Copy-Fixture $win 'unbound'
        [IO.File]::Delete((Join-Path $copy 'windows/check-report.json'))
        '{}' | Set-Content -LiteralPath (Join-Path $copy 'windows/raw/streams/extra.json')
        Invoke-Dotnet @($checker, 'check', (Join-Path $copy 'windows'), $commit, 'windows', $testMap)
    } 'unbound files'
    Assert-Rejected 'cross-os-copy' {
        # Same-OS raw data relabelled as the cross-OS variant cannot be generated.
        $copy = Join-Path $temporary 'cross-os-copy'
        $result = Invoke-Dotnet @($benchmark, '--performance-evidence-synthesize', $copy, $commit, 'windows', 'win', $contractPath, $testMap)
        if ($result.ExitCode -ne 0) { throw $result.Output }
        $native = Join-Path $copy 'raw/provider-windows'
        $foreign = Join-Path $copy 'raw/provider-linux'
        Get-ChildItem $foreign -File | ForEach-Object { [IO.File]::Delete($_.FullName) }
        Get-ChildItem $native -File | ForEach-Object {
            $text = [IO.File]::ReadAllText($_.FullName).Replace('|variant=windows', '|variant=linux').Replace('"variant":"windows"', '"variant":"linux"')
            [IO.File]::WriteAllText((Join-Path $foreign $_.Name), $text.Replace('"captureProfile":"native"', '"captureProfile":"synthetic-foreign-client"').Replace('"clientOs":"windows"', '"clientOs":"linux"'))
        }
        Invoke-Dotnet @($benchmark, '--performance-evidence-generate-synthetic', $copy, $commit, 'windows', $contractPath, $testMap)
    } 'InvalidDataException'
    Assert-Rejected 'unresolved-cross-os-with-committed-map' {
        $copy = Join-Path $temporary 'committed-map-generate'
        $result = Invoke-Dotnet @($benchmark, '--performance-evidence-synthesize', $copy, $commit, 'windows', 'win', $contractPath, $testMap)
        if ($result.ExitCode -ne 0) { throw $result.Output }
        Invoke-Dotnet @($benchmark, '--performance-evidence-generate-synthetic', $copy, $commit, 'windows', $contractPath, $mapPath)
    } 'no adopted meaning'
    Assert-Rejected 'synthetic-through-production-generator' {
        $copy = Join-Path $temporary 'production-generator'
        $result = Invoke-Dotnet @($benchmark, '--performance-evidence-synthesize', $copy, $commit, 'windows', 'win', $contractPath, $testMap)
        if ($result.ExitCode -ne 0) { throw $result.Output }
        Invoke-Dotnet @($benchmark, '--performance-evidence-generate', $copy, $commit, 'windows', $contractPath, $testMap)
    } 'Synthetic fixtures are accepted only by the self-test path'
    Assert-Rejected 'manifest-image-mismatch' {
        $copy = Copy-Fixture $win 'image-mismatch'
        Get-ChildItem $copy -File | ForEach-Object { [IO.File]::Delete($_.FullName) }
        $manifest = Join-Path $copy 'linux/environment-manifest.json'
        $value = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
        $value.containerImageDigests = @('postgres:other@sha256:' + ('d' * 64))
        $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM
        & (Join-Path $PSScriptRoot 'assemble-performance-leadership-evidence.ps1') -EvidenceRoot $copy -ExpectedCommit $commit `
            -VerifierSelfTestLogPath $log -VariantMapPath $testMap -Diagnostic
    } 'measured images'
    Assert-Rejected 'missing-environment' {
        $copy = Copy-Fixture $win 'missing-linux'
        Get-ChildItem $copy -File | ForEach-Object { [IO.File]::Delete($_.FullName) }
        Remove-Item -LiteralPath (Join-Path $copy 'linux') -Recurse -Force
        & (Join-Path $PSScriptRoot 'assemble-performance-leadership-evidence.ps1') -EvidenceRoot $copy -ExpectedCommit $commit `
            -VerifierSelfTestLogPath $log -VariantMapPath $testMap
    } 'needs checked windows and linux evidence'
    Assert-Rejected 'overwrite-existing-evidence' {
        & (Join-Path $PSScriptRoot 'assemble-performance-leadership-evidence.ps1') -EvidenceRoot $win -ExpectedCommit $commit `
            -VerifierSelfTestLogPath $log -VariantMapPath $testMap -Diagnostic
    } 'never overwrites'

    # 4. The committed map cannot even synthesize the unresolved cross-OS variant.
    $result = Invoke-Dotnet @($benchmark, '--performance-evidence-synthesize', (Join-Path $temporary 'committed-map-synthesis'),
        $commit, 'windows', 'win', $contractPath, $mapPath)
    if ($result.ExitCode -eq 0 -or -not $result.Output.Contains('no adopted meaning'))
    { throw 'The committed map must not produce unresolved cross-OS captures.' }

    $head = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the performance test checkout.' }
    $active = & (Join-Path $PSScriptRoot 'resolve-core-performance-release.ps1') -ExpectedCommit $head
    if ($active.Release -ceq '1.2.0')
    {
        $arguments = @{ ContractPath = $active.ContractPath; VariantMapPath = $active.VariantMapPath }
        & (Join-Path $PSScriptRoot 'verify-performance-leadership-contract.ps1') @arguments | Out-Null
        $activePlan = & (Join-Path $PSScriptRoot 'get-core-performance-capture-plan.ps1') @arguments -PlanPath $active.PlanPath | ConvertFrom-Json
        $providerLegs = @($activePlan.capture.include | Where-Object kind -eq 'provider')
        if ($providerLegs.Count -ne 21 -or @($providerLegs | Where-Object status -ne 'ready').Count -ne 0 -or
            @($providerLegs | Where-Object { $_.os -ceq 'windows' -and $_.variant -ceq 'linux' -and $_.profile -ceq 'linux-container-client' }).Count -ne 3 -or
            @($providerLegs | Where-Object { $_.os -ceq 'linux' -and $_.variant -ceq 'windows' }).Count -ne 0)
        { throw 'The 1.2 plan must include both clients on the Windows host and the native client on Linux.' }
        $inventoryLegs = @($activePlan.capture.include | Where-Object family -ceq 'ControlPlane')
        if ($inventoryLegs.Count -ne 2 -or @($inventoryLegs | Where-Object status -cne 'implemented').Count -ne 0)
        { throw 'The 1.2 plan must include a Control Plane capture on both operating systems.' }
        $workloads = (Get-Content -LiteralPath $active.ContractPath -Raw | ConvertFrom-Json).workloads
        $liveWorkloads = $workloads.Live
        $expectedPartitions = @(foreach ($scenario in $liveWorkloads.scenarios) {
            foreach ($rows in $liveWorkloads.resultCounts) { "$scenario-r$rows" }
        })
        foreach ($os in 'windows','linux') {
            $liveLegs = @($activePlan.capture.include | Where-Object { $_.os -ceq $os -and $_.family -ceq 'Live' })
            $syncLegs = @($activePlan.capture.include | Where-Object { $_.os -ceq $os -and $_.family -ceq 'Sync' })
            if (@($liveLegs | Where-Object status -cne 'implemented').Count -ne 0 -or
                (Compare-Object $expectedPartitions @($liveLegs.workload) -CaseSensitive) -or
                (Compare-Object $workloads.Sync.destinations @($syncLegs.workload) -CaseSensitive))
            { throw "The 1.2 Live and Sync jobs must cover each workload partition exactly once on $os." }
        }
        $partitionIds = @($activePlan.capture.include | Where-Object { $_.kind -ceq 'family' -and $_.workload } |
            ForEach-Object workload_number)
        if (($partitionIds | Sort-Object -Unique).Count -ne $partitionIds.Count)
        { throw 'Each benchmark partition must use a distinct fixture namespace.' }
        $liveScript = Join-Path $PSScriptRoot 'capture-live-signalr.ps1'
        $liveOutput = Join-Path $root "artifacts/live-matrix-plan-$([Guid]::NewGuid().ToString('N'))"
        $livePlan = & $liveScript -ExpectedCommit $head -OutputPath $liveOutput -AllWorkloads -Qualification -Trials 30 -Describe |
            ConvertFrom-Json
        $expectedLiveKeys = @(foreach ($rows in $liveWorkloads.resultCounts) {
            foreach ($subscribers in $liveWorkloads.subscriberCounts) {
                foreach ($scenario in $liveWorkloads.scenarios) {
                    "$($livePlan.os)|Live|results=$rows|subscribers=$subscribers|scenario=$scenario"
                }
            }
        })
        if ($livePlan.diagnostic -ne $false -or $livePlan.trials -ne 30 -or
            $livePlan.records -ne (60 * $expectedLiveKeys.Count) -or (Compare-Object $expectedLiveKeys $livePlan.workloads -CaseSensitive))
        { throw 'The Live qualification plan must include both roles for each full-matrix workload and trial.' }
        $partitionedKeys = @(foreach ($partition in $expectedPartitions) {
            $parameters = @{ ExpectedCommit = $head; OutputPath = $liveOutput; ContractWorkload = $partition; Trials = 50 }
            $selection = & $liveScript @parameters -AllWorkloads -Qualification -Describe | ConvertFrom-Json
            if ($selection.diagnostic -ne $false -or $selection.records -ne (100 * $liveWorkloads.subscriberCounts.Count) -or
                $selection.workloads.Count -ne $liveWorkloads.subscriberCounts.Count)
            { throw "Live partition '$partition' must retain both roles and every subscriber count at 50 trials." }
            $selection.workloads
        })
        if (Compare-Object $expectedLiveKeys $partitionedKeys -CaseSensitive)
        { throw 'Partitioned Live captures must cover the original matrix without gaps or overlaps.' }
        Assert-Rejected 'uncontracted-sync-partition' {
            $parameters = @{ Family = 'Sync'; ExpectedCommit = $head; OutputPath = $liveOutput; Workload = 'unavailable' }
            & (Join-Path $PSScriptRoot 'capture-family-comparison.ps1') @parameters -Diagnostic
        } 'absent from the performance contract'
        Assert-Rejected 'short-live-qualification' {
            & (Join-Path $PSScriptRoot 'capture-family-comparison.ps1') -Family Live -ExpectedCommit $head `
                -OutputPath $liveOutput -Trials 3
        } 'committed minimum trial count and timing'
        if (Test-Path -LiteralPath $liveOutput) { throw 'Planning and rejection must not create raw Live evidence.' }
        $primaryLegs = @($activePlan.capture.include | Where-Object family -ceq 'primary-hot-path')
        $primaryDescription = & (Join-Path $PSScriptRoot 'capture-family-comparison.ps1') -Family primary-hot-path `
            -ExpectedCommit $head -OutputPath (Join-Path $temporary 'primary') -Describe | ConvertFrom-Json
        if ($primaryDescription.definitionStatus -cne 'owner-approved' -or $primaryLegs.Count -ne 2 -or
            @($primaryLegs | Where-Object status -cne 'implemented').Count -ne 0)
        { throw 'The approved 1.2 primary workloads must stay required on both operating systems and feed the implemented capture route.' }
        $shortPrimary = Join-Path $temporary 'short-primary/primary-hot-path'
        Assert-Rejected 'short-primary-qualification' {
            & (Join-Path $PSScriptRoot 'capture-family-comparison.ps1') -Family primary-hot-path -ExpectedCommit $head `
                -OutputPath $shortPrimary -Trials 3
        } 'committed minimum trial count and timing'
        if (Test-Path -LiteralPath (Split-Path -Parent $shortPrimary)) { throw 'The rejected primary capture wrote evidence.' }
        $alteredContract = Join-Path $temporary 'changed-primary-contract.json'
        $contractDocument = Get-Content -LiteralPath $active.ContractPath -Raw | ConvertFrom-Json -AsHashtable
        $contractDocument.primaryHotPaths.workloads.Live.subscribers = 63
        $contractDocument | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $alteredContract -Encoding utf8NoBOM
        Assert-Rejected 'changed-primary-workload' {
            & (Join-Path $PSScriptRoot 'verify-performance-leadership-contract.ps1') -ContractPath $alteredContract
        } 'five approved primary workloads'
        $shortInventory = Join-Path $temporary 'short-inventory'
        Assert-Rejected 'short-inventory-qualification' {
            & (Join-Path $PSScriptRoot 'capture-family-comparison.ps1') -Family ControlPlane -ExpectedCommit $head `
                -OutputPath $shortInventory -Trials 3
        } 'committed minimum trial count and timing'
        if (Test-Path -LiteralPath $shortInventory) { throw 'The rejected Control Plane capture wrote evidence.' }

        $activeRoot = New-Fixture 'release-1.2' 'win' $active.VariantMapPath $active.ContractPath
        $activeOs = Join-Path $activeRoot 'windows'
        Assert-Rejected '1.2-summary-with-historical-contract' {
            Invoke-Dotnet @($checker, 'check', $activeOs, $commit, 'windows', $active.VariantMapPath,
                (Join-Path $temporary 'historical-contract-check.json'))
        } 'Summary identity, release'
        $assemblyArguments = @{
            EvidenceRoot = $activeRoot; ExpectedCommit = $commit; VerifierSelfTestLogPath = $log
            ContractPath = $active.ContractPath; VariantMapPath = $active.VariantMapPath; Diagnostic = $true
        }
        & (Join-Path $PSScriptRoot 'assemble-performance-leadership-evidence.ps1') @assemblyArguments | Out-Null
        $activeEvidence = Join-Path $activeRoot 'diagnostic-performance-leadership-evidence.json'
        $activeDocument = Get-Content -LiteralPath $activeEvidence -Raw | ConvertFrom-Json
        if ($activeDocument.release -cne '1.2.0' -or $activeDocument.diagnostic -ne $true -or $activeDocument.synthetic -ne $true)
        { throw 'Active performance assembly must retain the 1.2 identity and diagnostic labels.' }
        Assert-Rejected 'diagnostic-1.2' {
            & $verifier -EvidencePath $activeEvidence -ExpectedCommit $commit -ContractPath $active.ContractPath
        } 'never release evidence'
        $activeProbe = Remove-DiagnosticLabelsForShapeProbe $activeRoot 'diagnostic-performance-leadership-evidence.json'
        $activeVerdict = & $verifier -EvidencePath $activeProbe -ExpectedCommit $commit -ContractPath $active.ContractPath
        if (-not ([string]$activeVerdict).Contains('488 declared BlueTusk 1.2.0 Core performance comparisons'))
        { throw "The 1.2 comparison set does not match the approved topology: $activeVerdict" }
        Assert-Rejected 'historical-evidence-in-1.2' {
            & $verifier -EvidencePath $diagnosticEvidence -ExpectedCommit $commit -ContractPath $active.ContractPath
        } 'identity, release'
    }

    Write-Output "Performance evidence pipeline self-test passed: complete synthetic Windows/Linux matrices generated, independently checked and assembled; diagnostic labels rejected; generated shape accepted only in a test probe; ties failed; $script:rejections rejected substitutions."
}
finally
{
    if ($temporary.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $temporary))
    {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}

# GitHub's pwsh wrapper exits with LASTEXITCODE after this script returns.
# Expected native rejections above retain their exit codes for assertions, but
# must not leak the final rejection as the status of a fully passed self-test.
# This line is reached only after every assertion and cleanup have succeeded.
$global:LASTEXITCODE = 0
