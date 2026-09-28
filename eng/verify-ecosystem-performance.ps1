[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Run', 'Verify')][string]$Mode = 'Verify',
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [string]$EvidenceRoot = 'artifacts/ecosystem-performance'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = [IO.Path]::GetFullPath((Join-Path $repository $EvidenceRoot))
$budgetPath = Join-Path $PSScriptRoot 'ecosystem-performance-budgets.json'
$budget = Get-Content -LiteralPath $budgetPath -Raw | ConvertFrom-Json -Depth 30

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Number($Value, [string]$Name) {
    Require ($null -ne $Value) "$Name is missing."
    $parsed = [double]$Value
    Require ([double]::IsFinite($parsed)) "$Name is not finite."
    return $parsed
}
function AtLeast($Value, $Minimum, [string]$Name) {
    Require ((Number $Value $Name) -ge (Number $Minimum "$Name budget")) "$Name is below its minimum $Minimum."
}
function AtMost($Value, $Maximum, [string]$Name) {
    Require ((Number $Value $Name) -le (Number $Maximum "$Name budget")) "$Name exceeds its maximum $Maximum."
}
function Positive($Value, [string]$Name) { Require ((Number $Value $Name) -gt 0) "$Name must be positive." }
function Json([string]$Path) {
    Require (Test-Path -LiteralPath $Path -PathType Leaf) "Missing evidence: $Path"
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function CaptureSource([string]$Path) {
    & python eng/capture-ecosystem-source.py --output $Path
    Require ($LASTEXITCODE -eq 0) 'Candidate source capture failed.'
}
function SnapshotBinaries([string]$Root) {
    $projects = [ordered]@{
        jobs = 'benchmarks/BlueTusk.Workflows.LoadHarness'
        projections = 'benchmarks/BlueTusk.Projections.LoadHarness'
        documents = 'benchmarks/BlueTusk.Documents.LoadHarness'
    }
    $records = @()
    foreach ($name in $projects.Keys) {
        $project = $projects[$name]
        & dotnet build "$project/$([IO.Path]::GetFileName($project)).csproj" -c Release -nr:false --nologo
        Require ($LASTEXITCODE -eq 0) "The $name harness did not build."
        $binaryDirectory = Join-Path $repository "$project/bin/Release/net10.0"
        Require (Test-Path -LiteralPath $binaryDirectory -PathType Container) "The $name harness has no Release binaries."
        $destination = Join-Path $Root $name
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $files = @(Get-ChildItem -LiteralPath $binaryDirectory -File | Where-Object { $_.Extension -in @('.dll', '.exe', '.json') } | Sort-Object Name)
        Require ($files.Count -gt 0) "The $name harness has no executable snapshot."
        foreach ($file in $files) {
            $copy = Join-Path $destination $file.Name
            Copy-Item -LiteralPath $file.FullName -Destination $copy
            $sha = Hash $file.FullName
            Require ((Hash $copy) -ceq $sha) "The $name binary copy changed."
            $records += [ordered]@{ Product = $name; Name = $file.Name; Sha256 = $sha }
        }
    }
    [ordered]@{ Files = $records } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Root 'binaries.json') -Encoding utf8
}
function VerifyLiveBinaries([string]$Root) {
    $records = @( (Json (Join-Path $Root 'binaries.json')).Files )
    Require ($records.Count -gt 0) 'The binary snapshot manifest is empty.'
    $projects = @{ jobs = 'benchmarks/BlueTusk.Workflows.LoadHarness'; projections = 'benchmarks/BlueTusk.Projections.LoadHarness'; documents = 'benchmarks/BlueTusk.Documents.LoadHarness' }
    foreach ($entry in $records) {
        Require ($projects.ContainsKey([string]$entry.Product) -and [string]$entry.Name -match '^[A-Za-z0-9_.-]+$' -and [string]$entry.Name -notin @('.', '..')) 'Binary snapshot entry is invalid.'
        $live = Join-Path $repository "$($projects[[string]$entry.Product])/bin/Release/net10.0/$($entry.Name)"
        $copy = Join-Path $Root "$($entry.Product)/$($entry.Name)"
        Require ((Test-Path -LiteralPath $live -PathType Leaf) -and (Test-Path -LiteralPath $copy -PathType Leaf)) 'A candidate binary is missing.'
        Require ((Hash $live) -ceq $entry.Sha256 -and (Hash $copy) -ceq $entry.Sha256) 'A candidate binary changed during measurement.'
    }
}
function VerifyArchivedBinaries([string]$Root) {
    $records = @((Json (Join-Path $Root 'binaries.json')).Files)
    Require ($records.Count -gt 0) 'The archived binary snapshot is empty.'
    $seen = @{}
    foreach ($entry in $records) {
        Require ([string]$entry.Product -in @('jobs', 'projections', 'documents')) 'The archived binary product is unknown.'
        Require ([string]$entry.Name -match '^[A-Za-z0-9_.-]+$' -and [string]$entry.Name -notin @('.', '..')) 'The archived binary name is unsafe.'
        $key = "$($entry.Product)/$($entry.Name)"
        Require (-not $seen.ContainsKey($key)) 'The archived binary list contains duplicate entries.'
        $seen[$key] = $true
        $path = Join-Path $Root $key
        Require (Test-Path -LiteralPath $path -PathType Leaf) "Archived binary is missing: $key"
        Require ((Hash $path) -ceq $entry.Sha256) "Archived binary hash changed: $key"
    }
}
function InvokeCampaign([string]$Name, [scriptblock]$Command, [string]$Log) {
    Write-Output "Starting $Name."
    & $Command 2>&1 | Tee-Object -FilePath $Log
    Require ($LASTEXITCODE -eq 0) "$Name failed; partial raw evidence is retained."
}
function VerifySource($Before, $After) {
    foreach ($capture in @($Before, $After)) {
        Require (-not $capture.dirty) 'Source capture reports a dirty candidate.'
        Require ([string]::Equals([string]$capture.commit, $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) 'Source capture does not identify the requested commit.'
        Require ([string]$capture.sourceTreeSha256 -match '^[0-9a-fA-F]{64}$') 'Source capture hash is missing.'
    }
    Require ($Before.sourceTreeSha256 -ceq $After.sourceTreeSha256) 'Candidate source changed during measurement.'
}
function VerifyJobs($Root, $Configuration, $Run) {
    $path = Join-Path $Root 'jobs-workflows.json'
    $report = Json $path
    $environment = Json ($path + '.environment.json')
    Require ($report.Profile -ceq 'storage') "Jobs/Workflows run $Run has the wrong profile."
    Require ($report.PayloadMode -ceq $Configuration.payloadMode -and $environment.PayloadMode -ceq $Configuration.payloadMode) "Jobs/Workflows run $Run did not attest the high-entropy payload."
    Require ([string]$report.PostgreSqlVersion -match '^PostgreSQL 15\.' -and [string]$environment.PostgreSql.server -match '^PostgreSQL 15\.') "Jobs/Workflows run $Run used the wrong PostgreSQL version."
    Require ([string]$report.SourceSha256 -match '^[0-9a-fA-F]{64}$') "Jobs/Workflows run $Run has no source fingerprint."
    Require (@($report.Faults).Count -eq 5 -and @($report.Faults | Where-Object { $_.Passed -ne $true }).Count -eq 0) "Jobs/Workflows run $Run failed recovery verification."
    Require (@($report.Overload).Count -eq 2) "Jobs/Workflows run $Run must include both products."
    foreach ($product in @('Jobs', 'Workflows')) {
        $entries = @($report.Overload | Where-Object { $_.Product -ceq $product })
        Require ($entries.Count -eq 1) "Jobs/Workflows run $Run has missing or duplicate $product results."
        $item = $entries[0]
        Require ($item.PayloadMode -ceq $Configuration.payloadMode -and $item.Verified -eq $true) "$product run $Run did not verify the required payload mode and invariants."
        AtLeast $item.OfferedDurationSeconds $Configuration.secondsPerProduct "$product run $Run offered seconds"
        Require ($item.Accepted -gt 0 -and $item.DurableEffects -eq $item.Accepted -and $item.PrunedPrimaryRows -eq $item.Accepted) "$product run $Run has incomplete durable effects or pruning."
        AtLeast $item.CompletionsPerSecond $Configuration.minimumCompletionsPerSecond.$product "$product run $Run completions/s"
        $clusterWalBytes = ([decimal]$item.After.WalPosition) - ([decimal]$item.Before.WalPosition)
        Require ($clusterWalBytes -gt 0) "$product run $Run has no positive cluster WAL observation."
        AtMost ($clusterWalBytes / $item.Accepted) $Configuration.maximumClusterWalBytesPerAccepted.$product "$product run $Run cluster WAL bytes/accepted"
        Require (@($item.Tenants).Count -eq 4) "$product run $Run has incomplete tenant evidence."
        Positive $item.Tenants[0].DurableLatencyMilliseconds.P99 "$product run $Run hot durable p99"
        AtMost $item.Tenants[0].DurableLatencyMilliseconds.P99 $Configuration.maximumHotDurableP99Milliseconds.$product "$product run $Run hot durable p99"
        foreach ($tenant in @($item.Tenants | Select-Object -Skip 1)) {
            Require ($tenant.Accepted -gt 0 -and $tenant.Rejected -eq 0) "$product run $Run lost cold-tenant progress."
            Positive $tenant.DurableLatencyMilliseconds.P99 "$product run $Run cold durable p99"
            AtMost $tenant.DurableLatencyMilliseconds.P99 $Configuration.maximumColdDurableP99Milliseconds.$product "$product run $Run cold durable p99"
        }
        $samples = @($item.StorageSamples)
        Require ($samples.Count -ge [math]::Floor($Configuration.secondsPerProduct / 5)) "$product run $Run has too few storage samples."
        Require ($samples[0].ElapsedSeconds -le 15 -and $samples[-1].ElapsedSeconds -ge $Configuration.secondsPerProduct - 20) "$product run $Run has incomplete storage coverage."
        $peak = ($samples | Measure-Object -Property RuntimeRelationBytes -Maximum).Maximum
        Positive $peak "$product run $Run runtime relation peak"
        AtMost $peak $Configuration.maximumRuntimeRelationBytes.$product "$product run $Run runtime relation peak"
        $late = @($samples | Where-Object { $_.ElapsedSeconds -ge $Configuration.secondsPerProduct / 2 })
        Require ($late.Count -ge 3 -and ($late[-1].ElapsedSeconds - $late[0].ElapsedSeconds) -ge $Configuration.secondsPerProduct / 3) "$product run $Run has no complete late storage window."
        $growthPerMinute = ($late[-1].RuntimeRelationBytes - $late[0].RuntimeRelationBytes) / (($late[-1].ElapsedSeconds - $late[0].ElapsedSeconds) / 60.0)
        AtMost $growthPerMinute $Configuration.maximumLateGrowthBytesPerMinute.$product "$product run $Run late storage growth bytes/min"
    }
    return [string]$report.SourceSha256
}
function VerifyProjections($Root, $Configuration, $Run, $Source) {
    $metadata = Json (Join-Path $Root 'campaign.json')
    Require ($metadata.Profile -ceq 'soak' -and $metadata.Seconds -eq $Configuration.secondsPerRun -and $metadata.Repetitions -eq 1) "Projections run $Run has the wrong campaign configuration."
    Require ($metadata.ImageDigest -ceq $budget.postgreSql18Image) "Projections run $Run used the wrong PostgreSQL image."
    Require ($metadata.Passed -eq $true -and $metadata.CandidateUnchanged -eq $true) "Projections run $Run failed or changed candidate."
    VerifySource (Json (Join-Path $Root 'source-before.json')) (Json (Join-Path $Root 'source-after.json'))
    $before = Json (Join-Path $Root 'source-before.json')
    Require ($before.sourceTreeSha256 -ceq $Source.sourceTreeSha256) "Projections run $Run is not bound to the gate candidate."
    $report = Json (Join-Path $Root 'run-1.json')
    Require ($report.Profile -ceq 'soak' -and $report.Passed -eq $true -and @($report.Scenarios).Count -eq 1) "Projections run $Run is incomplete."
    $item = $report.Scenarios[0]
    Require ($item.Configuration.Name -ceq 'overload-fairness' -and $item.Configuration.Seconds -eq $Configuration.secondsPerRun -and $item.ExactStateVerified -eq $true) "Projections run $Run lacks exact-state verification."
    Require ([string]$item.PostgreSql -match '^PostgreSQL 18\.') "Projections run $Run used the wrong PostgreSQL version."
    AtLeast $item.MeasuredSeconds ($Configuration.secondsPerRun - 1) "Projections run $Run measured seconds"
    Require ($item.Committed -gt 0 -and $item.WalTransactions -ge $item.Committed -and $item.InboxEffects -ge $item.Committed) "Projections run $Run has missing durable transactions."
    AtLeast $item.TransactionsPerSecond $Configuration.minimumTransactionsPerSecond "Projections run $Run transactions/s"
    foreach ($pair in @(@('Commit', 'maximumCommitP99Milliseconds'), @('Projection', 'maximumProjectionP99Milliseconds'), @('Inbox', 'maximumInboxP99Milliseconds'))) {
        $latency = $item.($pair[0])
        Require ($latency.Samples -gt 0) "Projections run $Run has no $($pair[0]) latency samples."
        Positive $latency.P99Milliseconds "Projections run $Run $($pair[0]) p99"
        AtMost $latency.P99Milliseconds $Configuration.($pair[1]) "Projections run $Run $($pair[0]) p99"
    }
    Positive $item.Runtime.MaximumOwnedStorageBytes "Projections run $Run owned storage peak"
    AtMost $item.Runtime.MaximumOwnedStorageBytes $Configuration.maximumOwnedStorageBytes "Projections run $Run owned storage peak"
    AtMost $item.Runtime.MaximumRetainedSlotBytes $Configuration.maximumRetainedSlotBytes "Projections run $Run retained slot peak"
    $walDelta = (Number $item.After.WalBytes 'Projections after WAL bytes') - (Number $item.Before.WalBytes 'Projections before WAL bytes')
    Require ($walDelta -ge 0) "Projections run $Run WAL counter regressed."
    AtMost ($walDelta / $item.Committed) $Configuration.maximumWalBytesPerTransaction "Projections run $Run WAL bytes/transaction"
    $windows = @($item.ServiceWindows)
    Require ($windows.Count -ge [math]::Floor($Configuration.secondsPerRun / 15) -and $windows[-1].ElapsedSeconds -ge $Configuration.secondsPerRun - 20) "Projections run $Run lacks continuous service-window evidence."
}
function VerifyDocuments($Root, $Configuration, $Run) {
    $path = Join-Path $Root 'documents-load.json'
    $bindings = Json (Join-Path $Root 'bindings.json')
    $report = Json $path
    Require ($report.Environment.PostgreSqlImage -ceq $budget.postgreSql18Image -and [string]$report.Environment.PostgreSqlVersion -match '^PostgreSQL 18\.') "Documents run $Run used the wrong PostgreSQL image or version."
    $sustained = @($report.Scenarios | Where-Object { $_.Name -ceq 'sustained-fixed-cardinality-churn' })
    Require ($sustained.Count -eq 1 -and $sustained[0].StorageMode -ceq $Configuration.storageMode) "Documents run $Run did not measure attached content in the sustained scenario."
    Require ($bindings.Status -ceq 'success' -and $bindings.GlobalCandidateUnchanged -eq $true -and $bindings.ScopedRuntimeSourcesUnchanged -eq $true) "Documents run $Run has changed candidate or scoped source."
    Require ($bindings.BeforeGlobalFingerprint -ceq $bindings.AfterGlobalFingerprint -and $bindings.BeforeGlobalFingerprint -ceq $report.Environment.SourceFingerprint) "Documents run $Run source fingerprints disagree."
    Require ([string]::Equals([string]$bindings.RawReportSha256, (Hash $path), [StringComparison]::OrdinalIgnoreCase)) "Documents run $Run raw report hash differs from its binding."
    Require (@($bindings.MeasuredAssembliesMatchedSnapshot).Count -gt 0) "Documents run $Run has no binary snapshot bindings."
    foreach ($assembly in $bindings.MeasuredAssembliesMatchedSnapshot) {
        Require ($assembly.MeasurementStartSha256 -ceq $assembly.SnapshotSha256) "Documents run $Run measured binary differs from its snapshot."
    }
    $maintenance = $sustained[0].Maintenance
    Require ($null -ne $maintenance -and $null -ne $maintenance.AfterIdleDrain) "Documents run $Run lacks complete maintenance evidence."
    $during = @($maintenance.DuringWrites)
    Require ($during.Count -ge [math]::Floor($Configuration.sustainedSeconds / 10)) "Documents run $Run has too few whole-database samples."
    $observations = @($maintenance.BeforeWrites, $maintenance.AfterWrites, $maintenance.AfterHotKeyAndVerification, $maintenance.AfterIdleDrain)
    $observations += @($during | ForEach-Object { $_.Observation })
    $observations += @($maintenance.DuringIdleDrain | ForEach-Object { $_.Observation })
    $peakDatabaseBytes = 0.0
    foreach ($observation in $observations) {
        $bytes = Number $observation.DatabaseBytes "Documents run $Run whole-database bytes"
        Require ($bytes -gt 0) "Documents run $Run has invalid whole-database bytes."
        $peakDatabaseBytes = [math]::Max($peakDatabaseBytes, $bytes)
    }
    AtMost $peakDatabaseBytes $Configuration.maximumWholeDatabaseBytes "Documents run $Run whole-database peak"
    $late = @($during | Where-Object { $_.ElapsedSeconds -ge $sustained[0].MeasuredSeconds / 2 })
    Require ($late.Count -ge 3 -and ($late[-1].ElapsedSeconds - $late[0].ElapsedSeconds) -ge $Configuration.sustainedSeconds / 3) "Documents run $Run lacks a whole-database late window."
    $lateGrowth = ((Number $maintenance.AfterWrites.DatabaseBytes 'Documents after-writes database bytes') -
        (Number $late[0].Observation.DatabaseBytes 'Documents late database bytes')) /
        (($sustained[0].MeasuredSeconds - $late[0].ElapsedSeconds) / 60.0)
    AtMost $lateGrowth $Configuration.maximumWholeDatabaseLateGrowthBytesPerMinute "Documents run $Run whole-database late growth bytes/min"
    & (Join-Path $PSScriptRoot 'verify-documents-capacity-report.ps1') `
        -ReportPath $path `
        -MinimumSustainedSeconds $Configuration.sustainedSeconds `
        -MaximumDocumentsRelationBytes $Configuration.maximumDocumentsRelationBytes `
        -MaximumLateGrowthBytesPerMinute $Configuration.maximumLateGrowthBytesPerMinute `
        -MaximumWalBytesPerTransition $Configuration.maximumWalBytesPerTransition `
        -MinimumTransitionsPerSecond $Configuration.minimumTransitionsPerSecond `
        -MaximumSaveP99Milliseconds $Configuration.maximumSaveP99Milliseconds `
        -ExpectedSourceFingerprint $bindings.BeforeGlobalFingerprint
}
function VerifyEvidence($Source) {
    Require ($budget.repetitions -ge 2) 'The gate requires repeat campaigns.'
    $jobFingerprints = @()
    for ($run = 1; $run -le $budget.repetitions; $run++) {
        $jobFingerprints += VerifyJobs (Join-Path $evidence "run-$run/jobs-workflows") $budget.jobsWorkflows $run
        VerifyProjections (Join-Path $evidence "run-$run/projections") $budget.projections $run $Source
        VerifyDocuments (Join-Path $evidence "run-$run/documents") $budget.documents $run
    }
    Require (@($jobFingerprints | Sort-Object -Unique).Count -eq 1) 'Jobs/Workflows repeats used different source fingerprints.'
}

Push-Location -LiteralPath $repository
try {
    Require ($ExpectedCommit -match '^[0-9a-fA-F]{40}$') 'ExpectedCommit must be a full 40-character SHA.'
    $head = (& git rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and [string]::Equals($head, $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) 'The checkout is not the requested exact commit.'
    Require (@(& git status --porcelain --untracked-files=normal).Count -eq 0) 'The checkout must be clean before qualification.'
    Require ($budget.schemaVersion -eq 1 -and $budget.repetitions -eq 2) 'Unsupported ecosystem performance budget contract.'
    $processor = [string](Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
    Require ($processor.Contains([string]$budget.referenceProcessor, [StringComparison]::OrdinalIgnoreCase)) 'This is not the specified ecosystem performance reference processor.'
    foreach ($seconds in @($budget.jobsWorkflows.secondsPerProduct, $budget.projections.secondsPerRun, $budget.documents.sustainedSeconds)) {
        Require ($seconds -ge 1800) 'Qualification requires at least 30 minutes per sustained product/run.'
    }
    if ($Mode -eq 'Preflight') { Write-Output "Clean exact candidate $head verified."; return }
    Require ($evidence.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'EvidenceRoot must be inside the checkout.'
    if ($Mode -eq 'Run') {
        Require (-not (Test-Path -LiteralPath $evidence)) 'Choose a fresh evidence directory for the gate.'
        New-Item -ItemType Directory -Path $evidence -Force | Out-Null
        Copy-Item -LiteralPath $budgetPath -Destination (Join-Path $evidence 'budgets.json')
        [ordered]@{ Processor = $processor; LogicalProcessors = [Environment]::ProcessorCount;
            OperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription;
            DotNetSdk = (& dotnet --version); DockerServer = (& docker version --format '{{.Server.Version}}') } |
            ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidence 'runner.json') -Encoding utf8
        Require ($LASTEXITCODE -eq 0) 'The Docker server is unavailable on the reference runner.'
        CaptureSource (Join-Path $evidence 'source-before.json')
        $source = Json (Join-Path $evidence 'source-before.json')
        SnapshotBinaries (Join-Path $evidence 'binary-snapshot')
        for ($run = 1; $run -le $budget.repetitions; $run++) {
            $runRoot = Join-Path $evidence "run-$run"
            foreach ($name in @('jobs-workflows', 'projections', 'documents')) { New-Item -ItemType Directory -Path (Join-Path $runRoot $name) -Force | Out-Null }
            $jobs = Join-Path $runRoot 'jobs-workflows'
            InvokeCampaign "Jobs/Workflows run $run" {
                & ./eng/jobs-storage-campaign.ps1 -Version 15 -Seconds $budget.jobsWorkflows.secondsPerProduct -PayloadMode SeededHighEntropy -NoBuild -Output (Join-Path $jobs 'jobs-workflows.json')
            } (Join-Path $jobs 'campaign.log')
            $projections = Join-Path $runRoot 'projections'
            InvokeCampaign "Projections run $run" {
                & ./docs/projections/evidence/run-load.ps1 -Profile soak -Seconds $budget.projections.secondsPerRun -Repetitions 1 -NoBuild -OutputDirectory $projections
            } (Join-Path $projections 'campaign.log')
            $documents = Join-Path $runRoot 'documents'
            InvokeCampaign "Documents run $run" {
                & ./eng/run-documents-load.ps1 -CellSeconds $budget.documents.cellSeconds -SustainedSeconds $budget.documents.sustainedSeconds `
                    -MaximumDatabaseBytes $budget.documents.maximumDatabaseBytes -MinimumFilesystemAvailableBytes $budget.documents.minimumFilesystemAvailableBytes `
                    -IdleDrainSeconds $budget.documents.idleDrainSeconds -StorageMode $budget.documents.storageMode -NoBuild -OutputDirectory $documents
            } (Join-Path $documents 'campaign.log')
            VerifyLiveBinaries (Join-Path $evidence 'binary-snapshot')
        }
        CaptureSource (Join-Path $evidence 'source-after.json')
        VerifySource $source (Json (Join-Path $evidence 'source-after.json'))
        VerifyEvidence $source
        $files = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | ForEach-Object {
            [ordered]@{ Path = [IO.Path]::GetRelativePath($evidence, $_.FullName).Replace('\', '/'); Sha256 = (Hash $_.FullName) }
        } | Sort-Object Path)
        [ordered]@{ SchemaVersion = 1; CandidateSha = $head; SourceTreeSha256 = $source.sourceTreeSha256;
            BudgetSha256 = (Hash (Join-Path $evidence 'budgets.json')); Repetitions = $budget.repetitions;
            QualifiedLocalCapacity = $true; ProductionQualified = $false; Files = $files } |
            ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'manifest.json') -Encoding utf8
        Write-Output "Manual local ecosystem performance gate passed for $head. Production qualification remains false."
    } else {
        $manifest = Json (Join-Path $evidence 'manifest.json')
        Require ($manifest.SchemaVersion -eq 1 -and $manifest.CandidateSha -ceq $head -and $manifest.QualifiedLocalCapacity -eq $true -and $manifest.ProductionQualified -eq $false) 'Invalid or mismatched gate manifest.'
        Require ((Hash (Join-Path $evidence 'budgets.json')) -ceq $manifest.BudgetSha256) 'Budget snapshot hash differs from manifest.'
        Require ((Hash $budgetPath) -ceq $manifest.BudgetSha256) 'Candidate budget differs from evidence budget.'
        $runner = Json (Join-Path $evidence 'runner.json')
        Require ([string]$runner.Processor -match [regex]::Escape([string]$budget.referenceProcessor)) 'Archived evidence used a different reference processor.'
        $listed = @($manifest.Files)
        $actual = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $evidence 'manifest.json') })
        Require ($listed.Count -gt 0 -and $listed.Count -eq $actual.Count) 'Artifact count differs from the manifest.'
        $seen = @{}
        foreach ($entry in $listed) {
            Require ($entry.Path -match '^[^/\\]+(?:/[^/\\]+)*$' -and -not $entry.Path.Contains('..')) 'Manifest contains an unsafe path.'
            Require (-not $seen.ContainsKey([string]$entry.Path)) 'Manifest contains a duplicate path.'
            $seen[[string]$entry.Path] = $true
            $path = Join-Path $evidence $entry.Path
            Require (Test-Path -LiteralPath $path -PathType Leaf) "Manifest artifact is missing: $($entry.Path)"
            Require ([string]::Equals([string]$entry.Sha256, (Hash $path), [StringComparison]::OrdinalIgnoreCase)) "Manifest artifact hash changed: $($entry.Path)"
        }
        $source = Json (Join-Path $evidence 'source-before.json')
        VerifySource $source (Json (Join-Path $evidence 'source-after.json'))
        Require ($source.sourceTreeSha256 -ceq $manifest.SourceTreeSha256) 'Manifest source hash differs from the candidate capture.'
        VerifyArchivedBinaries (Join-Path $evidence 'binary-snapshot')
        VerifyEvidence $source
        Write-Output "Archived ecosystem performance evidence verified for $head."
    }
} finally { Pop-Location }
