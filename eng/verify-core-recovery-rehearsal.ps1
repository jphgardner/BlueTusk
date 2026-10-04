[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('BackupRestore', 'Rollback')]
    [string] $Rehearsal,

    [Parameter(Mandatory)]
    [string] $EvidenceRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedCommit
)

# Re-checks the evidence written by run-core-recovery-rehearsal.ps1: exact candidate commit, pinned
# image, exact package versions, hash-bound phase reports, every probe check, and approval details
# that are both derived from those reports and valid against the approval-evidence contract.
# Passing proves the retained rehearsal is internally consistent. It is NOT an approval record.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
function Read-Json([string] $Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Get-Sha256([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw "Recovery rehearsal evidence rejected: $Message" } }
function Test-Check([object] $Report, [string] $Name)
{
    $property = $Report.checks.PSObject.Properties[$Name]
    return $null -ne $property -and $property.Value -eq $true
}

$reportPath = Join-Path $root 'rehearsal-report.json'
Require (Test-Path -LiteralPath $reportPath -PathType Leaf) 'rehearsal-report.json is missing.'
$report = Read-Json $reportPath
Require ([int]$report.schemaVersion -eq 1 -and [string]$report.rehearsal -ceq $Rehearsal -and
    [string]$report.scope -ceq 'Core' -and [string]$report.releaseVersion -ceq '1.1.0') 'schema, rehearsal, scope or release differ.'
Require ([string]$report.candidateCommit -ceq $ExpectedCommit) "the rehearsal ran at $($report.candidateCommit), not $ExpectedCommit."
Require ($report.passed -eq $true -and $null -eq $report.PSObject.Properties['error']) 'the rehearsal did not pass.'
Require ([string]$report.operator -match '\S{2,}') 'the operator is not named.'

$readiness = Read-Json (Join-Path $PSScriptRoot 'v1.1-candidate-readiness.json')
Require ([string]$report.postgreSqlImage -ceq [string]$readiness.endurancePostgreSqlImage -and
    [string]$report.postgreSqlImage -match '@sha256:[0-9a-f]{64}$') 'PostgreSQL was not the digest-pinned Core image.'
Require ([string]$report.candidatePackageManifestSha256 -cmatch '^[0-9a-f]{64}$') 'the candidate package manifest is not hash bound.'
foreach ($file in @('CoreRecoveryProbe.csproj', 'Program.cs'))
{
    Require ([string]$report.probeSourceSha256.$file -ceq (Get-Sha256 (Join-Path $PSScriptRoot "CoreRecoveryProbe/$file"))) "probe source '$file' differs from this commit."
}

$probePackages = @(
    'BlueTusk.Data', 'BlueTusk.Streams', 'BlueTusk.Streams.Storage.PostgreSql', 'BlueTusk.Streams.Testing',
    'BlueTusk.Live', 'BlueTusk.Live.DependencyInjection', 'BlueTusk.ControlPlane', 'BlueTusk.Sync',
    'BlueTusk.Sync.PostgreSql')
function Assert-Packages([object[]] $Packages, [string] $Version, [string] $Name)
{
    foreach ($id in $probePackages)
    {
        Require (@($Packages | Where-Object { $_.id -ceq $id -and $_.version -ceq $Version -and [string]$_.sha512 -match '^[A-Za-z0-9+/=]{40,}$' }).Count -eq 1) "$Name package $id $Version is not recorded."
    }
    Require (@($Packages | Where-Object { $_.version -cne $Version }).Count -eq 0) "$Name packages mix BlueTusk versions."
}
Assert-Packages @($report.candidatePackages) '1.1.0' 'candidate'

$expectedPhases = if ($Rehearsal -eq 'BackupRestore')
{
    @(@('candidate', '1.1.0', 'seed'), @('candidate', '1.1.0', 'advance'), @('candidate', '1.1.0', 'verify'))
}
else
{
    Assert-Packages @($report.rollbackPackages) '1.0.0' 'rollback'
    @(@('previous-release', '1.0.0', 'seed'), @('candidate', '1.1.0', 'advance'), @('previous-release', '1.0.0', 'verify'))
}
$phases = @($report.phases)
Require ($phases.Count -eq $expectedPhases.Count) "expected $($expectedPhases.Count) probe phases, found $($phases.Count)."
$phaseReports = @()
for ($index = 0; $index -lt $phases.Count; $index++)
{
    $phase = $phases[$index]
    $expected = $expectedPhases[$index]
    Require ([string]$phase.binary -ceq $expected[0] -and [string]$phase.version -ceq $expected[1] -and
        [string]$phase.phase -ceq $expected[2] -and $phase.passed -eq $true) "phase $($index + 1) is not a passing $($expected[0]) $($expected[2]) on $($expected[1])."
    Require ([string]$phase.path -cmatch '^phases/[0-9]{2}-[a-z-]+-(seed|advance|verify)\.json$') "phase $($index + 1) path is not canonical."
    $path = Join-Path $root ([string]$phase.path)
    Require ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Sha256 $path) -ceq [string]$phase.sha256) "phase report '$($phase.path)' is missing or altered."
    $phaseReport = Read-Json $path
    Require ($phaseReport.passed -eq $true -and [string]$phaseReport.phase -ceq $expected[2] -and
        [string]$phaseReport.observations.packageVersion -ceq $expected[1] -and @($phaseReport.failures).Count -eq 0) "phase report '$($phase.path)' did not pass on $($expected[1])."
    foreach ($check in $phaseReport.checks.PSObject.Properties)
    {
        Require ($check.Value -eq $true) "phase report '$($phase.path)' check '$($check.Name)' failed."
    }
    $phaseReports += $phaseReport
}

