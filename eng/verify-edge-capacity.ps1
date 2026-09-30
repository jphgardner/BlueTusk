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
$budgetPath = Join-Path $PSScriptRoot 'edge-capacity-budgets.json'
$budget = Get-Content -LiteralPath $budgetPath -Raw | ConvertFrom-Json -Depth 30

function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Json([string]$Path) {
    Require (Test-Path -LiteralPath $Path -PathType Leaf) "Missing Edge evidence: $Path"
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 60
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Number($Value, [string]$Name) {
    Require ($null -ne $Value) "$Name is missing."
    $parsed = [double]$Value
    Require ([double]::IsFinite($parsed)) "$Name is not finite."
    return $parsed
}
function Minimum($Value, $Limit, [string]$Name) {
    Require ((Number $Value $Name) -ge (Number $Limit "$Name budget")) "$Name is below its minimum $Limit."
}
function Maximum($Value, $Limit, [string]$Name) {
    Require ((Number $Value $Name) -le (Number $Limit "$Name budget")) "$Name exceeds its maximum $Limit."
}
function Instant($Value, [string]$Name) {
    Require ($null -ne $Value) "$Name is missing."
    if ($Value -is [DateTime]) {
        return [DateTimeOffset]::new($Value.ToUniversalTime(), [TimeSpan]::Zero)
    }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
}

Require ($ExpectedCommit -match '^[0-9a-fA-F]{40}$') 'A full exact candidate SHA is required.'
Require ($evidence.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'EvidenceDirectory must be inside the checkout.'
$head = (& git -C $root rev-parse HEAD).Trim()
Require ($LASTEXITCODE -eq 0 -and [string]::Equals($head, $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) 'Verifier checkout is not the requested candidate.'
Require (@(& git -C $root status --porcelain --untracked-files=normal).Count -eq 0) 'Verifier checkout must be clean.'
Require ($budget.schemaVersion -eq 1 -and $budget.minimumRuns -eq 2 -and $budget.minimumSecondsPerRun -ge 1800 -and
    $budget.clients -eq 8 -and $budget.sqliteClients -eq 7 -and $budget.browserClients -eq 1) 'Unsupported Edge capacity budget contract.'
$manifest = Json (Join-Path $evidence 'manifest.json')
Require ($manifest.SchemaVersion -eq 1 -and $manifest.ProductionQualified -eq $false -and
    ($Candidate -or $manifest.QualifiedLocalCapacity -eq $true)) 'Edge evidence is not a completed local campaign.'
Require ($manifest.CandidateSha -ceq $head -and $manifest.Runs -ge $budget.minimumRuns) 'Manifest candidate or repetition count differs.'
Require ((Hash $budgetPath) -ceq $manifest.BudgetSha256 -and (Hash (Join-Path $evidence 'budgets.json')) -ceq $manifest.BudgetSha256) 'Budget snapshot differs from candidate.'
$before = Json (Join-Path $evidence 'source-before.json')
$after = Json (Join-Path $evidence 'source-after.json')
foreach ($source in @($before, $after)) {
    Require ($source.commit -ceq $head -and $source.dirty -eq $false) 'Source capture does not describe a clean exact candidate.'
}
Require ($before.sourceTreeSha256 -ceq $after.sourceTreeSha256 -and $before.sourceTreeSha256 -ceq $manifest.SourceTreeSha256) 'Candidate source changed during Edge measurement.'
$runner = Json (Join-Path $evidence 'runner.json')
Require ($runner.CandidateSha -ceq $head -and $runner.SourceTreeSha256 -ceq $before.sourceTreeSha256 -and
    $runner.PostgreSqlImage -ceq $budget.postgreSqlImage -and $runner.DockerCpus -eq 4 -and
    $runner.DockerMemoryMiB -eq 2048 -and $runner.Runs -eq $manifest.Runs -and
    -not [string]::IsNullOrWhiteSpace([string]$runner.BrowserChannel)) 'Runner provenance or fixture contract differs.'

$binaryRoot = Join-Path $evidence 'binary-snapshot'
$binaries = @((Json (Join-Path $binaryRoot 'binaries.json')).Files)
Require ($binaries.Count -gt 0) 'The Edge executable snapshot is empty.'
$binaryNames = @{}
foreach ($entry in $binaries) {
    Require ([string]$entry.Name -match '^[A-Za-z0-9_.-]+$' -and -not $binaryNames.ContainsKey([string]$entry.Name)) 'Binary snapshot name is unsafe or repeated.'
    $binaryNames[[string]$entry.Name] = $true
    Require ((Hash (Join-Path $binaryRoot $entry.Name)) -ceq $entry.Sha256) "Binary snapshot hash changed: $($entry.Name)"
}
Require ((Hash (Join-Path $binaryRoot 'BlueTusk.Edge.LoadHarness.dll')) -ceq $manifest.HarnessBinarySha256) 'Harness binary differs from manifest.'
$listed = @($manifest.Files)
$actual = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $evidence 'manifest.json') })
Require ($listed.Count -gt 0 -and $listed.Count -eq $actual.Count) 'Artifact inventory differs from manifest.'
$seen = @{}
foreach ($entry in $listed) {
    Require ($entry.Path -match '^[^/\\]+(?:/[^/\\]+)*$' -and -not $entry.Path.Contains('..') -and
        -not $seen.ContainsKey([string]$entry.Path)) 'Manifest path is unsafe or duplicated.'
    $seen[[string]$entry.Path] = $true
    $path = Join-Path $evidence $entry.Path
    Require (Test-Path -LiteralPath $path -PathType Leaf) "Manifest artifact is missing: $($entry.Path)"
    Require ([string]::Equals([string]$entry.Sha256, (Hash $path), [StringComparison]::OrdinalIgnoreCase)) "Manifest artifact hash differs: $($entry.Path)"
}

