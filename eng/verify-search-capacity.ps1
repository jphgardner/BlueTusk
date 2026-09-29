[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [switch]$Candidate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = (Resolve-Path -LiteralPath $EvidenceDirectory).Path
$budgetPath = Join-Path $PSScriptRoot 'search-capacity-budgets.json'
$budget = Get-Content -LiteralPath $budgetPath -Raw | ConvertFrom-Json -Depth 20

function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Json([string]$Path) {
    Require (Test-Path -LiteralPath $Path -PathType Leaf) "Missing evidence: $Path"
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 40
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Number($Value, [string]$Name) {
    Require ($null -ne $Value) "$Name is missing."
    $parsed = [double]$Value
    Require ([double]::IsFinite($parsed)) "$Name is not finite."
    return $parsed
}
function Minimum($Value, $Budget, [string]$Name) {
    Require ((Number $Value $Name) -ge (Number $Budget "$Name budget")) "$Name is below its minimum $Budget."
}
function Maximum($Value, $Budget, [string]$Name) {
    Require ((Number $Value $Name) -le (Number $Budget "$Name budget")) "$Name exceeds its maximum $Budget."
}

Require ($ExpectedCommit -match '^[0-9a-fA-F]{40}$') 'A full candidate SHA is required.'
$head = (& git -C $root rev-parse HEAD).Trim()
Require ($LASTEXITCODE -eq 0 -and [string]::Equals($head, $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) 'The verifier checkout is not the requested candidate.'
Require (@(& git -C $root status --porcelain --untracked-files=normal).Count -eq 0) 'The verifier checkout is dirty.'
Require ($budget.schemaVersion -eq 1 -and $budget.minimumRuns -ge 2 -and $budget.minimumSecondsPerRun -ge 1800) 'Unsupported Search capacity budget contract.'

$manifest = Json (Join-Path $evidence 'manifest.json')
Require ($manifest.SchemaVersion -eq 1 -and $manifest.ProductionQualified -eq $false -and
    ($Candidate -or $manifest.QualifiedLocalCapacity -eq $true)) 'Evidence is not a completed local capacity campaign.'
Require ($manifest.CandidateSha -ceq $head -and $manifest.Runs -ge $budget.minimumRuns) 'Manifest candidate or run count differs.'
Require ((Hash $budgetPath) -ceq $manifest.BudgetSha256 -and (Hash (Join-Path $evidence 'budgets.json')) -ceq $manifest.BudgetSha256) 'Budget snapshot differs from the candidate.'
$before = Json (Join-Path $evidence 'source-before.json')
$after = Json (Join-Path $evidence 'source-after.json')
foreach ($source in @($before, $after)) {
    Require ($source.commit -ceq $head -and $source.dirty -eq $false) 'Source capture does not describe a clean exact candidate.'
}
Require ($before.sourceTreeSha256 -ceq $after.sourceTreeSha256 -and $before.sourceTreeSha256 -ceq $manifest.SourceTreeSha256) 'Candidate source changed during measurement.'
$binary = Join-Path $evidence 'binary-snapshot/BlueTusk.Search.LoadHarness.dll'
Require ((Hash $binary) -ceq $manifest.HarnessBinarySha256) 'Harness executable differs from the manifest.'

$listed = @($manifest.Files)
$actual = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $evidence 'manifest.json') })
Require ($listed.Count -gt 0 -and $listed.Count -eq $actual.Count) 'Artifact inventory differs from the manifest.'
$seen = @{}
foreach ($entry in $listed) {
    Require ($entry.Path -match '^[^/\\]+(?:/[^/\\]+)*$' -and -not $entry.Path.Contains('..') -and -not $seen.ContainsKey([string]$entry.Path)) 'Manifest path is unsafe or repeated.'
    $seen[[string]$entry.Path] = $true
    $path = Join-Path $evidence $entry.Path
    Require (Test-Path -LiteralPath $path -PathType Leaf) "Manifest artifact is missing: $($entry.Path)"
    Require ([string]::Equals([string]$entry.Sha256, (Hash $path), [StringComparison]::OrdinalIgnoreCase)) "Manifest artifact hash differs: $($entry.Path)"
}