$gateId = if ($Rehearsal -eq 'BackupRestore') { 'backup-restore-rehearsal' } else { 'rollback-rehearsal' }
$contract = Read-Json (Join-Path $PSScriptRoot 'v1-approval-evidence-contract.json')
$fieldNames = @(@($contract.gates | Where-Object id -ceq $gateId)[0].details | ForEach-Object { [string]$_.name })
$details = $report.details
Require ($null -ne $details -and @(Compare-Object $fieldNames @($details.PSObject.Properties.Name)).Count -eq 0) "details do not have exactly the '$gateId' fields."
$detailsPath = Join-Path $root 'approval-details.json'
Require ((Test-Path -LiteralPath $detailsPath -PathType Leaf) -and
    ((Read-Json $detailsPath | ConvertTo-Json -Depth 4 -Compress) -ceq ($details | ConvertTo-Json -Depth 4 -Compress))) 'approval-details.json differs from the report.'

if ($Rehearsal -eq 'BackupRestore')
{
    $workload = $phaseReports[1]
    $reconciliation = $phaseReports[2]
    $backupPath = Join-Path $root ([string]$report.backup.path)
    Require ([string]$report.backup.path -ceq 'backup/rehearsal.dump.enc' -and (Test-Path -LiteralPath $backupPath -PathType Leaf) -and
        (Get-Sha256 $backupPath) -ceq [string]$report.backup.sha256) 'the encrypted backup is missing or altered.'
    $bytes = [IO.File]::ReadAllBytes($backupPath)
    Require ($bytes.Length -gt 33 -and [Text.Encoding]::ASCII.GetString($bytes, 0, 5) -ceq 'BTRB1') 'the retained backup is not the encrypted archive format.'
    Require ([string]$details.backupId -ceq "core-rehearsal-$(([string]$report.backup.sha256).Substring(0, 16))") 'the backup id does not identify the retained backup.'
    Require ([string]$details.operator -ceq [string]$report.operator -and $details.backupEncrypted -eq $true -and
        $details.restoreTargetEmpty -eq $true) 'operator, encryption or empty-target facts differ.'
    $sourceRows = 0L; $restoredRows = 0L; $mismatches = 0
    $tables = @(@($report.inventory.source.PSObject.Properties.Name) + @($report.inventory.restored.PSObject.Properties.Name) | Sort-Object -Unique)
    foreach ($table in $tables)
    {
        $left = $report.inventory.source.PSObject.Properties[$table]
        $right = $report.inventory.restored.PSObject.Properties[$table]
        if ($null -ne $left) { $sourceRows += [long]$left.Value.rows }
        if ($null -ne $right) { $restoredRows += [long]$right.Value.rows }
        if ($null -eq $left -or $null -eq $right -or [long]$left.Value.rows -ne [long]$right.Value.rows -or
            [string]$left.Value.md5 -cne [string]$right.Value.md5) { $mismatches++ }
    }
    Require ($tables.Count -gt 0 -and [long]$details.sourceRowCount -eq $sourceRows -and [long]$details.restoredRowCount -eq $restoredRows -and
        [long]$details.integrityMismatches -eq $mismatches -and $mismatches -eq 0) 'row counts or table digests do not match the retained inventory.'
    Require ([long]$details.sourceObjectCount -ge 1 -and [long]$details.sourceObjectCount -eq [long]$details.restoredObjectCount) 'object counts differ.'
    Require ([string]$details.sourceCheckpointPosition -ceq [string]$workload.observations.checkpointAfter -and
        [string]$details.restoredCheckpointPosition -ceq [string]$reconciliation.observations.checkpointBefore -and
        [string]$details.sourceCheckpointPosition -ceq [string]$details.restoredCheckpointPosition) 'checkpoints are not the ones read by the probe.'
    $slos = Read-Json (Join-Path $PSScriptRoot 'v1-production-slos.json')
    $objective = @($slos.recoveryObjectives | Where-Object id -ceq 'streams-relay')[0]
    Require ([string]$objective.rto -match '^(\d+)m$') 'the streams-relay RTO is not declared in minutes.'
    Require ([double]$details.rtoSeconds -eq [int]$Matches[1] * 60 -and [double]$details.rpoSeconds -eq 0) 'declared RPO/RTO differ from eng/v1-production-slos.json.'
    Require ([double]$details.observedRestoreSeconds -gt 0 -and [double]$details.observedRestoreSeconds -le [double]$details.rtoSeconds -and
        [double]$details.observedRecoveryPointGapSeconds -le [double]$details.rpoSeconds) 'the restore missed its declared RPO or RTO.'
    Require ($details.reconciliationPassed -eq $true -and $reconciliation.passed -eq $true -and
        [long]$reconciliation.observations.missingAcknowledgedOrders -eq 0) 'post-restore reconciliation failed.'
}
else
{
    $seed = $phaseReports[0]
    $upgrade = $phaseReports[1]
    $after = $phaseReports[2]
    Require ([string]$details.candidateVersion -ceq '1.1.0' -and [string]$details.candidateVersion -ceq [string]$upgrade.observations.packageVersion -and
        [string]$details.rollbackVersion -ceq '1.0.0' -and [string]$details.rollbackVersion -ceq [string]$after.observations.packageVersion -and
        [string]$seed.observations.packageVersion -ceq '1.0.0') 'candidate or rollback versions differ from the phase reports.'
    Require ([string]$details.trigger -match '\S{5,}' -and [string]$details.decisionAuthority -match '\S{2,}' -and
        [double]$details.durationSeconds -gt 0) 'trigger, decision authority or duration is missing.'
    $durable = (Test-Check $after 'schemaInitialized') -and (Test-Check $after 'ordersReconciled') -and
        (Test-Check $after 'streamsCheckpointReconciled') -and (Test-Check $after 'liveReplayIntegrity') -and
        (Test-Check $after 'controlPlaneReconciled') -and (Test-Check $after 'syncReconciled')
    $live = (Test-Check $after 'liveStarted') -and (Test-Check $after 'liveClientResumedOrReset') -and (Test-Check $after 'liveFreshClientConnected')
    $loss = [long]$after.observations.missingAcknowledgedOrders + [long]$after.observations.orderIntegrityMismatches
    Require ($details.versionCompatibilityPassed -eq $true -and $details.connectionDrainPassed -eq $true -and
        $details.durableFormatCompatibilityPassed -eq $durable -and $durable -and
        $details.relayCheckpointOwnershipPassed -eq (Test-Check $after 'streamsOwnershipFenced') -and $details.relayCheckpointOwnershipPassed -eq $true -and
        $details.liveClientResetPassed -eq $live -and $live -and
        $details.controlPlaneFencingPassed -eq (Test-Check $after 'controlPlaneFencingEnforced') -and $details.controlPlaneFencingPassed -eq $true -and
        $details.reconciliationPassed -eq $true -and [long]$details.dataLossEvents -eq $loss -and $loss -eq 0) 'rollback outcomes are not the ones observed by the rolled-back binary.'
}

