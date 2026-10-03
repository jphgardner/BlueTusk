[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workflow = Get-Content (Join-Path $root '.github/workflows/jobs-release-capacity.yml') -Raw
$combinedWorkflow = Get-Content (Join-Path $root '.github/workflows/ecosystem-performance.yml') -Raw
$wrapper = Get-Content (Join-Path $PSScriptRoot 'verify-jobs-release-capacity.ps1') -Raw
$verifier = Get-Content (Join-Path $PSScriptRoot 'verify-ecosystem-performance.ps1') -Raw
$campaign = Get-Content (Join-Path $PSScriptRoot 'jobs-storage-campaign.ps1') -Raw
$local = Get-Content (Join-Path $PSScriptRoot 'run-ecosystem-capacity-local.ps1') -Raw
$budget = Get-Content (Join-Path $PSScriptRoot 'jobs-release-capacity-budgets.json') -Raw | ConvertFrom-Json

function Require([bool] $Condition, [string] $Message)
{
    if (-not $Condition) { throw $Message }
}

Require ($workflow -match '(?m)^  workflow_dispatch:\s*$' -and
    $workflow -notmatch '(?m)^  (?:push|pull_request|schedule):\s*$') 'Jobs capacity must be manual-only.'
Require ($workflow -match [regex]::Escape('ref: ${{ inputs.candidate_sha }}') -and
    $workflow -match 'bluetusk-benchmark' -and
    $workflow -match 'RUN-JOBS-RELEASE-CAPACITY') 'Jobs capacity must use the exact candidate and reference runner.'
foreach ($mode in @('Preflight', 'Run', 'Verify'))
{
    Require ($workflow -match "-Mode $mode ") "Jobs capacity omits the $mode verification step."
}
Require ($workflow -match [regex]::Escape('name: expansion-jobs-capacity-${{ inputs.candidate_sha }}') -and
    $workflow -match 'name: jobs-capacity-partial-' -and
    $workflow -match 'retention-days: 90') 'Jobs capacity must publish a distinct retained success artifact.'
foreach ($fixtureWorkflow in @($workflow, $combinedWorkflow, $local))
{
    $passwordAssignments = [regex]::Matches($fixtureWorkflow, 'POSTGRES_PASSWORD=')
    Require ($fixtureWorkflow -match '\[Security\.Cryptography\.RandomNumberGenerator\]::GetBytes\(24\)' -and
        $fixtureWorkflow -match 'POSTGRES_PASSWORD=\$fixturePassword' -and
        $passwordAssignments.Count -eq 1) 'Release capacity fixtures must use a fresh unpredictable PostgreSQL password.'
}
Require ($wrapper -match '-Product Jobs' -and
    $verifier -match 'jobs-release-capacity-budgets.json' -and
    $verifier -match '-Product Jobs -FixtureName bluetusk-jobs-release-pg15') 'Jobs capacity must use its scoped verifier and fixture.'
Require ($campaign -match "'storage-jobs'" -and
    $campaign -match "'bluetusk-jobs-release-pg15'" -and
    $campaign -match 'ImageRepoDigests' -and
    $verifier -match 'ImageRepoDigests') 'Jobs campaign must use its Jobs-only profile and verify the pinned image.'
Require ($local -match "-Mode Preflight" -and $local -match "-Mode Run" -and $local -match "-Mode Verify" -and
    $local -match 'bluetusk\.run-kind=local' -and $local -match '\[guid\]::NewGuid\(\)' -and
    $local -match 'GITHUB_RUN_ID\)\) \{ throw' -and
    $campaign -match 'FixtureRunKind' -and $verifier -match 'FixtureRunKind' -and
    $verifier -match 'BLUETUSK_LOCAL_CAMPAIGN_ID') 'Local capacity gates must use a local campaign UUID and record the Jobs fixture owner kind.'
Require ($budget.schemaVersion -eq 1 -and $budget.repetitions -eq 2 -and
    $budget.qualification -ceq 'manual-exact-candidate-jobs-capacity' -and
    $budget.jobsWorkflows.secondsPerProduct -ge 1800 -and
    $budget.jobsWorkflows.payloadMode -ceq 'SeededHighEntropy') 'Jobs capacity requires two full high-entropy campaigns.'
Require ($budget.jobsWorkflows.minimumCompletionsPerSecond.Jobs -ge 20 -and
    $budget.jobsWorkflows.maximumHotDurableP99Milliseconds.Jobs -le 10000 -and
    $budget.jobsWorkflows.maximumColdDurableP99Milliseconds.Jobs -le 3000 -and
    $budget.jobsWorkflows.maximumClusterWalBytesPerAccepted.Jobs -le 65536 -and
    $budget.jobsWorkflows.maximumRuntimeRelationBytes.Jobs -le 67108864 -and
    $budget.jobsWorkflows.maximumLateGrowthBytesPerMinute.Jobs -le 2097152) 'Jobs capacity budget cannot silently weaken its product limits.'
Require ($wrapper -match 'ProducerRunId' -and $verifier -match '\[long\]\$ProducerRunId' -and
    $verifier -match 'ProducerRunId applies only to archived Verify mode') 'Archived Jobs capacity re-verification must bind the producing run explicitly.'

# The shared ecosystem campaign qualifies one family per dispatch. Only that family's
# artifact may carry an expansion release name; the combined 'All' campaign is diagnostic.
Require ($combinedWorkflow -match '(?m)^  workflow_dispatch:\s*$' -and
    $combinedWorkflow -notmatch '(?m)^  (?:push|pull_request|schedule):\s*$') 'Ecosystem capacity must be manual-only.'
Require ($combinedWorkflow -match [regex]::Escape('ref: ${{ inputs.candidate_sha }}') -and
    $combinedWorkflow -match 'bluetusk-benchmark' -and
    $combinedWorkflow -match 'RUN-ECOSYSTEM-PERFORMANCE' -and
    $combinedWorkflow.Contains('$env:GITHUB_SHA -cne $env:CANDIDATE_SHA')) 'Ecosystem capacity must bind the exact candidate head on the reference runner.'
$familyInput = [regex]::Match($combinedWorkflow,
    '(?m)^      family:\r?\n(?:^        .*\r?\n)*?^        options:\r?\n(?<options>(?:^          - .+\r?\n)+)')
$familyOptions = @([regex]::Matches($familyInput.Groups['options'].Value, '(?m)^          - (\S+)\s*$') |
    ForEach-Object { $_.Groups[1].Value })
Require ($familyInput.Success -and ($familyOptions -join ',') -ceq 'Documents,Projections,Workflows,All') (
    'Ecosystem capacity must offer exactly Documents, Projections, Workflows and the diagnostic All campaign.')
foreach ($mode in @('Preflight', 'Run', 'Verify'))
{
    Require ($combinedWorkflow.Contains(
        "-Mode $mode -ExpectedCommit `$env:CANDIDATE_SHA -EvidenceRoot `$env:EVIDENCE_ROOT -Product `$env:CAPACITY_FAMILY")) (
        "Ecosystem capacity omits its family-scoped $mode step.")
}
Require ($combinedWorkflow.Contains("        if: inputs.family != 'All'`n        uses: actions/upload-artifact@") -and
    $combinedWorkflow.Contains('name: expansion-${{ env.CAPACITY_SLUG }}-capacity-${{ inputs.candidate_sha }}') -and
    [regex]::Matches($combinedWorkflow, 'name: expansion-').Count -eq 1 -and
    $combinedWorkflow.Contains("if: always() && inputs.family == 'All'") -and
    $combinedWorkflow.Contains('name: ecosystem-performance-${{ inputs.candidate_sha }}-${{ github.run_id }}-${{ github.run_attempt }}')) (
    'Only a single-family ecosystem dispatch may publish an expansion capacity artifact.')
Require ($verifier.Contains('Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $evidence -Product $Product') -and
    $verifier.Contains("'All', 'Jobs', 'Documents', 'Projections', 'Workflows', IgnoreCase = `$false")) (
    'The ecosystem verifier must check the exact family scope of archived evidence.')

Import-Module (Join-Path $PSScriptRoot 'ecosystem-capacity-scope.psm1') -Force
$commit = 'a' * 40
$rejected = 0
function Assert-ScopeRejected([string] $Name, [scriptblock] $Action, [string] $Guard)
{
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($Guard))
    {
        throw "Ecosystem scope case '$Name' did not fail at '$Guard': '$failure'."
    }
    $script:rejected++
}
Require ((Get-EcosystemCapacityArtifactName -Product Documents -Commit $commit) -ceq "expansion-documents-capacity-$commit" -and
    (Get-EcosystemCapacityArtifactName -Product Workflows -Commit $commit) -ceq "expansion-workflows-capacity-$commit") (
    'Family capacity artifacts must use the readiness binding name.')
