[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ReportPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedCommit,
    [string] $PolicyPath = (Join-Path $PSScriptRoot 'jobs-release-failover-policy.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$evidence = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json -Depth 100

function Require([bool] $Condition, [string] $Message)
{
    if (-not $Condition) { throw $Message }
}

Require ($policy.schemaVersion -eq 1 -and $policy.family -ceq 'Jobs' -and
    $policy.qualification -ceq 'manual-exact-candidate-synchronous-physical-promotion' -and
    $policy.repetitions -ge 3) 'Unsupported Jobs failover policy.'
Require ($evidence.ProductionQualified -eq $false -and
    $evidence.Repetitions -eq $policy.repetitions -and
    @($evidence.Runs).Count -eq $policy.repetitions) 'Jobs failover report has incomplete repetitions or overclaims qualification.'
$before = $evidence.SourceProvenance.Before
$after = $evidence.SourceProvenance.After
Require ($evidence.SourceProvenance.Unchanged -eq $true -and
    $before.dirty -eq $false -and $after.dirty -eq $false -and
    [string]$before.commit -ieq $ExpectedCommit -and
    [string]$after.commit -ieq $ExpectedCommit -and
    [string]$before.sourceTreeSha256 -match '^[0-9a-fA-F]{64}$' -and
    [string]$before.sourceTreeSha256 -ieq [string]$after.sourceTreeSha256 -and
    [int]$before.fileCount -gt 0 -and
    [int]$before.fileCount -eq [int]$after.fileCount) 'Jobs failover source is dirty, changed or not the exact candidate.'
Require ([string]$evidence.ScriptSha256 -match '^[0-9a-fA-F]{64}$') 'Jobs failover runner hash is missing.'

$fixtures = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$systemIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
for ($index = 1; $index -le $policy.repetitions; $index++)
{
    $runs = @($evidence.Runs | Where-Object { [int]$_.Repetition -eq $index })
    Require ($runs.Count -eq 1) "Jobs failover repetition $index is missing or duplicated."
    $run = $runs[0]
    $report = $run.Report
    Require ($fixtures.Add([string]$report.Fixture) -and
        [string]$report.Fixture -match '^jobs-physical-[0-9a-f]{32}-run[1-3]$' -and
        ([string]$report.Fixture).EndsWith("-run$index", [StringComparison]::Ordinal)) "Jobs failover repetition $index reused or misnamed its fixture."
    Require ([string]$report.Image -ceq [string]$policy.postgreSqlImage -and
        [string]$run.Metadata.ImageDigest -ceq [string]$policy.postgreSqlImage -and
        @($run.Metadata.ImageInspect.RepoDigests | Where-Object {
            [string]$_ -ceq [string]$policy.postgreSqlImage
        }).Count -gt 0 -and
        [string]$report.Server -match '^PostgreSQL 18\.') "Jobs failover repetition $index used an unverified PostgreSQL image."
    $settings = @($run.Metadata.BeforePrimarySettings | ForEach-Object { [string]$_ })
    Require ('fsync=on' -in $settings -and
        'full_page_writes=on' -in $settings -and
        'synchronous_commit=remote_apply' -in $settings -and
        'synchronous_standby_names=FIRST 1 (bluetusk_jobs_recovery_standby)' -in $settings) "Jobs failover repetition $index lacks synchronous durability settings."
    Require ([string]$report.BeforeSystemIdentifier -match '^[0-9]+$' -and
        [string]$report.BeforeSystemIdentifier -ceq [string]$report.AfterSystemIdentifier -and
        $systemIds.Add([string]$report.BeforeSystemIdentifier) -and
        [int]$report.BeforeTimeline -ge 1 -and
        [int]$report.AfterTimeline -eq ([int]$report.BeforeTimeline + 1)) "Jobs failover repetition $index did not promote a distinct physical standby."
    Require ([int]$report.AcknowledgedJobs -eq $policy.acknowledgedJobsPerRun -and
        [int]$report.VerifiedJobs -eq $policy.acknowledgedJobsPerRun -and
        [int]$report.AcknowledgedBusinessAdmissions -eq $policy.acknowledgedJobsPerRun -and
        [int]$report.JobEffects -eq $policy.acknowledgedJobsPerRun -and
        [int]$report.WorkflowEffects -eq $policy.workflowEffectsPerRun) "Jobs failover repetition $index lost or duplicated acknowledged effects."
    Require ($report.UnchangedMultihostSource -eq $true -and
        $report.NoCrossTenantReads -eq $true -and
        $report.UnavailableHealthDuringOutage -eq $true -and
        $report.HealthyAfterRecovery -eq $true) "Jobs failover repetition $index failed routing, isolation or health checks."
    foreach ($measurement in @(
        $report.PrimaryKillToPromotionMilliseconds,
        $report.PrimaryKillToFirstEffectReplayMilliseconds,
        $report.PrimaryKillToFirstRecoveredMilliseconds,
        $report.PrimaryKillToDrainedMilliseconds))
    {
        Require ([double]$measurement -gt 0 -and
            [double]$measurement -le $policy.maximumRecoveryMilliseconds) "Jobs failover repetition $index exceeded its bounded recovery window."
    }
    $jobLeases = @($report.Leases | Where-Object { [string]$_.Product -ceq 'Jobs' })
    $workflowLeases = @($report.Leases | Where-Object { [string]$_.Product -ceq 'Workflows' })
    Require (@($report.Leases).Count -eq
        ($policy.jobLeasesPerRun + $policy.workflowLeasesPerRun) -and
        $jobLeases.Count -eq $policy.jobLeasesPerRun -and
        $workflowLeases.Count -eq $policy.workflowLeasesPerRun -and
        @($jobLeases | ForEach-Object { [string]$_.Tenant } | Sort-Object -Unique).Count -eq 2) "Jobs failover repetition $index lacks both tenants' fenced leases."
    foreach ($lease in @($report.Leases))
    {
        Require ([int]$lease.Attempt -eq 2 -and
            [long]$lease.AfterFence -gt [long]$lease.BeforeFence -and
            $lease.StaleCompletionRejected -eq $true -and
            $lease.StaleEffectRejected -eq $true) "Jobs failover repetition $index accepted a stale owner or failed recovery."
    }
    Require (@($report.Workflows).Count -eq 6 -and
        @($report.Workflows | Where-Object { $_.ReplayMatches -ne $true }).Count -eq 0 -and
        @($report.Workflows | Where-Object { [string]$_.Status -ceq 'Succeeded' }).Count -eq 4 -and
        @($report.Workflows | Where-Object { [string]$_.Status -ceq 'Canceled' }).Count -eq 2 -and
        @($report.Workflows | ForEach-Object { [string]$_.Tenant } | Sort-Object -Unique).Count -eq 2) "Jobs failover repetition $index lost dependent workflow state."
    foreach ($files in @($run.BinariesBefore.Files, $run.BinariesAfter.Files))
    {
        Require (@($files).Count -gt 0) "Jobs failover repetition $index lacks a binary manifest."
    }
    Require ($run.BinariesUnchanged -eq $true -and
        @($run.BinariesBefore.Files).Count -eq @($run.BinariesAfter.Files).Count) "Jobs failover repetition $index changed its executable set."
    foreach ($file in @($run.BinariesBefore.Files))
    {
        $afterFiles = @($run.BinariesAfter.Files | Where-Object { [string]$_.Name -ceq [string]$file.Name })
        Require ($afterFiles.Count -eq 1 -and
            [string]$file.Sha256 -match '^[0-9a-fA-F]{64}$' -and
            [string]$afterFiles[0].Sha256 -ieq [string]$file.Sha256 -and
            [long]$afterFiles[0].Bytes -eq [long]$file.Bytes) "Jobs failover repetition $index changed a test binary."
    }
}

Write-Output "Verified three distinct exact-candidate Jobs promotions and fenced acknowledged effects for $ExpectedCommit."