# Schema check only: wrap the measured details in a throw-away envelope and run the gate verifier.
$schemaRoot = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-rehearsal-schema-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($schemaRoot) | Out-Null
try
{
    $envelope = [ordered]@{
        schemaVersion = 4; gateId = $gateId; candidateCommit = $ExpectedCommit; outcome = 'approved'
        approvedBy = [string]$report.operator; approvedUtc = ([DateTimeOffset]$report.completedUtc).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")
        summary = 'Schema conformance check of measured recovery-rehearsal details; not an approval record.'
        blockingFindings = 0; references = @('https://evidence.invalid/schema-check-only'); details = $details
    }
    $schemaPath = Join-Path $schemaRoot "$gateId.json"
    $envelope | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $schemaPath -Encoding utf8NoBOM
    & (Join-Path $PSScriptRoot 'verify-v1-approval-evidence.ps1') -EvidencePath $schemaPath -ExpectedGateId $gateId `
        -ExpectedCommit $ExpectedCommit -ReleaseTrack Core | Out-Null
}
finally
{
    [IO.Directory]::Delete($schemaRoot, $true)
}

Write-Output (
    "Verified the $Rehearsal recovery rehearsal for Core 1.1.0 candidate ${ExpectedCommit}: " +
    "$($phases.Count) hash-bound probe phases and '$gateId' details that satisfy the approval contract. " +
    'This is rehearsal evidence, NOT an approval; the accountable approver still signs the approval record.')
