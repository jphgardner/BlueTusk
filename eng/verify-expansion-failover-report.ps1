[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Documents', 'Projections', 'Workflows', IgnoreCase = $false)]
    [string] $Family,
    [Parameter(Mandatory)][string] $ReportPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedCommit,
    [string] $PolicyPath = (Join-Path $PSScriptRoot 'expansion-failover-policy.json')
)

# Judges one aggregated failover report against the family policy: exact candidate source, three
# fresh pairs, unchanged binaries, every policy scenario with its exact acknowledged work, no lost
# or duplicated effects, atomic in-flight work, stale-owner fences, tenant isolation, the expected
# server timeline and recovery within the pre-registered ceilings. It reads only the report.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -Depth 40
$evidence = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json -Depth 100

function Require([bool] $Condition, [string] $Message)
{
    if (-not $Condition) { throw $Message }
}
function Value($Object, [string] $Name)
{
    Require ($null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]) "Failover evidence is missing '$Name'."
    return $Object.PSObject.Properties[$Name].Value
}
function Within($Measurement, $Maximum, [string] $Context)
{
    Require ($null -ne $Measurement -and $null -ne $Maximum) "$Context lacks a recovery measurement or ceiling."
    $value = [double]$Measurement
    Require ([double]::IsFinite($value) -and $value -gt 0 -and $value -le [double]$Maximum) "$Context exceeded its pre-registered recovery ceiling of $Maximum ms."
}

$familyPolicy = $policy.families.PSObject.Properties[$Family].Value
Require ($policy.schemaVersion -eq 1 -and $null -ne $familyPolicy -and [int]$policy.repetitions -ge 3 -and
    [string]$policy.postgreSqlImage -cmatch '^postgres@sha256:[0-9a-f]{64}$') "Unsupported $Family failover policy."
$repetitions = [int]$policy.repetitions
$slug = $Family.ToLowerInvariant()
$image = [string]$policy.postgreSqlImage
$standbyName = [string]$familyPolicy.fixture.standbyApplicationName
Require ([string](Value $evidence 'Family') -ceq $Family -and (Value $evidence 'ProductionQualified') -eq $false -and
    [int](Value $evidence 'Repetitions') -eq $repetitions -and @(Value $evidence 'Runs').Count -eq $repetitions) (
    "$Family failover report has incomplete repetitions or overclaims qualification.")
$before = $evidence.SourceProvenance.Before
$after = $evidence.SourceProvenance.After
Require ($evidence.SourceProvenance.Unchanged -eq $true -and
    $before.dirty -eq $false -and $after.dirty -eq $false -and
    [string]$before.commit -ieq $ExpectedCommit -and [string]$after.commit -ieq $ExpectedCommit -and
    [string]$before.sourceTreeSha256 -match '^[0-9a-fA-F]{64}$' -and
    [string]$before.sourceTreeSha256 -ieq [string]$after.sourceTreeSha256 -and
    [int]$before.fileCount -gt 0 -and [int]$before.fileCount -eq [int]$after.fileCount) (
    "$Family failover source is dirty, changed or not the exact candidate.")
Require ([string](Value $evidence 'ScriptSha256') -match '^[0-9a-fA-F]{64}$') "$Family failover runner hash is missing."

