[CmdletBinding()]
param(
    [string] $BenchmarkAssemblyPath = 'benchmarks/BlueTusk.Benchmarks/bin/Release/net10.0/BlueTusk.Benchmarks.dll',
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
$checker = Join-Path $PSScriptRoot 'PerformanceEvidenceChecker/bin/Release/net10.0/PerformanceEvidenceChecker.dll'
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
    $expectedLegs = 2 * (@($contract.workloads.Provider.variants).Count + 5)
    if ($legs.Count -ne $expectedLegs -or @($legs | Where-Object { $_.status -eq 'unresolved' }).Count -ne 2 -or
        @($legs | Where-Object { $_.leg -eq 'windows-provider-linux' -and $_.status -eq 'unresolved' }).Count -ne 1)
    { throw 'The qualification plan must keep every leg, with unresolved cross-OS legs failing closed.' }
}

function Invoke-Dotnet([string[]] $Arguments)
{
    $output = & dotnet @Arguments 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String) }
}

function New-Fixture([string] $Name, [string] $Outcome, [string] $Map, [string[]] $Oses = @('windows', 'linux'))
{
    $performance = Join-Path $temporary "$Name/performance"
    foreach ($os in $Oses)
    {
        $osRoot = Join-Path $performance $os
        $result = Invoke-Dotnet @($benchmark, '--performance-evidence-synthesize', $osRoot, $commit, $os, $Outcome, $contractPath, $Map)
        if ($result.ExitCode -ne 0) { throw "Synthesis failed: $($result.Output)" }
        $result = Invoke-Dotnet @($benchmark, '--performance-evidence-generate-synthetic', $osRoot, $commit, $os, $contractPath, $Map)
        if ($result.ExitCode -ne 0) { throw "Synthetic generation failed: $($result.Output)" }
        & (Join-Path $PSScriptRoot 'write-performance-environment-manifest.ps1') -OsRoot $osRoot -ExpectedCommit $commit -Os $os `
            -Topology 'synthetic self-test fixture' -Synthetic | Out-Null
        $result = Invoke-Dotnet @($checker, 'check', $osRoot, $commit, $os, $Map)
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

    Write-Output "Performance evidence pipeline self-test passed: complete synthetic Windows/Linux matrices generated, independently checked and assembled; diagnostic labels rejected; generated shape accepted only in a test probe; ties failed; $script:rejections rejected substitutions."
}
finally
{
    if ($temporary.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $temporary))
    {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