$previousCompletion = [DateTimeOffset]::MinValue
$reportHashes = @{}
for ($run = 1; $run -le $manifest.Runs; $run++) {
    $runRoot = Join-Path $evidence "run-$run"
    $label = "Edge run $run"
    $runSource = Json (Join-Path $runRoot 'source-after.json')
    Require ($runSource.commit -ceq $head -and $runSource.dirty -eq $false -and
        $runSource.sourceTreeSha256 -ceq $before.sourceTreeSha256) "$label changed candidate source."
    $assetPath = Join-Path $runRoot 'browser-assets.json'
    $assets = @((Json $assetPath).Files)
    Require ($assets.Count -eq 4) "$label lacks the exact browser asset snapshot."
    foreach ($name in @('index.js','http.js','ordered.js','browser.mjs')) {
        $entry = @($assets | Where-Object Name -ceq $name)
        Require ($entry.Count -eq 1) "$label browser asset $name is missing or duplicated."
        $path = if ($name -eq 'browser.mjs') { Join-Path $runRoot $name } else { Join-Path (Join-Path $runRoot 'browser-assets') $name }
        Require ((Hash $path) -ceq $entry[0].Sha256) "$label browser asset $name differs from its snapshot."
    }
    $browser = Json (Join-Path $runRoot 'browser.json')
    $reportPath = Join-Path $runRoot 'edge-ordered.json'
    $report = Json $reportPath
    $reportHash = Hash $reportPath
    Require (-not $reportHashes.ContainsKey($reportHash)) "$label duplicates another run's raw report."
    $reportHashes[$reportHash] = $true
    $started = Instant $report.StartedUtc "$label start"
    $completed = Instant $report.CompletedUtc "$label completion"
    Require ($started -gt $previousCompletion -and $completed -gt $started) "$label timestamps overlap or are invalid."
    $previousCompletion = $completed
    Require ($report.FormatVersion -eq 2 -and $report.Passed -eq $true -and $report.ProductionQualified -eq $false) "$label lacks successful raw correctness evidence."
    Require ($report.CandidateSha -ceq $head -and $report.SourceTreeSha256 -ceq $before.sourceTreeSha256 -and
        $report.HarnessBinarySha256 -ceq $manifest.HarnessBinarySha256 -and
        $report.BrowserBundleSha256 -ceq (Hash $assetPath)) "$label is not bound to its measured binaries and source."
    Require ($report.Workload -ceq $budget.workload -and $report.PostgreSqlImage -ceq $budget.postgreSqlImage -and
        [string]$report.PostgreSqlVersion -match '^PostgreSQL 18\.') "$label used the wrong workload or PostgreSQL fixture."
    Minimum $report.DurationSeconds $budget.minimumSecondsPerRun "$label configured seconds"
    Minimum $report.MeasuredSeconds ($report.DurationSeconds - 1) "$label measured seconds"
    Maximum $report.DrainSeconds $budget.maximumDrainSeconds "$label drain seconds"
    Require ($report.SeededRecords -eq ($budget.clients * $budget.recordsPerScope) -and
        $report.BusinessEffects -gt 0 -and $report.ExactBusinessEffects -eq $true -and
        $report.ExactFeedState -eq $true -and $report.HostRestarted -eq $true -and
        $report.LaggingReaderResnapshotted -eq $true) "$label lacks complete business, feed or fault verification."
    $clients = @($report.Clients)
    Require ($clients.Count -eq $budget.clients) "$label lacks an eight-client report."
    Require (($clients | Measure-Object -Property Acknowledged -Sum).Sum -eq $report.BusinessEffects) "$label business count differs from client acknowledgements."
    foreach ($index in 0..7) {
        $matches = @($clients | Where-Object Index -eq $index)
        Require ($matches.Count -eq 1) "$label client $index is missing or duplicated."
        $client = $matches[0]
        Require ($client.Kind -ceq $(if ($index -eq 7) { 'IndexedDB' } else { 'SQLite' }) -and
            [string]$client.OrderedStreamId -match '^[0-9a-f]{15}$') "$label client $index used the wrong durable store or ordered identity."
        $planned = $client.Offered + $client.Skipped
        $expectedSlots = [long]([long]$report.DurationSeconds * 1000 / [long]$budget.offerIntervalMilliseconds)
        Require ($planned -eq $expectedSlots) "$label client $index offered and skipped slots differ from the fixed schedule."
        Require ($client.ScheduleSkipped -ge 0 -and $client.PendingKeySkipped -ge 0 -and
            $client.ScheduleSkipped + $client.PendingKeySkipped -eq $client.Skipped) "$label client $index skipped-slot causes differ from the total."
        Maximum ($client.Skipped / [double]$planned) $budget.maximumSkippedFraction "$label client $index skipped fraction"
        Require ($client.Offered -gt 0 -and $client.Acknowledged -eq $client.Offered -and
            $client.ExpectedConflicts -eq 0 -and $client.UnexpectedConflicts -eq 0 -and
            $client.FinalPending -eq 0 -and $client.FinalOutbox -eq 0 -and $client.FinalLocalReceipts -eq 0 -and
            $client.FinalOrderedSequence -eq $client.Acknowledged -and $client.FinalHorizon -eq $client.FinalOrderedSequence -and
            $client.FinalCheckpoint -eq ($budget.recordsPerScope + $client.Acknowledged) -and
            $client.LostResponseRecovered -eq $true -and $client.ReclaimedRetryFenced -eq $true -and
            $client.ExactFinalCache -eq $true) "$label client $index did not drain or preserve exact ordered state."
        Maximum $client.PeakPending $budget.maximumPendingPerClient "$label client $index peak pending"
        Maximum $client.PeakOutbox $budget.maximumPendingPerClient "$label client $index peak confirmation outbox"
        $maximumPhysical = if ($index -eq 7) { $budget.maximumBrowserProfileBytes } else { $budget.maximumSqliteFileAndWalBytes }
        Minimum $client.MaximumPhysicalBytes 1 "$label client $index physical observation"
        Maximum $client.MaximumPhysicalBytes $maximumPhysical "$label client $index physical footprint"
        foreach ($pair in @(@('Enqueue','maximumEnqueueP99Milliseconds'), @('HttpApply','maximumHttpApplyP99Milliseconds'),
                @('DurableAck','maximumDurableAckP99Milliseconds'), @('Horizon','maximumHorizonP99Milliseconds'))) {
            $latency = $client.($pair[0])
            Minimum $latency.Samples 100 "$label client $index $($pair[0]) samples"
            Minimum $latency.P99Milliseconds 0.001 "$label client $index $($pair[0]) p99"
            Maximum $latency.P99Milliseconds $budget.($pair[1]) "$label client $index $($pair[0]) p99"
        }
        Minimum ($client.DurableAck.Samples / [double]$client.Acknowledged) $budget.minimumDurableAckSampleFraction "$label client $index steady acknowledgement coverage"
        Require (@($client.FaultRecoverySeconds).Count -eq 4) "$label client $index omitted an injected fault recovery."
        foreach ($recovery in $client.FaultRecoverySeconds) {
            Minimum $recovery 0 "$label client $index recovery seconds"
            Maximum $recovery $budget.maximumFaultRecoverySeconds "$label client $index recovery seconds"
        }
    }
    Require (($browser | ConvertTo-Json -Depth 20 -Compress) -ceq
        ($clients[7] | ConvertTo-Json -Depth 20 -Compress)) "$label browser worker and combined report differ."
    Minimum ($report.BusinessEffects / $report.DurationSeconds) $budget.minimumDurableApplicationsPerSecond "$label durable applications/s"

    $samples = @($report.StorageSamples)
    Minimum $samples.Count ([math]::Floor($report.DurationSeconds / 10)) "$label physical sample count"
    Require ($samples[0].ElapsedSeconds -le 2 -and $samples[-1].ElapsedSeconds -ge ($report.DurationSeconds - 15)) "$label physical sampling misses the offered window."
    $previous = -1.0
    $peakOwned = 0.0; $peakDatabase = 0.0
    foreach ($sample in @($samples) + @($report.AfterDrain)) {
        $elapsed = Number $sample.ElapsedSeconds "$label physical elapsed"
        Require ($elapsed -gt $previous -and ($previous -lt 0 -or $elapsed - $previous -le 30)) "$label physical samples have a gap or invalid order."
        $previous = $elapsed
        Minimum $sample.OwnedRelationBytes 1 "$label owned relation observation"
        Minimum $sample.DatabaseBytes 1 "$label whole database observation"
        Minimum $sample.WalInsertBytes 1 "$label WAL position"
        Maximum $sample.ReceiptCount ($budget.clients * $budget.maximumReceiptsPerScope) "$label retained receipts"
        Maximum $sample.ReceiptBytes ($budget.clients * $budget.maximumReceiptBytesPerScope) "$label retained receipt bytes"
        Maximum $sample.ChangeCount ($budget.clients * $budget.maximumChangesPerScope) "$label retained feed rows"
        Maximum $sample.ChangeBytes ($budget.clients * $budget.maximumChangeBytesPerScope) "$label retained feed bytes"
        $peakOwned = [math]::Max($peakOwned, $sample.OwnedRelationBytes)
        $peakDatabase = [math]::Max($peakDatabase, $sample.DatabaseBytes)
    }
    Maximum $peakOwned $budget.maximumOwnedRelationBytes "$label owned relation peak"
    Maximum $peakDatabase $budget.maximumWholeDatabaseBytes "$label whole database peak"
    $late = @($samples | Where-Object { $_.ElapsedSeconds -ge $report.DurationSeconds / 2 -and $_.ElapsedSeconds -le $report.DurationSeconds })
    Require ($late.Count -ge 3 -and $late[-1].ElapsedSeconds - $late[0].ElapsedSeconds -ge $report.DurationSeconds / 3) "$label lacks a complete late-growth window."
    $growth = ($late[-1].OwnedRelationBytes - $late[0].OwnedRelationBytes) / (($late[-1].ElapsedSeconds - $late[0].ElapsedSeconds) / 60.0)
    Maximum $growth $budget.maximumLateOwnedGrowthBytesPerMinute "$label late owned-relation growth"
    $wal = (Number $report.AfterDrain.WalInsertBytes "$label ending WAL") - (Number $samples[0].WalInsertBytes "$label starting WAL")
    Minimum $wal 0 "$label WAL delta"
    Maximum ($wal / $report.BusinessEffects) $budget.maximumWalBytesPerApplied "$label WAL bytes/applied"
}

if ($Candidate) { Write-Output "Edge candidate metrics passed for $head; the final capacity manifest is pending." }
else { Write-Output "Edge local capacity evidence passed for $head across $($manifest.Runs) exact-source runs. Production qualification remains false." }
