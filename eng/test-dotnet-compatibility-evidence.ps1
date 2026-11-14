[CmdletBinding()]
param([string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$verifier = Join-Path $PSScriptRoot 'verify-dotnet-compatibility-evidence.ps1'
$contractPath = Join-Path $PSScriptRoot 'dotnet-compatibility.json'
$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
$root = Join-Path $RepositoryRoot ('artifacts/dotnet-compatibility/evidence-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $root 'proof') -Force | Out-Null
$commit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
$source = [ordered]@{ schemaVersion = 1; sourceCommit = $commit; worktreeClean = $false; files = @() }
$source | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'source-fingerprint.json') -Encoding utf8
# Synthetic historical payloads exercise validation semantics; they cannot
# qualify the current source or full product support.
'synthetic historical test assembly fixture' | Set-Content -LiteralPath (Join-Path $root 'proof/BlueTusk.Protocol.Tests.dll') -Encoding utf8
'{"runtimeOptions":{"tfm":"net5.0","framework":{"name":"Microsoft.NETCore.App","version":"5.0.0"}}}' |
    Set-Content -LiteralPath (Join-Path $root 'proof/BlueTusk.Protocol.Tests.runtimeconfig.json') -Encoding utf8
$testId = [guid]::NewGuid().ToString()
$executionId = [guid]::NewGuid().ToString()
$trx = @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult testId="$testId" executionId="$executionId" outcome="Passed" /></Results>
  <TestDefinitions><UnitTest id="$testId"><TestMethod codeBase="BlueTusk.Protocol.Tests.dll" /></UnitTest></TestDefinitions>
  <TestEntries><TestEntry testId="$testId" executionId="$executionId" /></TestEntries>
  <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary>
</TestRun>
"@
$trx | Set-Content -LiteralPath (Join-Path $root 'results.trx') -Encoding utf8
function Get-FixtureHash([string] $Relative) { (Get-FileHash -LiteralPath (Join-Path $root $Relative) -Algorithm SHA256).Hash.ToLowerInvariant() }
$baseline = [ordered]@{
    schemaVersion = 2
    sourceCommit = $commit
    sourceTreeDirty = $true
    requestedFrameworks = @('net5.0')
    sdkVersion = $contract.sdkVersion
    inventoryComplete = $false
    fullRuntimeCoverage = $true
    contractSha256 = (Get-FileHash -LiteralPath $contractPath -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceFingerprint = 'source-fingerprint.json'
    sourceFingerprintSha256 = Get-FixtureHash 'source-fingerprint.json'
    sourceUnchangedDuringRun = $true
    coveredRuntimePackages = 1
    pendingRuntimePackages = @()
    succeeded = $true
    failure = $null
    results = @([ordered]@{
        framework = 'net5.0'; runtimeVersion = $contract.runtimeVersions.'net5.0'
        testProject = 'tests/BlueTusk.Protocol.Tests/BlueTusk.Protocol.Tests.csproj'
        passed = 1; operatingSystem = 'windows'; containerImage = $null
        postgreSqlVersion = $null; postgreSqlIdentity = $null
        pgVectorVersion = $null; pgVectorIdentity = $null
        postgreSqlExtensionVersion = $null; postgreSqlExtensionIdentity = $null
        minioVersion = $null; minioIdentity = $null
        trx = 'results.trx'; trxSha256 = Get-FixtureHash 'results.trx'
        testAssembly = 'proof/BlueTusk.Protocol.Tests.dll'; testAssemblySha256 = Get-FixtureHash 'proof/BlueTusk.Protocol.Tests.dll'
        runtimeConfiguration = 'proof/BlueTusk.Protocol.Tests.runtimeconfig.json'
        runtimeConfigurationSha256 = Get-FixtureHash 'proof/BlueTusk.Protocol.Tests.runtimeconfig.json'
    })
}
$baselineText = $baseline | ConvertTo-Json -Depth 12
$reportPath = Join-Path $root 'report.json'
$baselineText | Set-Content -LiteralPath $reportPath -Encoding utf8
$summary = & $verifier -ReportPath $reportPath -Historical -PassThru
if ($summary.verifiedPairs -ne 1 -or $summary.fullRuntimeCoverage -or $summary.matrixComplete -or $summary.currentSourceValidated)
{
    throw 'A single synthetic historical pair must never satisfy complete matrix or current-source support.'
}
function Assert-EvidenceRejected
{
    param([string] $Name, [scriptblock] $Mutate, [string] $ExpectedMessage, [switch] $Matrix, [switch] $Duplicate)
    $fixture = $baselineText | ConvertFrom-Json
    & $Mutate $fixture
    $fixture | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
    $paths = if ($Duplicate) { @($reportPath, $reportPath) } else { @($reportPath) }
    $rejected = $false
    try { & $verifier -ReportPath $paths -Historical -RequireMatrixComplete:$Matrix | Out-Null }
    catch
    {
        if ($_.Exception.Message -notmatch $ExpectedMessage) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Evidence verifier accepted '$Name'." }
}
Assert-EvidenceRejected -Name missing-pairs -Mutate { param($fixture) } -Matrix -ExpectedMessage 'missing .* required OS/framework/test pairs'
Assert-EvidenceRejected -Name duplicate-pair -Mutate { param($fixture) } -Duplicate -ExpectedMessage 'unknown, duplicate, unrequested, or empty'
Assert-EvidenceRejected -Name zero-tests -ExpectedMessage 'unknown, duplicate, unrequested, or empty' -Mutate {
    param($fixture) $fixture.results[0].passed = 0
}
Assert-EvidenceRejected -Name changed-source -ExpectedMessage 'successful stable-source' -Mutate {
    param($fixture) $fixture.sourceUnchangedDuringRun = $false
}
Assert-EvidenceRejected -Name stale-contract -ExpectedMessage 'current contract' -Mutate {
    param($fixture) $fixture.contractSha256 = '0' * 64
}
Assert-EvidenceRejected -Name missing-postgresql-fixture -ExpectedMessage 'pinned PostgreSQL fixture' -Mutate {
    param($fixture) $fixture.results[0].testProject = $contract.postgreSqlFixture.testProjects[0]
}
Assert-EvidenceRejected -Name missing-minio-fixture -ExpectedMessage 'pinned minio fixture' -Mutate {
    param($fixture) $fixture.results[0].testProject = $contract.minioFixture.testProjects[0]
}
Assert-EvidenceRejected -Name missing-pgvector-fixture -ExpectedMessage 'pinned pgVector fixture' -Mutate {
    param($fixture)
    $fixture.results[0].testProject = $contract.postgreSqlFixture.pgVector.testProjects[0]
    $fixture.results[0].postgreSqlVersion = $contract.postgreSqlFixture.version
    $fixture.results[0].postgreSqlIdentity = 'sha256:' + $contract.postgreSqlFixture.windowsArchive.sha256
}
Assert-EvidenceRejected -Name wrong-tfm -ExpectedMessage 'compiled runtime framework' -Mutate {
    param($fixture)
    '{"runtimeOptions":{"tfm":"net11.0"}}' | Set-Content -LiteralPath (Join-Path $root 'proof/wrong-runtime.json') -Encoding utf8
    $fixture.results[0].runtimeConfiguration = 'proof/wrong-runtime.json'
    $fixture.results[0].runtimeConfigurationSha256 = Get-FixtureHash 'proof/wrong-runtime.json'
}
Assert-EvidenceRejected -Name skipped-tests -ExpectedMessage 'matching passed results|skipped, failed' -Mutate {
    param($fixture)
    $trx.Replace('outcome="Passed"', 'outcome="NotExecuted"').Replace('executed="1" passed="1"', 'executed="0" passed="0"') |
        Set-Content -LiteralPath (Join-Path $root 'skipped.trx') -Encoding utf8
    $fixture.results[0].trx = 'skipped.trx'
    $fixture.results[0].trxSha256 = Get-FixtureHash 'skipped.trx'
}
Assert-EvidenceRejected -Name tampered-trx -ExpectedMessage 'file hash binding' -Mutate {
    param($fixture) $fixture.results[0].trxSha256 = '0' * 64
}
Write-Output 'Verified compatibility evidence rejects zero/skipped tests, wrong frameworks, stale contracts, changed sources, tampered files, duplicate pairs, and incomplete OS/runtime matrices.'
