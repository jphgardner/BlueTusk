[CmdletBinding()]
param()

# Offline self-test for the expansion failover gates. It checks each implemented family's manual
# exact-candidate workflow contract, the pre-registered ceilings, the readiness mapping, and that
# the report verifier accepts complete synthetic evidence but rejects every listed substitution.
# Synthetic inputs exercise the verifier only; they never certify a failover run.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$policyPath = Join-Path $PSScriptRoot 'expansion-failover-policy.json'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json -Depth 40
$releasePolicy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'expansion-release-policy.json') -Raw | ConvertFrom-Json
$jobsPolicy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'jobs-release-failover-policy.json') -Raw | ConvertFrom-Json
$verifier = Join-Path $PSScriptRoot 'verify-expansion-failover-report.ps1'
$archiveVerifier = Join-Path $PSScriptRoot 'verify-expansion-release-failover.ps1'
Import-Module (Join-Path $PSScriptRoot 'expansion-candidate-evidence.psm1') -Force
$implemented = @('Projections', 'Documents', 'Workflows')
$commit = 'a' * 40
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-expansion-failover-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$fixturePath = Join-Path $temporaryRoot 'report.json'

function Fail([string] $Message) { throw "Expansion failover self-test: $Message" }
function Copy-Deep($Value) { return $Value | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100 }
function Hex([int] $Seed, [int] $Length) { return (('{0:x2}' -f ($Seed % 256)) * $Length).Substring(0, $Length) }

function New-StepReport([string] $Family, $StepPolicy, [int] $Run, [string] $Fixture)
{
    $familyPolicy = $policy.families.PSObject.Properties[$Family].Value
    $stepScenarios = @($familyPolicy.scenarios | Where-Object { [string]$_.step -ceq [string]$StepPolicy.name })
    switch -CaseSensitive ([string]$StepPolicy.kind)
    {
        'harness'
        {
            $scenarios = foreach ($scenario in $stepScenarios)
            {
                [ordered]@{
                    Name = [string]$scenario.name; Fault = 'synthetic'; DeliverySemantics = 'synthetic'; Tenants = 2
                    AcknowledgedOperations = [int]$scenario.acknowledgedOperations
                    VerifiedOperations = [int]$scenario.acknowledgedOperations
                    ExpectedEffects = [int]$scenario.acknowledgedOperations + 1
                    ObservedEffects = [int]$scenario.acknowledgedOperations + 1
                    InFlightAtomic = $true; InFlightCommitted = [bool]$scenario.inFlightMustCommit; NoCrossTenantReads = $true
                    StaleOwnerRejected = $true; BeforeFence = 1; AfterFence = 2
                    BeforeSystemIdentifier = "76900000000000000$Run"; AfterSystemIdentifier = "76900000000000000$Run"
                    BeforeTimeline = 1; AfterTimeline = 1 + [int]$scenario.timelineAdvance
                    FaultToFirstSuccessMilliseconds = 1000; FaultToVerifiedMilliseconds = 2000
                    Checks = @($scenario.requiredChecks)
                }
            }
            return [ordered]@{
                FormatVersion = 1; Family = $Family; Fixture = $Fixture; Server = 'PostgreSQL 18.0 synthetic'
                Image = [string]$policy.postgreSqlImage; SourceDurability = 'synthetic'; Scenarios = @($scenarios)
                ProductionQualified = $false; Qualification = 'synthetic'
            }
        }
        'xunit'
        {
            if ($Family -ceq 'Projections')
            {
                # The retained rehearsal record, rebound to the policy image.
                $report = Get-Content -LiteralPath (Join-Path $root 'docs/projections/evidence/physical-promotion-pg18.json') -Raw | ConvertFrom-Json -Depth 50
                $report.PostgreSqlImage = [string]$policy.postgreSqlImage
                return $report
            }
            $retained = Get-Content -LiteralPath (Join-Path $root 'docs/jobs/performance-reports/optimized-physical-promotion-pg18.json') -Raw | ConvertFrom-Json -Depth 100
            $report = Copy-Deep $retained.Runs[$Run - 1].Report
            $report.Fixture = $Fixture
            $report.Image = [string]$policy.postgreSqlImage
            return $report
        }
        'console'
        {
            $faults = foreach ($scenario in $stepScenarios)
            {
                [ordered]@{
                    Name = [string]$scenario.name; Passed = $true; RecoveryMilliseconds = 500; Attempts = [int]$scenario.attempts
                    DurableEffects = 1; CommitAcknowledgementDropped = ([string]$scenario.name -ceq 'ambiguous-commit-lost-server-ack')
                }
            }
            return [ordered]@{ Profile = 'faults'; PostgreSqlVersion = 'PostgreSQL 18.0 synthetic'; Cases = @(); Faults = @($faults); Overload = @() }
        }
    }
}