$policySteps = @($familyPolicy.steps)
$scenarios = @($familyPolicy.scenarios)
$fixtures = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$systems = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
for ($index = 1; $index -le $repetitions; $index++)
{
    $matching = @($evidence.Runs | Where-Object { [int]$_.Repetition -eq $index })
    Require ($matching.Count -eq 1) "$Family failover repetition $index is missing or duplicated."
    $run = $matching[0]
    $label = "$Family failover repetition $index"
    $fixture = [string]$run.Metadata.Fixture
    Require ($fixtures.Add($fixture) -and $fixture -cmatch "^$slug-fo-[0-9a-f]{20}-run$index$") "$label reused or misnamed its fixture."
    Require ([string]$run.Metadata.ImageDigest -ceq $image -and
        @($run.Metadata.ImageInspect.RepoDigests | Where-Object { [string]$_ -ceq $image }).Count -gt 0) "$label used an unverified PostgreSQL image."
    $settings = @($run.Metadata.BeforePrimarySettings | ForEach-Object { [string]$_ })
    Require ('fsync=on' -in $settings -and 'full_page_writes=on' -in $settings -and
        'synchronous_commit=remote_apply' -in $settings -and
        "synchronous_standby_names=FIRST 1 ($standbyName)" -in $settings) "$label lacks synchronous durability settings."
    $steps = @($run.Steps)
    Require ($steps.Count -eq $policySteps.Count) "$label does not contain exactly the policy steps."
    for ($position = 0; $position -lt $policySteps.Count; $position++)
    {
        $step = $steps[$position]
        $expected = $policySteps[$position]
        Require ([string]$step.Name -ceq [string]$expected.name -and [string]$step.Kind -ceq [string]$expected.kind) "$label ran its steps out of policy order."
        Require ($step.BinariesUnchanged -eq $true -and @($step.BinariesBefore.Files).Count -gt 0 -and
            @($step.BinariesBefore.Files).Count -eq @($step.BinariesAfter.Files).Count) "$label step '$($step.Name)' changed its executable set."
        foreach ($file in @($step.BinariesBefore.Files))
        {
            $afterFiles = @($step.BinariesAfter.Files | Where-Object { [string]$_.Name -ceq [string]$file.Name })
            Require ($afterFiles.Count -eq 1 -and [string]$file.Sha256 -match '^[0-9a-f]{64}$' -and
                [string]$afterFiles[0].Sha256 -ceq [string]$file.Sha256 -and [long]$afterFiles[0].Bytes -eq [long]$file.Bytes) (
                "$label step '$($step.Name)' changed a binary.")
        }
        foreach ($required in @($expected.requiredBinaries))
        {
            Require (@($step.BinariesBefore.Files | Where-Object { [string]$_.Name -ceq [string]$required }).Count -eq 1) (
                "$label step '$($step.Name)' lacks executable dependency '$required'.")
        }
        Require (($expected.kind -cne 'xunit') -or [string]$step.TrxPath -cmatch '\.trx$') "$label step '$($step.Name)' lacks its test result."
    }

    foreach ($stepPolicy in $policySteps)
    {
        $step = @($steps | Where-Object { [string]$_.Name -ceq [string]$stepPolicy.name })[0]
        $report = $step.Report
        $stepScenarios = @($scenarios | Where-Object { [string]$_.step -ceq [string]$stepPolicy.name })
        Require ($stepScenarios.Count -gt 0) "Policy step '$($stepPolicy.name)' qualifies no scenario."
        switch -CaseSensitive ([string]$stepPolicy.kind)
        {
            'harness'
            {
                Require ([int](Value $report 'FormatVersion') -eq 1 -and [string]$report.Family -ceq $Family -and
                    [string]$report.Fixture -ceq $fixture -and [string]$report.Image -ceq $image -and
                    [string]$report.Server -match '^PostgreSQL 18\.' -and $report.ProductionQualified -eq $false) "$label harness report is not bound to this fixture."
                $reported = @($report.Scenarios)
                Require ($reported.Count -eq $stepScenarios.Count -and
                    @($reported | ForEach-Object { [string]$_.Name } | Sort-Object -Unique).Count -eq $reported.Count) "$label harness ran a different scenario set."
                foreach ($scenario in $stepScenarios)
                {
                    $context = "$label scenario '$($scenario.name)'"
                    $result = @($reported | Where-Object { [string]$_.Name -ceq [string]$scenario.name })
                    Require ($result.Count -eq 1) "$context is missing."
                    $result = $result[0]
                    $acknowledged = [int]$scenario.acknowledgedOperations
                    Require ($acknowledged -gt 0 -and [int]$result.AcknowledgedOperations -eq $acknowledged -and
                        [int]$result.VerifiedOperations -eq $acknowledged) "$context lost acknowledged work or changed its admission."
                    Require ([long]$result.ExpectedEffects -ge $acknowledged -and
                        [long]$result.ObservedEffects -eq [long]$result.ExpectedEffects) "$context lost or duplicated committed effects."
                    Require ($result.InFlightAtomic -eq $true -and
                        (-not $scenario.inFlightMustCommit -or $result.InFlightCommitted -eq $true)) "$context left partial or unrecovered in-flight work."
                    Require ($result.NoCrossTenantReads -eq $true -and [int]$result.Tenants -ge 2) "$context failed tenant isolation."
                    if ($scenario.fenced)
                    {
                        Require ($result.StaleOwnerRejected -eq $true -and [long]$result.AfterFence -gt [long]$result.BeforeFence -and
                            [long]$result.BeforeFence -gt 0) "$context accepted a stale owner or did not advance its fence."
                    }
                    Require ([string]$result.BeforeSystemIdentifier -match '^[0-9]+$' -and
                        [string]$result.AfterSystemIdentifier -ceq [string]$result.BeforeSystemIdentifier -and
                        [int]$result.BeforeTimeline -ge 1 -and
                        [int]$result.AfterTimeline -eq ([int]$result.BeforeTimeline + [int]$scenario.timelineAdvance)) "$context ran on an unexpected server or timeline."
                    [void]$systems.Add("$index|$($result.BeforeSystemIdentifier)")
                    Within $result.FaultToFirstSuccessMilliseconds $scenario.maximumFaultToFirstSuccessMilliseconds "$context first success"
                    Within $result.FaultToVerifiedMilliseconds $scenario.maximumFaultToVerifiedMilliseconds "$context verified recovery"
                    Require ([double]$result.FaultToFirstSuccessMilliseconds -le [double]$result.FaultToVerifiedMilliseconds) "$context reported inconsistent recovery timings."
                    $checks = @($result.Checks | ForEach-Object { [string]$_ })
                    $required = @($scenario.requiredChecks | ForEach-Object { [string]$_ })
                    Require ($checks.Count -eq $required.Count -and
                        @($checks | Sort-Object -Unique -CaseSensitive).Count -eq $checks.Count -and
                        @(Compare-Object -ReferenceObject $required -DifferenceObject $checks -CaseSensitive).Count -eq 0) (
                        "$context did not run exactly its policy assertions.")
                }
            }
            'xunit'
            {
                Require ($stepScenarios.Count -eq 1) "Policy xunit step '$($stepPolicy.name)' must qualify exactly one scenario."
                $scenario = $stepScenarios[0]
                $context = "$label scenario '$($scenario.name)'"
                switch -CaseSensitive ([string]$scenario.verifier)
                {
                    'projections-physical-promotion'
                    {
                        Require ([string]$report.Scenario -ceq 'synchronous-physical-standby-promotion-and-controlled-ddl-rebuild' -and
                            [int]$report.PostgreSqlMajor -eq 18 -and [string]$report.PostgreSqlImage -ceq $image -and
                            $report.SameSystemIdentifier -eq $true -and [int]$report.BeforeTimeline -ge 1 -and
                            [int]$report.AfterTimeline -eq ([int]$report.BeforeTimeline + 1)) "$context did not promote the synchronous standby of this fixture."
                        Require ([int]$report.AcknowledgedBusinessCommits -eq 8 -and [int]$report.PreservedOutboxEvents -eq 16 -and
                            [int]$report.ExactlyOnceWalInboxEffects -eq 16 -and [int]$report.RecoveredReplayEffects -eq 8) "$context lost or duplicated acknowledged effects."
                        Require ([long]$report.NewLiveFence -gt [long]$report.OldLiveFence -and $report.OldCheckpointPreserved -eq $true -and
                            $report.StaleLiveAppendRejected -eq $true -and $report.StaleProjectionReacquisitionRejected -eq $true -and
                            $report.ChangedTimelineBindingRejected -eq $true) "$context accepted a stale owner or a changed timeline."
                        Require ([int]$report.RecoveryCutovers -eq 2 -and [int]$report.TenantFirstTotal -eq 10 -and
                            [int]$report.TenantAnotherTotal -eq 999 -and $report.LogicalSlotsCopied -eq $false -and
                            $report.RecoveryTransparent -eq $false -and $report.ControlledDdlBeforeFreshSnapshot -eq $true) "$context did not recover exact tenant totals through explicit recovery."
                        Within $report.PromotionAndRecoveryElapsedMilliseconds $scenario.maximumFaultToVerifiedMilliseconds "$context recovery"
                    }
                    'workflows-physical-promotion'
                    {
                        Require ([string]$report.Fixture -ceq $fixture -and [string]$report.Image -ceq $image -and
                            [string]$report.Server -match '^PostgreSQL 18\.' -and
                            [string]$report.BeforeSystemIdentifier -match '^[0-9]+$' -and
                            [string]$report.AfterSystemIdentifier -ceq [string]$report.BeforeSystemIdentifier -and
                            [int]$report.BeforeTimeline -ge 1 -and [int]$report.AfterTimeline -eq ([int]$report.BeforeTimeline + 1)) "$context did not promote the synchronous standby of this fixture."
                        Require ([int]$report.AcknowledgedJobs -gt 0 -and [int]$report.VerifiedJobs -eq [int]$report.AcknowledgedJobs -and
                            [int]$report.JobEffects -eq [int]$report.AcknowledgedJobs -and
                            [int]$report.AcknowledgedBusinessAdmissions -eq [int]$report.AcknowledgedJobs -and
                            [int]$report.WorkflowEffects -eq 12) "$context lost or duplicated acknowledged workflow effects."
                        $workflowLeases = @($report.Leases | Where-Object { [string]$_.Product -ceq 'Workflows' })
                        Require ($workflowLeases.Count -eq 4) "$context lacks the four fenced workflow activity leases."
                        foreach ($lease in $workflowLeases)
                        {
                            Require ([int]$lease.Attempt -eq 2 -and [long]$lease.AfterFence -gt [long]$lease.BeforeFence -and
                                $lease.StaleCompletionRejected -eq $true -and $lease.StaleEffectRejected -eq $true) "$context accepted a stale workflow owner."
                        }
                        $workflows = @($report.Workflows)
                        Require ($workflows.Count -eq 6 -and @($workflows | Where-Object { $_.ReplayMatches -ne $true }).Count -eq 0 -and
                            @($workflows | Where-Object { [string]$_.Status -ceq 'Succeeded' }).Count -eq 4 -and
                            @($workflows | Where-Object { [string]$_.Status -ceq 'Canceled' }).Count -eq 2 -and
                            @($workflows | ForEach-Object { [string]$_.Tenant } | Sort-Object -Unique).Count -eq 2) "$context lost workflow replay, timer, signal or compensation state."
                        Require ($report.NoCrossTenantReads -eq $true -and $report.UnchangedMultihostSource -eq $true -and
                            $report.UnavailableHealthDuringOutage -eq $true -and $report.HealthyAfterRecovery -eq $true) "$context failed routing, isolation or health checks."
                        [void]$systems.Add("$index|$($report.BeforeSystemIdentifier)")
                        foreach ($measurement in @($report.PrimaryKillToPromotionMilliseconds, $report.PrimaryKillToFirstEffectReplayMilliseconds,
                            $report.PrimaryKillToFirstRecoveredMilliseconds, $report.PrimaryKillToDrainedMilliseconds))
                        {
                            Within $measurement $scenario.maximumFaultToVerifiedMilliseconds "$context recovery"
                        }
                    }
                    default { throw "Unknown xunit scenario verifier '$($scenario.verifier)'." }
                }
            }
            'console'
            {
                Require (@($stepScenarios | Where-Object { [string]$_.verifier -cne 'workflows-fault' }).Count -eq 0) "Unknown console scenario verifier."
                Require ([string]$report.Profile -ceq 'faults' -and [string]$report.PostgreSqlVersion -match '^PostgreSQL 18\.' -and
                    @($report.Cases).Count -eq 0 -and @($report.Faults).Count -eq $stepScenarios.Count) "$label fault harness ran a different scenario set."
                foreach ($scenario in $stepScenarios)
                {
                    $context = "$label scenario '$($scenario.name)'"
                    $fault = @($report.Faults | Where-Object { [string]$_.Name -ceq [string]$scenario.name })
                    Require ($fault.Count -eq 1) "$context is missing or duplicated."
                    $fault = $fault[0]
                    Require ($fault.Passed -eq $true -and [int]$fault.DurableEffects -eq 1 -and
                        [int]$fault.Attempts -eq [int]$scenario.attempts) "$context lost, duplicated or unfenced its durable effect."
                    Require (([string]$scenario.name -cne 'ambiguous-commit-lost-server-ack') -or
                        $fault.CommitAcknowledgementDropped -eq $true) "$context did not actually drop the commit acknowledgement."
                    Within $fault.RecoveryMilliseconds $scenario.maximumFaultToVerifiedMilliseconds "$context recovery"
                }
            }
            default { throw "Unknown failover step kind '$($stepPolicy.kind)'." }
        }
    }
}
$perRunSystems = @($systems | ForEach-Object { ($_ -split '\|')[1] } | Sort-Object -Unique)
Require ($systems.Count -eq 0 -or $perRunSystems.Count -eq @($systems | ForEach-Object { ($_ -split '\|')[0] } | Sort-Object -Unique).Count) (
    "$Family failover repetitions did not each use a distinct fresh PostgreSQL system.")
Write-Output "Verified $repetitions fresh exact-candidate $Family failover repetitions for $ExpectedCommit; production qualification remains false."
