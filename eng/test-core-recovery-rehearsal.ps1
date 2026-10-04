[CmdletBinding()]
param()

# Self-test for the Core recovery-rehearsal tooling. It needs no Docker or build: synthetic evidence
# (clearly labelled, never release evidence) proves the verifier accepts a consistent backup/restore
# and rollback record and rejects altered, failed or inconsistent ones, and the runner's preflight
# refuses unsafe inputs before touching Docker. It does not run a rehearsal.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-core-recovery-rehearsal.ps1'
$runner = Join-Path $PSScriptRoot 'run-core-recovery-rehearsal.ps1'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$commit = 'a' * 40
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-recovery-rehearsal-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$image = [string](Get-Content (Join-Path $PSScriptRoot 'v1.1-candidate-readiness.json') -Raw | ConvertFrom-Json).endurancePostgreSqlImage
$probePackages = @(
    'BlueTusk.Data', 'BlueTusk.Streams', 'BlueTusk.Streams.Storage.PostgreSql', 'BlueTusk.Streams.Testing',
    'BlueTusk.Live', 'BlueTusk.Live.DependencyInjection', 'BlueTusk.ControlPlane', 'BlueTusk.Sync',
    'BlueTusk.Sync.PostgreSql')

function Write-Json([string] $Path, [object] $Value)
{
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent)) | Out-Null
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Get-Sha256([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function New-PhaseReport([string] $Phase, [string] $Version, [string] $CheckpointBefore, [string] $CheckpointAfter)
{
    $checks = [ordered]@{ schemaInitialized = $true; ordersWritable = $true; syncApplied = $true; liveStarted = $true; liveFreshClientConnected = $true }
    if ($Phase -eq 'seed')
    {
        $checks.streamsCheckpointWritable = $true; $checks.controlPlaneWritable = $true
    }
    else
    {
        foreach ($name in @('ordersReconciled', 'streamsCheckpointReconciled', 'controlPlaneReconciled', 'syncReconciled',
                'syncIdempotent', 'liveReplayIntegrity', 'liveClientResumedOrReset', 'streamsOwnershipFenced', 'controlPlaneFencingEnforced'))
        { $checks[$name] = $true }
    }
    $observations = [ordered]@{ packageVersion = $Version; checkpointAfter = $CheckpointAfter; ordersAcknowledgedAfter = 200 }
    if ($Phase -ne 'seed')
    {
        $observations.checkpointBefore = $CheckpointBefore
        $observations.missingAcknowledgedOrders = 0
        $observations.orderIntegrityMismatches = 0
    }
    return [ordered]@{ schemaVersion = 1; phase = $Phase; passed = $true; checks = $checks; observations = $observations; failures = @() }
}

function New-Fixture([string] $Name, [string] $Rehearsal)
{
    $root = Join-Path $scratch $Name
    $packages = { param($version) @($probePackages | ForEach-Object { [ordered]@{ id = $_; version = $version; sha512 = ('A' * 86) + '==' } }) }
    $report = [ordered]@{
        schemaVersion = 1; rehearsal = $Rehearsal; scope = 'Core'; releaseVersion = '1.1.0'; candidateCommit = $commit
        operator = 'Synthetic Operator'; startedUtc = '2026-01-01T00:00:00.0000000+00:00'; postgreSqlImage = $image
        candidatePackageManifestSha256 = 'b' * 64
        probeSourceSha256 = [ordered]@{
            'CoreRecoveryProbe.csproj' = Get-Sha256 (Join-Path $PSScriptRoot 'CoreRecoveryProbe/CoreRecoveryProbe.csproj')
            'Program.cs' = Get-Sha256 (Join-Path $PSScriptRoot 'CoreRecoveryProbe/Program.cs')
        }
        candidatePackages = & $packages '1.1.0'
        completedUtc = '2026-01-01T00:10:00.0000000+00:00'; passed = $true
    }
    $checkpoint = 'streams=2000/1;sync=2500;deployment=2/4;orders=200'
    if ($Rehearsal -eq 'BackupRestore')
    {
        $plan = @(@('candidate', '1.1.0', 'seed', '', 'streams=1000/0;sync=1500;deployment=1/2;orders=100'),
            @('candidate', '1.1.0', 'advance', 'streams=1000/0;sync=1500;deployment=1/2;orders=100', $checkpoint),
            @('candidate', '1.1.0', 'verify', $checkpoint, 'streams=3000/2;sync=3500;deployment=3/6;orders=300'))
    }
    else
    {
        $report.rollbackPackages = & $packages '1.0.0'
        $plan = @(@('previous-release', '1.0.0', 'seed', '', 'streams=1000/0;sync=1500;deployment=1/2;orders=100'),
            @('candidate', '1.1.0', 'advance', 'streams=1000/0;sync=1500;deployment=1/2;orders=100', $checkpoint),
            @('previous-release', '1.0.0', 'verify', $checkpoint, 'streams=3000/2;sync=3500;deployment=3/6;orders=300'))
    }
    $phases = @()
    for ($index = 0; $index -lt $plan.Count; $index++)
    {
        $entry = $plan[$index]
        $relative = 'phases/{0:D2}-{1}-{2}.json' -f ($index + 1), $entry[0], $entry[2]
        $path = Join-Path $root $relative
        Write-Json $path (New-PhaseReport $entry[2] $entry[1] $entry[3] $entry[4])
        $phases += [ordered]@{ binary = $entry[0]; version = $entry[1]; phase = $entry[2]; applicationName = "bluetusk-rehearsal-$($entry[0])"
            path = $relative; sha256 = Get-Sha256 $path; passed = $true }
    }
    $report.phases = $phases
    if ($Rehearsal -eq 'BackupRestore')
    {
        $backup = Join-Path $root 'backup/rehearsal.dump.enc'
        [IO.Directory]::CreateDirectory((Split-Path $backup -Parent)) | Out-Null
        [IO.File]::WriteAllBytes($backup, [byte[]]([Text.Encoding]::ASCII.GetBytes('BTRB1') + [byte[]]::new(64)))
        $backupSha = Get-Sha256 $backup
        $report.backup = [ordered]@{ path = 'backup/rehearsal.dump.enc'; sha256 = $backupSha; bytes = 69; encryption = 'synthetic' }
        $tables = [ordered]@{ 'rehearsal.orders' = [ordered]@{ rows = 200; md5 = 'c' * 32 }; 'bluetusk_sync.documents' = [ordered]@{ rows = 20; md5 = 'd' * 32 } }
        $report.inventory = [ordered]@{ source = $tables; restored = $tables }
        $report.details = [ordered]@{
            backupId = "core-rehearsal-$($backupSha.Substring(0, 16))"; operator = 'Synthetic Operator'; backupEncrypted = $true
            restoreTargetEmpty = $true; sourceObjectCount = 40; restoredObjectCount = 40; sourceRowCount = 220; restoredRowCount = 220
            sourceCheckpointPosition = $checkpoint; restoredCheckpointPosition = $checkpoint; rpoSeconds = 0
            observedRecoveryPointGapSeconds = 0; rtoSeconds = 1800; observedRestoreSeconds = 12.5; integrityMismatches = 0
            reconciliationPassed = $true
        }
    }
    else
    {
        $report.details = [ordered]@{
            candidateVersion = '1.1.0'; rollbackVersion = '1.0.0'; trigger = 'Synthetic stop condition'; decisionAuthority = 'Synthetic Operator'
            durationSeconds = 9.75; versionCompatibilityPassed = $true; connectionDrainPassed = $true; durableFormatCompatibilityPassed = $true
            relayCheckpointOwnershipPassed = $true; liveClientResetPassed = $true; controlPlaneFencingPassed = $true
            reconciliationPassed = $true; dataLossEvents = 0
        }
    }
    Write-Json (Join-Path $root 'rehearsal-report.json') $report
    Write-Json (Join-Path $root 'approval-details.json') $report.details
    return $root
}

function Edit-Fixture([string] $Root, [scriptblock] $Change)
{
    $path = Join-Path $Root 'rehearsal-report.json'
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    & $Change $report $Root
    Write-Json $path $report
    Write-Json (Join-Path $Root 'approval-details.json') $report.details
}

function Edit-Phase([object] $Report, [string] $Root, [int] $Index, [scriptblock] $Change)
{
    $path = Join-Path $Root $Report.phases[$Index].path
    $phase = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    & $Change $phase
    Write-Json $path $phase
    $Report.phases[$Index].sha256 = Get-Sha256 $path
}

try
{
    $accepted = 0
    foreach ($rehearsal in @('BackupRestore', 'Rollback'))
    {
        $positive = New-Fixture "positive-$rehearsal" $rehearsal
        & $verifier -Rehearsal $rehearsal -EvidenceRoot $positive -ExpectedCommit $commit | Out-Null
        $accepted++
    }

    $cases = @(
        @{ Name = 'wrong-commit'; Rehearsal = 'BackupRestore'; Change = { param($r) $r.candidateCommit = 'b' * 40 } },
        @{ Name = 'failed-run'; Rehearsal = 'BackupRestore'; Change = { param($r) $r.passed = $false } },
        @{ Name = 'unpinned-image'; Rehearsal = 'BackupRestore'; Change = { param($r) $r.postgreSqlImage = 'postgres:18-alpine' } },
        @{ Name = 'mixed-packages'; Rehearsal = 'BackupRestore'; Change = { param($r) $r.candidatePackages[0].version = '1.1.0-rc.2' } },
        @{ Name = 'altered-phase'; Rehearsal = 'BackupRestore'; Change = { param($r, $root) Add-Content -LiteralPath (Join-Path $root $r.phases[2].path) -Value ' ' } },
        @{ Name = 'failed-probe-check'; Rehearsal = 'BackupRestore'; Change = { param($r, $root) Edit-Phase $r $root 2 { param($p) $p.checks.ordersReconciled = $false } } },
        @{ Name = 'restore-over-source'; Rehearsal = 'BackupRestore'; Change = { param($r) $r.details.restoreTargetEmpty = $false } },
        @{ Name = 'unencrypted-backup'; Rehearsal = 'BackupRestore'; Change = { param($r, $root) [IO.File]::WriteAllBytes((Join-Path $root 'backup/rehearsal.dump.enc'), [byte[]]::new(80)); $r.backup.sha256 = (Get-Sha256 (Join-Path $root 'backup/rehearsal.dump.enc')) } },
        @{ Name = 'row-count-claim'; Rehearsal = 'BackupRestore'; Change = { param($r) $r.details.restoredRowCount = 219 } },
        @{ Name = 'table-digest-mismatch'; Rehearsal = 'BackupRestore'; Change = { param($r) $r.inventory.restored.'rehearsal.orders'.md5 = 'e' * 32 } },
        @{ Name = 'invented-checkpoint'; Rehearsal = 'BackupRestore'; Change = { param($r) $r.details.restoredCheckpointPosition = 'streams=9999/9' ; $r.details.sourceCheckpointPosition = 'streams=9999/9' } },
        @{ Name = 'rto-missed'; Rehearsal = 'BackupRestore'; Change = { param($r) $r.details.observedRestoreSeconds = 1801 } },
        @{ Name = 'rpo-missed'; Rehearsal = 'BackupRestore'; Change = { param($r) $r.details.observedRecoveryPointGapSeconds = 1 } },
        @{ Name = 'details-file-differs'; Rehearsal = 'BackupRestore'; Change = { param($r, $root) $r.details.operator = 'Someone Else' } },
        @{ Name = 'rollback-to-candidate'; Rehearsal = 'Rollback'; Change = { param($r) $r.details.rollbackVersion = '1.1.0' } },
        @{ Name = 'rollback-phase-order'; Rehearsal = 'Rollback'; Change = { param($r) $r.phases[0].binary = 'candidate' } },
        @{ Name = 'live-reset-claim'; Rehearsal = 'Rollback'; Change = { param($r, $root) Edit-Phase $r $root 2 { param($p) $p.checks.liveClientResumedOrReset = $false } } },
        @{ Name = 'hidden-data-loss'; Rehearsal = 'Rollback'; Change = { param($r, $root) Edit-Phase $r $root 2 { param($p) $p.observations.missingAcknowledgedOrders = 1 } } },
        @{ Name = 'drain-failed'; Rehearsal = 'Rollback'; Change = { param($r) $r.details.connectionDrainPassed = $false } },
        @{ Name = 'rollback-packages-missing'; Rehearsal = 'Rollback'; Change = { param($r) $r.rollbackPackages = @() } }
    )
    $rejected = 0
    foreach ($case in $cases)
    {
        $root = New-Fixture $case.Name $case.Rehearsal
        Edit-Fixture $root $case.Change
        if ($case.Name -eq 'details-file-differs')
        {
            $detailsPath = Join-Path $root 'approval-details.json'
            $details = Get-Content $detailsPath -Raw | ConvertFrom-Json
            $details.operator = 'Synthetic Operator'
            Write-Json $detailsPath $details
        }
        $failure = $null
        try { & $verifier -Rehearsal $case.Rehearsal -EvidenceRoot $root -ExpectedCommit $commit | Out-Null }
        catch { $failure = $_.Exception.Message }
        if ($null -eq $failure) { throw "Inconsistent rehearsal evidence '$($case.Name)' was accepted." }
        $rejected++
    }

    # Runner preflight: unsafe inputs are refused before Docker, a build or any container starts.
    $existing = Join-Path $repositoryRoot ('artifacts/recovery-preflight-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($existing) | Out-Null
    try
    {
        $preflight = @(
            @{ Name = 'evidence-outside-artifacts'; Error = 'must be below'; Arguments = @{ EvidenceRoot = (Join-Path $scratch 'outside'); ExpectedCommit = $commit } },
            @{ Name = 'existing-evidence'; Error = 'already exists'; Arguments = @{ EvidenceRoot = $existing; ExpectedCommit = $commit } },
            @{ Name = 'wrong-checkout'; Error = 'is not the rehearsal candidate'; Arguments = @{ EvidenceRoot = "$existing-new"; ExpectedCommit = $commit } }
        )
        foreach ($case in $preflight)
        {
            $arguments = $case.Arguments
            $failure = $null
            try
            {
                & $runner -Rehearsal BackupRestore -CandidatePackageRoot 'artifacts/no-such-candidate' -Operator 'Synthetic Operator' @arguments | Out-Null
            }
            catch { $failure = $_.Exception.Message }
            if ($null -eq $failure -or $failure -notmatch [regex]::Escape($case.Error))
            {
                throw "Runner preflight '$($case.Name)' did not fail at '$($case.Error)': $failure"
            }
            if (Test-Path -LiteralPath "$existing-new") { throw 'The runner created evidence before its preflight passed.' }
            $rejected++
        }
    }
    finally
    {
        [IO.Directory]::Delete($existing, $true)
    }

    $probeProject = Get-Content (Join-Path $PSScriptRoot 'CoreRecoveryProbe/CoreRecoveryProbe.csproj') -Raw
    foreach ($package in $probePackages)
    {
        if (-not $probeProject.Contains("<PackageReference Include=`"$package`" Version=`"[`$(BlueTuskPackageVersion)]`" />", [StringComparison]::Ordinal))
        {
            throw "The recovery probe does not pin '$package' to the exact rehearsed version."
        }
    }

    Write-Output (
        "Core recovery-rehearsal self-test passed: $accepted synthetic records accepted and $rejected altered, failed, " +
        'inconsistent or unsafe cases rejected. Synthetic fixtures are not rehearsal evidence.')
}
finally
{
    $full = [IO.Path]::GetFullPath($scratch)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($full).StartsWith('bluetusk-recovery-rehearsal-', [StringComparison]::Ordinal))
    {
        [IO.Directory]::Delete($full, $true)
    }
}