function New-SyntheticReport([string] $Family)
{
    $familyPolicy = $policy.families.PSObject.Properties[$Family].Value
    $slug = $Family.ToLowerInvariant()
    $runs = for ($run = 1; $run -le [int]$policy.repetitions; $run++)
    {
        $fixture = "$slug-fo-$(Hex $run 20)-run$run"
        $steps = foreach ($step in @($familyPolicy.steps))
        {
            $files = @($step.requiredBinaries | ForEach-Object -Begin { $seed = 10 } -Process { $seed++; [ordered]@{ Name = [string]$_; Bytes = 100 + $seed; Sha256 = Hex $seed 64 } })
            [ordered]@{
                Name = [string]$step.name; Kind = [string]$step.kind
                Report = New-StepReport $Family $step $run $fixture
                ReportPath = "artifacts/$slug-release-failover/1-1/raw/run$run/$($step.name).json"
                LogPath = "artifacts/$slug-release-failover/1-1/raw/run$run/$($step.name).log"
                TrxPath = $(if ([string]$step.kind -ceq 'xunit') { "artifacts/$slug-release-failover/1-1/raw/run$run/$($step.name).trx" } else { $null })
                BinariesBefore = [ordered]@{ Files = $files }; BinariesAfter = [ordered]@{ Files = $files }; BinariesUnchanged = $true
            }
        }
        [ordered]@{
            Repetition = $run
            Metadata = [ordered]@{
                Family = $Family; Fixture = $fixture; Owner = 'bluetusk.expansion.failover'; ImageDigest = [string]$policy.postgreSqlImage
                ImageInspect = [ordered]@{ Id = 'sha256:' + (Hex 7 64); RepoDigests = @([string]$policy.postgreSqlImage) }
                BeforePrimarySettings = @('fsync=on', 'full_page_writes=on', 'max_connections=100', 'synchronous_commit=remote_apply',
                    "synchronous_standby_names=FIRST 1 ($($familyPolicy.fixture.standbyApplicationName))", "wal_level=$($familyPolicy.fixture.walLevel)")
            }
            Steps = @($steps)
            FixtureSamplePath = "artifacts/$slug-release-failover/1-1/raw/run$run/fixture.jsonl"
        }
    }
    $source = [ordered]@{ formatVersion = 1; commit = $commit; dirty = $false; sourceTreeSha256 = Hex 3 64; fileCount = 10; sourceBytes = 1000; productionQualified = $false }
    return Copy-Deep ([ordered]@{
        Family = $Family; Campaign = 'synthetic'; Configuration = 'Release'; Repetitions = [int]$policy.repetitions; Runs = @($runs)
        Sources = @(); SourceProvenance = [ordered]@{ Before = $source; After = $source; Unchanged = $true }
        InstalledRuntime = @(); ScriptSha256 = Hex 5 64; ProductionQualified = $false; Qualification = 'synthetic'
    })
}

function Assert-Accepted([string] $Family, $Report)
{
    $Report | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $fixturePath -Encoding utf8
    & $verifier -Family $Family -ReportPath $fixturePath -ExpectedCommit $commit | Out-Null
}
function Assert-Rejected([string] $Family, $Report, [string] $Expected, [string] $Case)
{
    $Report | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $fixturePath -Encoding utf8
    $failure = $null
    try { & $verifier -Family $Family -ReportPath $fixturePath -ExpectedCommit $commit | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($Expected))
    {
        Fail "$Family '$Case' expected a rejection containing '$Expected'; received '$failure'."
    }
}
function Mutate([string] $Family, [string] $Case, [string] $Expected, [scriptblock] $Change)
{
    $report = New-SyntheticReport $Family
    & $Change $report
    Assert-Rejected $Family $report $Expected $Case
}
function Harness($Report, [int] $Run, [string] $Scenario)
{
    $step = @($Report.Runs[$Run].Steps | Where-Object { [string]$_.Kind -ceq 'harness' })[0]
    return @($step.Report.Scenarios | Where-Object { [string]$_.Name -ceq $Scenario })[0]
}
function Step($Report, [int] $Run, [string] $Name) { return @($Report.Runs[$Run].Steps | Where-Object { [string]$_.Name -ceq $Name })[0] }