$previousCompleted = [DateTimeOffset]::MinValue
$reportHashes = @{}
for ($run = 1; $run -le $manifest.Runs; $run++) {
    $runRoot = Join-Path $evidence "run-$run"
    $reportPath = Join-Path $runRoot 'search-mixed.json'
    $report = Json $reportPath
    $label = "Search run $run"
    $reportHash = Hash $reportPath
    Require (-not $reportHashes.ContainsKey($reportHash)) "$label duplicates another run's raw report."
    $reportHashes[$reportHash] = $true
    $runSource = Json (Join-Path $runRoot 'source-after.json')
    Require ($runSource.commit -ceq $head -and $runSource.dirty -eq $false -and
        $runSource.sourceTreeSha256 -ceq $before.sourceTreeSha256) "$label changed candidate source."
    $started = [DateTimeOffset]::Parse([string]$report.StartedUtc)
    $completed = [DateTimeOffset]::Parse([string]$report.CompletedUtc)
    Require ($started -gt $previousCompleted -and $completed -gt $started) "$label has overlapping or invalid run timestamps."
    $previousCompleted = $completed
    Require ($report.FormatVersion -eq 1 -and $report.Passed -eq $true -and $report.ProductionQualified -eq $false) "$label lacks a completed raw report."
    Require ($report.CandidateSha -ceq $head -and $report.SourceTreeSha256 -ceq $before.sourceTreeSha256 -and
        $report.HarnessBinarySha256 -ceq $manifest.HarnessBinarySha256) "$label is not bound to the exact candidate source and harness binary."
    Require ($report.Workload -ceq $budget.workload -and $report.PostgreSqlImage -ceq $budget.postgreSqlImage -and [string]$report.PostgreSqlVersion -match '^PostgreSQL 18\.') "$label has the wrong workload or PostgreSQL fixture."
    foreach ($pair in @(@('Tenants','tenants'), @('DocumentsPerTenant','documentsPerTenant'), @('ContentBytes','contentBytes'), @('Writers','writers'), @('Readers','readers'))) {
        Require ($report.($pair[0]) -eq $budget.($pair[1])) "$label has the wrong $($pair[0]) workload size."
    }
    Require ($report.WriteOfferIntervalMilliseconds -eq $budget.writeOfferIntervalMilliseconds -and
        $report.ReadOfferIntervalMilliseconds -eq $budget.readOfferIntervalMilliseconds) "$label has the wrong fixed offered-rate schedule."
    Minimum $report.DurationSeconds $budget.minimumSecondsPerRun "$label configured seconds"
    Minimum $report.MeasuredSeconds ($budget.minimumSecondsPerRun - 1) "$label measured seconds"
    Require ($report.SeededDocuments -eq ($budget.tenants * $budget.documentsPerTenant) -and $report.ExactVersionRows -eq $report.SeededDocuments) "$label has incomplete final corpus verification."
    Require ($report.StaleFenceRejected -eq $true -and $report.SameVersionReplayAccepted -eq $true -and $report.ConflictRejected -eq $true -and $report.AuthorizedRetrievalVerified -eq $true -and $report.QueryRetentionDrained -eq $true) "$label failed version, ACL or terminal-drain checks."
    Require ($report.WritesOffered -eq $report.WritesAccepted + $report.WritesRejected -and $report.ReadsOffered -eq $report.ReadsAccepted + $report.ReadsRejected) "$label has incomplete offered/admitted accounting."
    Require (@($report.TenantProgress).Count -eq $budget.tenants) "$label has incomplete tenant progress."
    foreach ($tenant in $report.TenantProgress) {
        Require ($tenant.WritesAccepted -gt 0 -and $tenant.ReadsAccepted -gt 0 -and
            $tenant.WritesOffered -eq $tenant.WritesAccepted + $tenant.WritesRejected -and
            $tenant.ReadsOffered -eq $tenant.ReadsAccepted + $tenant.ReadsRejected) "$label lost tenant progress."
    }
    foreach ($name in @('WritesOffered','WritesAccepted','WritesRejected','WriteScheduleSkipped','ReadsOffered','ReadsAccepted','ReadsRejected','ReadScheduleSkipped')) {
        Require (($report.TenantProgress | Measure-Object -Property $name -Sum).Sum -eq $report.$name) "$label tenant $name totals differ from the report."
    }
    $writeSlots = $report.WritesOffered + $report.WriteScheduleSkipped
    $readSlots = $report.ReadsOffered + $report.ReadScheduleSkipped
    Minimum $writeSlots (0.99 * $report.DurationSeconds * $budget.writers * 1000 / $budget.writeOfferIntervalMilliseconds) "$label scheduled write slots"
    Minimum $readSlots (0.99 * $report.DurationSeconds * $budget.readers * 1000 / $budget.readOfferIntervalMilliseconds) "$label scheduled read slots"
    Maximum ($report.WriteScheduleSkipped / [double]$writeSlots) $budget.maximumWriteScheduleSkippedFraction "$label missed write schedule fraction"
    Maximum ($report.ReadScheduleSkipped / [double]$readSlots) $budget.maximumReadScheduleSkippedFraction "$label missed read schedule fraction"
    Minimum ($report.WritesAccepted / $report.MeasuredSeconds) $budget.minimumWritesPerSecond "$label accepted writes/s"
    Minimum ($report.ReadsAccepted / $report.MeasuredSeconds) $budget.minimumReadsPerSecond "$label accepted reads/s"
    Maximum ($report.WritesRejected / [double]$report.WritesOffered) $budget.maximumWriteRejectionFraction "$label write rejection fraction"
    Maximum ($report.ReadsRejected / [double]$report.ReadsOffered) $budget.maximumReadRejectionFraction "$label read rejection fraction"
    Minimum $report.MaintenancePruneAttempts ([math]::Floor($report.DurationSeconds / 6)) "$label maintenance prune attempts"
    Require ($report.MaintenancePruneRejected -ge 0 -and $report.MaintenancePruneRejected -le $report.MaintenancePruneAttempts -and
        $report.MaintenancePruneRemoved -ge 0) "$label has invalid maintenance prune accounting."
    Maximum ($report.MaintenancePruneRejected / [double]$report.MaintenancePruneAttempts) $budget.maximumMaintenancePruneRejectionFraction "$label maintenance prune rejection fraction"
    Require ($report.WriteLatency.Count -eq $report.WritesAccepted -and $report.ReadLatency.Count -eq $report.ReadsAccepted) "$label latency samples differ from accepted operations."
    foreach ($pair in @(@('WritesRejected','RejectedWriteLatency'), @('ReadsRejected','RejectedReadLatency'))) {
        $rejections = $report.($pair[0])
        $latency = $report.($pair[1])
        if ($rejections -eq 0) { Require ($null -eq $latency) "$label has unexpected rejected-admission latency evidence." }
        else {
            Require ($null -ne $latency -and $latency.Count -eq $rejections) "$label lacks rejected-admission timing evidence."
            Maximum $latency.P99Milliseconds $budget.maximumRejectedAdmissionP99Milliseconds "$label rejected-admission p99"
        }
    }
    Minimum $report.WriteLatency.P99Milliseconds 0.001 "$label write p99"
    Minimum $report.ReadLatency.P99Milliseconds 0.001 "$label read p99"
    Maximum $report.WriteLatency.P99Milliseconds $budget.maximumWriteP99Milliseconds "$label write p99"
    Maximum $report.ReadLatency.P99Milliseconds $budget.maximumReadP99Milliseconds "$label read p99"
    $samples = @($report.StorageSamples)
    Require ($samples.Count -ge [math]::Floor($budget.minimumSecondsPerRun / 6) -and $samples[0].ElapsedSeconds -le 1 -and $samples[-1].ElapsedSeconds -ge $budget.minimumSecondsPerRun - 10) "$label has sparse or incomplete five-second physical observations."
    Require ([math]::Abs($samples[-1].ElapsedSeconds - $report.MeasuredSeconds) -le 10) "$label physical observations do not cover measured time."
    Require ((Number $samples[0].OwnedRelationBytes "$label initial owned bytes") -gt 0 -and
        (Number $samples[0].DatabaseBytes "$label initial database bytes") -gt 0 -and
        (Number $samples[0].WalInsertBytes "$label initial WAL bytes") -ge 0) "$label has an invalid initial physical sample."
    for ($sample = 1; $sample -lt $samples.Count; $sample++) {
        Require ($samples[$sample].ElapsedSeconds -gt $samples[$sample - 1].ElapsedSeconds -and
            $samples[$sample].ElapsedSeconds - $samples[$sample - 1].ElapsedSeconds -le 10 -and
            (Number $samples[$sample].WalInsertBytes "$label WAL sample") -ge (Number $samples[$sample - 1].WalInsertBytes "$label previous WAL sample") -and
            (Number $samples[$sample].OwnedRelationBytes "$label owned sample") -gt 0 -and
            (Number $samples[$sample].DatabaseBytes "$label database sample") -gt 0) "$label has a broken or invalid physical time series."
    }
    Require ($report.AfterDrain.ElapsedSeconds -ge $samples[-1].ElapsedSeconds -and
        (Number $report.AfterDrain.OwnedRelationBytes "$label post-drain owned bytes") -gt 0 -and
        (Number $report.AfterDrain.DatabaseBytes "$label post-drain database bytes") -gt 0) "$label lacks a valid post-drain physical sample."
    $peakOwned = ($samples + @($report.AfterDrain) | Measure-Object -Property OwnedRelationBytes -Maximum).Maximum
    $peakDatabase = ($samples + @($report.AfterDrain) | Measure-Object -Property DatabaseBytes -Maximum).Maximum
    Minimum $peakOwned 1 "$label owned relation bytes"
    Maximum $peakOwned $budget.maximumOwnedRelationBytes "$label owned relation peak"
    Maximum $peakDatabase $budget.maximumDatabaseBytes "$label database peak"
    $late = @($samples | Where-Object { $_.ElapsedSeconds -ge $report.MeasuredSeconds / 2 })
    Require ($late.Count -ge 3 -and ($late[-1].ElapsedSeconds - $late[0].ElapsedSeconds) -ge $budget.minimumSecondsPerRun / 3) "$label has no complete late storage window."
    $growthPerMinute = ($late[-1].OwnedRelationBytes - $late[0].OwnedRelationBytes) / (($late[-1].ElapsedSeconds - $late[0].ElapsedSeconds) / 60.0)
    Maximum $growthPerMinute $budget.maximumLateOwnedGrowthBytesPerMinute "$label late owned-relation growth"
    $wal = (Number $samples[-1].WalInsertBytes "$label ending WAL") - (Number $samples[0].WalInsertBytes "$label starting WAL")
    Require ($wal -ge 0) "$label WAL counter regressed."
    Maximum ($wal / $report.WritesAccepted) $budget.maximumWalBytesPerAcceptedWrite "$label WAL bytes/accepted write"
}

if ($Candidate) { Write-Output "Search candidate metrics passed for $head; final capacity manifest is pending." }
else { Write-Output "Search local capacity evidence passed for $head across $($manifest.Runs) exact-source runs. Production qualification remains false." }