Assert-ScopeRejected 'all-artifact' { Get-EcosystemCapacityArtifactName -Product All -Commit $commit } 'diagnostic only'
Assert-ScopeRejected 'family-case' { Get-EcosystemCapacityScope -Product documents } 'Unknown ecosystem capacity scope'

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bluetusk-ecosystem-scope-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
function New-ScopeFixture([string] $Name, [string] $Layout, [AllowNull()][object] $Family)
{
    # Synthetic layout only: placeholder files exercise the scope guards and are never evidence.
    $root = Join-Path $temporaryRoot $Name
    $layoutScope = Get-EcosystemCapacityScope -Product $Layout
    [IO.Directory]::CreateDirectory($root) | Out-Null
    foreach ($file in @('budgets.json', 'runner.json', 'source-before.json', 'source-after.json'))
    {
        Set-Content -LiteralPath (Join-Path $root $file) -Value '{}' -Encoding utf8NoBOM
    }
    $manifest = [ordered]@{ SchemaVersion = 1; Fixture = 'synthetic scope self-test; NOT capacity evidence' }
    if ($null -ne $Family) { $manifest.Family = $Family }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'manifest.json') -Encoding utf8NoBOM
    $records = @()
    foreach ($harness in $layoutScope.Harnesses)
    {
        [IO.Directory]::CreateDirectory((Join-Path $root "binary-snapshot/$harness")) | Out-Null
        Set-Content -LiteralPath (Join-Path $root "binary-snapshot/$harness/fixture.dll") -Value 'synthetic' -Encoding utf8NoBOM
        $records += [ordered]@{ Product = $harness; Name = 'fixture.dll'; Sha256 = ('0' * 64) }
    }
    [ordered]@{ Files = $records } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $root 'binary-snapshot/binaries.json') -Encoding utf8NoBOM
    foreach ($run in 1, 2)
    {
        foreach ($campaign in $layoutScope.Campaigns)
        {
            [IO.Directory]::CreateDirectory((Join-Path $root "run-$run/$campaign")) | Out-Null
            Set-Content -LiteralPath (Join-Path $root "run-$run/$campaign/campaign.log") -Value 'synthetic' -Encoding utf8NoBOM
        }
    }
    return $root
}
try
{
    foreach ($product in @('Documents', 'Projections', 'Workflows', 'Jobs', 'All'))
    {
        Assert-EcosystemCapacityEvidenceScope -Product $product -Repetitions 2 `
            -EvidenceRoot (New-ScopeFixture "positive-$product" $product $product)
    }
    Assert-EcosystemCapacityEvidenceScope -Product All -Repetitions 2 -EvidenceRoot (New-ScopeFixture 'legacy-all' 'All' $null)

    $combined = New-ScopeFixture 'combined-as-family' 'All' 'All'
    foreach ($family in @('Documents', 'Projections', 'Workflows'))
    {
        Assert-ScopeRejected "all-as-$family" {
            Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $combined -Product $family -Repetitions 2 } 'wrong product scope'
    }
    $relabelled = New-ScopeFixture 'combined-relabelled' 'All' 'Documents'
    Assert-ScopeRejected 'combined-relabelled' {
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $relabelled -Product Documents -Repetitions 2 } 'exactly the'
    $projections = New-ScopeFixture 'projections-as-documents' 'Projections' 'Documents'
    Assert-ScopeRejected 'other-family-layout' {
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $projections -Product Documents -Repetitions 2 } 'exactly the'
    $documents = New-ScopeFixture 'documents-as-workflows' 'Documents' 'Documents'
    Assert-ScopeRejected 'other-family-manifest' {
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $documents -Product Workflows -Repetitions 2 } 'wrong product scope'
    $lowercase = New-ScopeFixture 'lowercase-family' 'Documents' 'documents'
    Assert-ScopeRejected 'lowercase-family' {
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $lowercase -Product Documents -Repetitions 2 } 'wrong product scope'
    $unlabelled = New-ScopeFixture 'unlabelled-family' 'Documents' $null
    Assert-ScopeRejected 'unlabelled-family' {
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $unlabelled -Product Documents -Repetitions 2 } 'wrong product scope'
    $missingRun = New-ScopeFixture 'missing-campaign' 'Projections' 'Projections'
    Remove-Item -LiteralPath (Join-Path $missingRun 'run-2/projections-overload') -Recurse -Force
    Assert-ScopeRejected 'missing-campaign' {
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $missingRun -Product Projections -Repetitions 2 } 'run 2'
    $extraRun = New-ScopeFixture 'extra-repetition' 'Workflows' 'Workflows'
    [IO.Directory]::CreateDirectory((Join-Path $extraRun 'run-3/jobs-workflows')) | Out-Null
    Assert-ScopeRejected 'extra-repetition' {
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $extraRun -Product Workflows -Repetitions 2 } 'top-level'
    $extraFile = New-ScopeFixture 'extra-file' 'Documents' 'Documents'
    Set-Content -LiteralPath (Join-Path $extraFile 'all-campaign.json') -Value '{}' -Encoding utf8NoBOM
    Assert-ScopeRejected 'extra-file' {
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $extraFile -Product Documents -Repetitions 2 } 'top-level'
    $foreignBinary = New-ScopeFixture 'foreign-binary-record' 'Documents' 'Documents'
    [ordered]@{ Files = @(
            [ordered]@{ Product = 'documents'; Name = 'fixture.dll'; Sha256 = ('0' * 64) },
            [ordered]@{ Product = 'jobs'; Name = 'fixture.dll'; Sha256 = ('0' * 64) }) } |
        ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $foreignBinary 'binary-snapshot/binaries.json') -Encoding utf8NoBOM
    Assert-ScopeRejected 'foreign-binary-record' {
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $foreignBinary -Product Documents -Repetitions 2 } 'binary manifest'
    $foreignHarness = New-ScopeFixture 'foreign-harness' 'Documents' 'Documents'
    [IO.Directory]::CreateDirectory((Join-Path $foreignHarness 'binary-snapshot/projections')) | Out-Null
    Assert-ScopeRejected 'foreign-harness' {
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $foreignHarness -Product Documents -Repetitions 2 } 'binary snapshot'
}
finally
{
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedRoot).StartsWith(
            'bluetusk-ecosystem-scope-tests-', [StringComparison]::Ordinal)))
    {
        throw 'Refusing to remove an ecosystem scope test directory outside the explicit temporary root.'
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

Write-Output (
    'Jobs release and per-family ecosystem capacity source contracts passed with ' +
    "$rejected rejected scope substitutions; no workload was run or qualified.")