try
{
    # Pre-registered ceilings may only stay equal or tighten; the Jobs peer ceiling is unchanged.
    if ([int]$jobsPolicy.maximumRecoveryMilliseconds -ne 180000) { Fail 'the Jobs peer recovery ceiling changed.' }
    if ([string]$policy.postgreSqlImage -cnotmatch '^postgres@sha256:[0-9a-f]{64}$' -or [int]$policy.repetitions -lt 3) { Fail 'the fixture image or repetition floor weakened.' }
    $ceilings = @{ 'backend-termination' = 45000; 'host-process-kill' = 45000; 'primary-crash-restart' = 60000; 'synchronous-standby-promotion' = 60000 }
    foreach ($family in $implemented)
    {
        $familyPolicy = $policy.families.PSObject.Properties[$family].Value
        $slug = $family.ToLowerInvariant()
        if ($null -eq $familyPolicy) { Fail "$family has no failover policy." }
        foreach ($gap in @('asynchronous-replication-loss', 'split-brain', 'old-primary-rejoin', 'persistent-storage-corruption-or-loss', 'independent-host-fleet'))
        {
            if ($gap -cnotin @($familyPolicy.unqualifiedDisturbances)) { Fail "$family no longer declares untested '$gap'." }
        }
        if ([string]::IsNullOrWhiteSpace([string]$familyPolicy.failoverSemantics)) { Fail "$family lacks its documented failover semantics." }
        foreach ($scenario in @($familyPolicy.scenarios))
        {
            $verified = [int]$scenario.maximumFaultToVerifiedMilliseconds
            $kind = if ($null -ne $scenario.PSObject.Properties['verifier']) { [string]$scenario.verifier } else { 'harness' }
            $limit = if ($kind -ceq 'projections-physical-promotion') { 120000 } elseif ($kind -ceq 'workflows-fault') { 45000 } else { 180000 }
            if ($verified -le 0 -or $verified -gt $limit) { Fail "$family '$($scenario.name)' weakened its verified-recovery ceiling." }
            if ($null -ne $scenario.PSObject.Properties['maximumFaultToFirstSuccessMilliseconds'])
            {
                $first = [int]$scenario.maximumFaultToFirstSuccessMilliseconds
                if ($first -le 0 -or $first -gt $ceilings[[string]$scenario.name] -or $first -gt $verified) { Fail "$family '$($scenario.name)' weakened its first-success ceiling." }
                if (@($scenario.requiredChecks).Count -lt 8 -or [int]$scenario.acknowledgedOperations -lt 16) { Fail "$family '$($scenario.name)' dropped assertions or acknowledged work." }
            }
        }

        $workflowFile = ".github/workflows/$slug-release-failover.yml"
        if ([string]$releasePolicy.families.$family.failoverWorkflow -cne "$slug-release-failover.yml") { Fail "$family release policy names another failover workflow." }
        $workflow = Get-Content -LiteralPath (Join-Path $root $workflowFile) -Raw
        if ($workflow -notmatch '(?m)^  workflow_dispatch:\s*$' -or
            $workflow -match '(?m)^  (?:push|pull_request|schedule|workflow_run|workflow_call):\s*$' -or
            $workflow -notmatch [regex]::Escape('runs-on: [self-hosted, windows, x64, bluetusk-benchmark]') -or
            $workflow -notmatch [regex]::Escape('ref: ${{ inputs.candidate_sha }}') -or
            $workflow -notmatch [regex]::Escape('group: bluetusk-reference-host') -or
            $workflow -notmatch "RUN-$($family.ToUpperInvariant())-RELEASE-FAILOVER" -or
            $workflow -notmatch [regex]::Escape("EVIDENCE_ROOT: artifacts/$slug-release-failover/") -or
            $workflow -notmatch "name: $slug-failover-partial-")
        {
            Fail "$family failover workflow lost its exact manual candidate contract."
        }
        $artifacts = @([regex]::Matches($workflow, '(?m)^\s+name: expansion-[a-z]+-[a-z]+-\$\{\{ inputs\.candidate_sha \}\}\s*$'))
        if ($artifacts.Count -ne 1 -or $artifacts[0].Value.Trim() -cne ('name: expansion-' + $slug + '-failover-${{ inputs.candidate_sha }}')) { Fail "$family failover workflow must upload exactly one expansion artifact." }
        foreach ($mode in @('Preflight', 'Run', 'Verify'))
        {
            if ($workflow -notmatch [regex]::Escape("./eng/verify-expansion-release-failover.ps1 -Family $family -Mode $mode ")) { Fail "$family failover omits its $mode evidence step." }
        }
        $mapped = Get-ExpansionRoleVerifier -Family $family -Role 'failover'
        if ($mapped.Script -cne 'verify-expansion-release-failover.ps1' -or $mapped.EvidenceParameter -cne 'EvidenceRoot' -or
            $mapped.Arguments.Family -cne $family -or $mapped.Arguments.Mode -cne 'Verify' -or $null -ne $mapped.ProducerRunParameter) { Fail "$family failover readiness mapping changed." }

        # The archived verifier refuses evidence outside this workflow's own artifact directory.
        $outside = $null
        try { & $archiveVerifier -Family $family -Mode Verify -ExpectedCommit $commit -EvidenceRoot 'artifacts/jobs-release-failover/substituted' | Out-Null }
        catch { $outside = $_.Exception.Message }
        if ($null -eq $outside -or -not $outside.Contains("artifacts/$slug-release-failover/")) { Fail "$family archive verifier accepted another workflow's directory." }

        Assert-Accepted $family (New-SyntheticReport $family)
        Mutate $family 'dirty-source' 'dirty, changed or not the exact candidate' { param($r) $r.SourceProvenance.Before.dirty = $true }
        Mutate $family 'other-commit' 'dirty, changed or not the exact candidate' { param($r) $r.SourceProvenance.After.commit = 'b' * 40 }
        Mutate $family 'overclaim' 'overclaims qualification' { param($r) $r.ProductionQualified = $true }
        Mutate $family 'two-repetitions' 'incomplete repetitions' { param($r) $r.Runs = @($r.Runs[0], $r.Runs[1]) }
        Mutate $family 'reused-fixture' 'reused or misnamed its fixture' { param($r) $r.Runs[1].Metadata.Fixture = $r.Runs[0].Metadata.Fixture }
        Mutate $family 'tag-image' 'unverified PostgreSQL image' { param($r) $r.Runs[0].Metadata.ImageDigest = 'postgres:18-alpine' }
        Mutate $family 'async-primary' 'synchronous durability' { param($r) $r.Runs[2].Metadata.BeforePrimarySettings = @('fsync=on', 'full_page_writes=on', 'synchronous_commit=on') }
        Mutate $family 'changed-binary' 'changed a binary' { param($r) $r.Runs[1].Steps[0].BinariesAfter.Files[0].Sha256 = Hex 99 64 }
        Mutate $family 'missing-dependency' 'lacks executable dependency' { param($r) $r.Runs[0].Steps[0].BinariesBefore.Files = @($r.Runs[0].Steps[0].BinariesBefore.Files | Select-Object -Skip 1); $r.Runs[0].Steps[0].BinariesAfter.Files = $r.Runs[0].Steps[0].BinariesBefore.Files }
        Mutate $family 'other-family' 'incomplete repetitions or overclaims' { param($r) $r.Family = 'Jobs' }
        if (@($familyPolicy.steps).Count -gt 1)
        {
            Mutate $family 'reordered-steps' 'out of policy order' { param($r) $r.Runs[0].Steps = @($r.Runs[0].Steps[1], $r.Runs[0].Steps[0]) }
        }
    }

    foreach ($family in @('Projections', 'Documents'))
    {
        Mutate $family 'duplicated-effect' 'lost or duplicated committed effects' { param($r) (Harness $r 0 'backend-termination').ObservedEffects += 1 }
        Mutate $family 'lost-acknowledged' 'lost acknowledged work' { param($r) (Harness $r 1 'host-process-kill').VerifiedOperations -= 1 }
        Mutate $family 'smaller-admission' 'lost acknowledged work' { param($r) $s = Harness $r 2 'primary-crash-restart'; $s.AcknowledgedOperations -= 1; $s.VerifiedOperations -= 1 }
        Mutate $family 'partial-in-flight' 'partial or unrecovered in-flight work' { param($r) (Harness $r 0 'host-process-kill').InFlightAtomic = $false }
        Mutate $family 'cross-tenant' 'tenant isolation' { param($r) (Harness $r 1 'backend-termination').NoCrossTenantReads = $false }
        Mutate $family 'stale-owner' 'stale owner' { param($r) (Harness $r 2 'host-process-kill').StaleOwnerRejected = $false }
        Mutate $family 'fence-regressed' 'did not advance its fence' { param($r) (Harness $r 0 'primary-crash-restart').AfterFence = 1 }
        Mutate $family 'slow-first-success' 'pre-registered recovery ceiling' { param($r) (Harness $r 0 'backend-termination').FaultToFirstSuccessMilliseconds = 45001 }
        Mutate $family 'slow-restart' 'pre-registered recovery ceiling' { param($r) (Harness $r 1 'primary-crash-restart').FaultToFirstSuccessMilliseconds = 60001 }
        Mutate $family 'slow-verify' 'pre-registered recovery ceiling' { param($r) (Harness $r 2 'backend-termination').FaultToVerifiedMilliseconds = 180001 }
        Mutate $family 'unmeasured' 'pre-registered recovery ceiling' { param($r) (Harness $r 2 'host-process-kill').FaultToFirstSuccessMilliseconds = 0 }
        Mutate $family 'dropped-check' 'exactly its policy assertions' { param($r) $s = Harness $r 0 'primary-crash-restart'; $s.Checks = @($s.Checks | Select-Object -Skip 1) }
        Mutate $family 'renamed-check' 'exactly its policy assertions' { param($r) $s = Harness $r 1 'host-process-kill'; $s.Checks[0] = 'assumed synchronous standby' }
        Mutate $family 'restart-changed-timeline' 'unexpected server or timeline' { param($r) (Harness $r 0 'primary-crash-restart').AfterTimeline = 2 }
        Mutate $family 'other-server' 'unexpected server or timeline' { param($r) (Harness $r 1 'backend-termination').AfterSystemIdentifier = '1' }
        Mutate $family 'reused-pair' 'distinct fresh PostgreSQL system' { param($r) foreach ($s in @(@($r.Runs[1].Steps | Where-Object { $_.Kind -ceq 'harness' })[0].Report.Scenarios)) { $s.BeforeSystemIdentifier = '769000000000000001'; $s.AfterSystemIdentifier = '769000000000000001' } }
        Mutate $family 'skipped-scenario' 'different scenario set' { param($r) $step = @($r.Runs[0].Steps | Where-Object { $_.Kind -ceq 'harness' })[0]; $step.Report.Scenarios = @($step.Report.Scenarios | Select-Object -Skip 1) }
        Mutate $family 'other-fixture-report' 'not bound to this fixture' { param($r) @($r.Runs[0].Steps | Where-Object { $_.Kind -ceq 'harness' })[0].Report.Fixture = $r.Runs[1].Metadata.Fixture }
        Mutate $family 'family-substitution' 'not bound to this fixture' { param($r) @($r.Runs[2].Steps | Where-Object { $_.Kind -ceq 'harness' })[0].Report.Family = 'Search' }
    }
    Mutate 'Projections' 'unrecovered-in-flight' 'partial or unrecovered in-flight work' { param($r) (Harness $r 1 'backend-termination').InFlightCommitted = $false }
    Mutate 'Documents' 'promotion-without-new-timeline' 'unexpected server or timeline' { param($r) (Harness $r 0 'synchronous-standby-promotion').AfterTimeline = 1 }
    Mutate 'Projections' 'promotion-lost-inbox-effect' 'lost or duplicated acknowledged effects' { param($r) (Step $r 0 'physical-promotion').Report.ExactlyOnceWalInboxEffects = 15 }
    Mutate 'Projections' 'promotion-stale-live' 'stale owner or a changed timeline' { param($r) (Step $r 1 'physical-promotion').Report.StaleLiveAppendRejected = $false }
    Mutate 'Projections' 'promotion-transparent' 'explicit recovery' { param($r) (Step $r 2 'physical-promotion').Report.RecoveryTransparent = $true }
    Mutate 'Projections' 'promotion-slow' 'pre-registered recovery ceiling' { param($r) (Step $r 0 'physical-promotion').Report.PromotionAndRecoveryElapsedMilliseconds = 120001 }
    Mutate 'Projections' 'promotion-other-image' 'did not promote the synchronous standby' { param($r) (Step $r 1 'physical-promotion').Report.PostgreSqlImage = 'postgres:18' }
    Mutate 'Workflows' 'workflow-effect-duplicated' 'lost or duplicated acknowledged workflow effects' { param($r) (Step $r 0 'physical-promotion').Report.WorkflowEffects = 13 }
    Mutate 'Workflows' 'workflow-stale-owner' 'stale workflow owner' { param($r) @((Step $r 1 'physical-promotion').Report.Leases | Where-Object { $_.Product -ceq 'Workflows' })[0].StaleEffectRejected = $false }
    Mutate 'Workflows' 'workflow-replay-mismatch' 'lost workflow replay' { param($r) (Step $r 2 'physical-promotion').Report.Workflows[0].ReplayMatches = $false }
    Mutate 'Workflows' 'workflow-other-fixture' 'did not promote the synchronous standby of this fixture' { param($r) (Step $r 0 'physical-promotion').Report.Fixture = 'jobs-physical-0-run1' }
    Mutate 'Workflows' 'workflow-slow-promotion' 'pre-registered recovery ceiling' { param($r) (Step $r 1 'physical-promotion').Report.PrimaryKillToDrainedMilliseconds = 180001 }
    Mutate 'Workflows' 'fault-duplicated-effect' 'lost, duplicated or unfenced' { param($r) (Step $r 0 'fault-harness').Report.Faults[1].DurableEffects = 2 }
    Mutate 'Workflows' 'fault-unfenced-attempt' 'lost, duplicated or unfenced' { param($r) (Step $r 1 'fault-harness').Report.Faults[0].Attempts = 1 }
    Mutate 'Workflows' 'fault-ack-not-dropped' 'did not actually drop the commit acknowledgement' { param($r) @((Step $r 2 'fault-harness').Report.Faults | Where-Object { $_.Name -ceq 'ambiguous-commit-lost-server-ack' })[0].CommitAcknowledgementDropped = $false }
    Mutate 'Workflows' 'fault-skipped' 'different scenario set' { param($r) $s = Step $r 0 'fault-harness'; $s.Report.Faults = @($s.Report.Faults | Select-Object -Skip 1) }
    Mutate 'Workflows' 'fault-slow' 'pre-registered recovery ceiling' { param($r) (Step $r 1 'fault-harness').Report.Faults[2].RecoveryMilliseconds = 45001 }
    Mutate 'Workflows' 'fault-other-server' 'different scenario set' { param($r) (Step $r 2 'fault-harness').Report.PostgreSqlVersion = 'PostgreSQL 15.4' }

    foreach ($family in @('Events', 'Schema', 'Sql', 'Studio', 'Search', 'Edge'))
    {
        $failure = $null
        try { Get-ExpansionRoleVerifier -Family $family -Role 'failover' | Out-Null } catch { $failure = $_.Exception.Message }
        if ($null -eq $failure -or -not $failure.Contains('fails closed')) { Fail "$family failover must fail closed until its gate exists." }
    }
    Write-Output 'Expansion failover self-test passed on synthetic evidence; no disturbance was run or qualified.'
}
finally
{
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedRoot).StartsWith('bluetusk-expansion-failover-', [StringComparison]::Ordinal)))
    {
        throw 'Refusing to remove a failover self-test directory outside the explicit temporary root.'
    }
    foreach ($file in [IO.Directory]::GetFiles($resolvedRoot)) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($resolvedRoot)
}
