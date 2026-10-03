[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Run', 'Verify')][string]$Mode = 'Verify',
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [string]$EvidenceRoot = 'artifacts/ecosystem-performance',
    # Documents, Projections and Workflows are the per-family release capacity scopes. 'All' is
    # the combined diagnostic campaign and 'Jobs' the Jobs-only gate (verify-jobs-release-capacity.ps1).
    [ValidateSet('All', 'Jobs', 'Documents', 'Projections', 'Workflows', IgnoreCase = $false)][string]$Product = 'All',
    # Archived re-verification only: the GitHub run that produced the evidence, so a Jobs fixture
    # owner is bound to that exact run rather than to the verifying run.
    [ValidateRange(1, [long]::MaxValue)][long]$ProducerRunId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ecosystem-capacity-scope.psm1') -Force
$scope = Get-EcosystemCapacityScope -Product $Product
$producerRun = if ($PSBoundParameters.ContainsKey('ProducerRunId')) { [string]$ProducerRunId } else { '' }
if (-not [string]::IsNullOrEmpty($producerRun) -and $Mode -ne 'Verify') {
    throw 'ProducerRunId applies only to archived Verify mode.'
}
$harnessProjects = [ordered]@{
    jobs = 'benchmarks/BlueTusk.Workflows.LoadHarness'
    projections = 'benchmarks/BlueTusk.Projections.LoadHarness'
    documents = 'benchmarks/BlueTusk.Documents.LoadHarness'
}
$runsJobsCampaign = $scope.Campaigns -contains 'jobs' -or $scope.Campaigns -contains 'jobs-workflows'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = [IO.Path]::GetFullPath((Join-Path $repository $EvidenceRoot))
$budgetFile = if ($Product -eq 'Jobs') {
    'jobs-release-capacity-budgets.json'
} else {
    'ecosystem-performance-budgets.json'
}
$budgetPath = Join-Path $PSScriptRoot $budgetFile
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
    $projects = [ordered]@{}
    foreach ($harness in $scope.Harnesses) { $projects[$harness] = $harnessProjects[$harness] }
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
    foreach ($entry in $records) {
        Require ([string]$entry.Product -cin $scope.Harnesses -and [string]$entry.Name -match '^[A-Za-z0-9_.-]+$' -and [string]$entry.Name -notin @('.', '..')) 'Binary snapshot entry is invalid.'
        $live = Join-Path $repository "$($harnessProjects[[string]$entry.Product])/bin/Release/net10.0/$($entry.Name)"
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
        Require ([string]$entry.Product -cin $scope.Harnesses) 'The archived binary product is unknown.'
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
function VerifyJobs($Root, $Configuration, $Run, [string]$ExpectedImage) {
    $jobsOnly = $Product -eq 'Jobs'
    $path = Join-Path $Root $(if ($jobsOnly) { 'jobs.json' } else { 'jobs-workflows.json' })
    $report = Json $path
    $environment = Json ($path + '.environment.json')
    $expectedProfile = if ($jobsOnly) { 'storage-jobs' } else { 'storage' }
    Require ($report.Profile -ceq $expectedProfile) "Jobs run $Run has the wrong profile."
    if ($jobsOnly) {
        Require ($environment.Container -ceq 'bluetusk-jobs-release-pg15' -and
            $environment.ProductScope -ceq 'Jobs' -and
            $environment.FixtureOwner -ceq 'jobs-release-capacity' -and
            -not [string]::IsNullOrWhiteSpace([string]$environment.FixtureRunId)) "Jobs run $Run did not use the owned product fixture."
        # Evidence recorded before local campaigns existed carries no kind and came from a workflow run.
        $kindProperty = $environment.PSObject.Properties['FixtureRunKind']
        $runKind = if ($null -eq $kindProperty) { 'github' } else { [string]$kindProperty.Value }
        Require (($runKind -ceq 'github' -and [string]$environment.FixtureRunId -match '^[0-9]+$') -or
            ($runKind -ceq 'local' -and [string]$environment.FixtureRunId -cmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')) "Jobs run $Run has an invalid fixture owner identity."
        if (-not [string]::IsNullOrEmpty($producerRun)) {
            # Readiness re-verifies archived evidence from another run: bind the fixture to that run.
            Require ($runKind -ceq 'github' -and $environment.FixtureRunId -ceq $producerRun) "Jobs run $Run used a fixture from another workflow run."
        } elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID)) {
            Require ($runKind -ceq 'github' -and $environment.FixtureRunId -ceq $env:GITHUB_RUN_ID) "Jobs run $Run used a fixture from another workflow run."
        } elseif (-not [string]::IsNullOrWhiteSpace($env:BLUETUSK_LOCAL_CAMPAIGN_ID)) {
            Require ($runKind -ceq 'local' -and $environment.FixtureRunId -ceq $env:BLUETUSK_LOCAL_CAMPAIGN_ID) "Jobs run $Run used a fixture from another local campaign."
        }
    }
    Require ($report.PayloadMode -ceq $Configuration.payloadMode -and $environment.PayloadMode -ceq $Configuration.payloadMode) "Jobs/Workflows run $Run did not attest the high-entropy payload."
    Require ([string]$report.PostgreSqlVersion -match '^PostgreSQL 15\.' -and [string]$environment.PostgreSql.server -match '^PostgreSQL 15\.') "Jobs/Workflows run $Run used the wrong PostgreSQL version."
    $expectedDigest = ($ExpectedImage -split '@')[-1]
    Require ($expectedDigest -match '^sha256:[0-9a-f]{64}$') 'Capacity budget lacks a pinned PostgreSQL image.'
    if ($jobsOnly) {
        Require ($environment.ImageReference -ceq $ExpectedImage -and
            [string]$environment.ImageId -match '^sha256:[0-9a-f]{64}$' -and
            @($environment.ImageRepoDigests | Where-Object {
                ([string]$_).EndsWith("@$expectedDigest", [StringComparison]::Ordinal)
            }).Count -gt 0) "Jobs run $Run used a different PostgreSQL image digest."
    } else {
        Require ([string]$environment.ImageId -ceq $expectedDigest) "Jobs/Workflows run $Run used a different PostgreSQL image digest."
    }
    Require ([string]$report.SourceSha256 -match '^[0-9a-fA-F]{64}$') "Jobs/Workflows run $Run has no source fingerprint."
    $expectedFaults = if ($jobsOnly) {
        @('process-death-after-effect-before-ack', 'ambiguous-commit-lost-server-ack', 'jobs-network-partition-and-heal')
    } else { @() }
    if ($jobsOnly) {
        Require (@($report.Cases).Count -eq 0 -and @($report.Faults).Count -eq 3 -and
            @(Compare-Object $expectedFaults @($report.Faults | ForEach-Object { [string]$_.Name })).Count -eq 0) "Jobs run $Run includes another product or lacks a Jobs recovery scenario."
    }
    Require (@($report.Faults).Count -eq $(if ($jobsOnly) { 3 } else { 5 }) -and
        @($report.Faults | Where-Object { $_.Passed -ne $true }).Count -eq 0) "Jobs run $Run failed recovery verification."
    Require (@($report.Overload).Count -eq $(if ($jobsOnly) { 1 } else { 2 })) "Jobs run $Run has an incomplete product result."
    $products = if ($jobsOnly) { @('Jobs') } else { @('Jobs', 'Workflows') }
    foreach ($product in $products) {
        $entries = @($report.Overload | Where-Object { $_.Product -ceq $product })
        Require ($entries.Count -eq 1) "Jobs/Workflows run $Run has missing or duplicate $product results."
        $item = $entries[0]
        Require ($item.PayloadMode -ceq $Configuration.payloadMode -and $item.Verified -eq $true) "$product run $Run did not verify the required payload mode and invariants."
        AtLeast $item.OfferedDurationSeconds $Configuration.secondsPerProduct "$product run $Run offered seconds"
        if ($jobsOnly) {
            AtLeast $item.AdmissionAndDrainSeconds $Configuration.secondsPerProduct "Jobs run $Run measured seconds"
        }
        Require ($item.Accepted -gt 0 -and $item.DurableEffects -eq $item.Accepted -and $item.PrunedPrimaryRows -eq $item.Accepted) "$product run $Run has incomplete durable effects or pruning."
        if ($jobsOnly) {
            Require ($item.PrunedJobs -eq $item.Accepted -and $item.Rejected -gt 0 -and
                $item.MaximumOutstanding -le 128 -and $item.MaximumAccepted -eq 200000 -and
                $item.AdmissionSlotsPerTenant -eq 32 -and $item.ConcurrencyPerTenant -eq 4 -and
                $item.HandlerDelayMilliseconds -eq 100) "Jobs run $Run lacks bounded overload or retention evidence."
        }
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
        if ($jobsOnly) {
            $physicalSamples = @($samples | Where-Object { $null -ne $_.Physical })
            Require ($physicalSamples.Count -ge
                [math]::Floor($Configuration.secondsPerProduct / 5)) "Jobs run $Run lacks physical storage observations."
            Require ($physicalSamples[0].Physical.WalPosition -gt 0 -and
                $physicalSamples[-1].Physical.WalPosition -gt $physicalSamples[0].Physical.WalPosition -and
                @($physicalSamples[-1].Physical.Relations).Count -gt 0) "Jobs run $Run lacks physical WAL or relation observations."
            Require ($samples[-1].RetainedPrimaryRows -eq 0 -and
                $samples[-1].RetainedJobs -eq 0 -and
                $samples[-1].JobAttemptRows -eq 0) "Jobs run $Run did not fully prune retained rows."
        }
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
function VerifyProjections($Root, $Configuration, $Run, $Source, [string]$Profile) {
    Require ($Profile -in @('capacity', 'soak')) "Projections run $Run has an unknown profile."
    $limits = if ($Profile -eq 'capacity') { $Configuration.capacity } else { $Configuration.overload }
    $scenarioName = if ($Profile -eq 'capacity') { 'sustainable-capacity' } else { 'overload-fairness' }
    $metadata = Json (Join-Path $Root 'campaign.json')
    Require ($metadata.Profile -ceq $Profile -and $metadata.Seconds -eq $Configuration.secondsPerRun -and $metadata.Repetitions -eq 1) "Projections $Profile run $Run has the wrong campaign configuration."
    Require ($metadata.ImageDigest -ceq $budget.postgreSql18Image) "Projections run $Run used the wrong PostgreSQL image."
    Require ($metadata.Passed -eq $true -and $metadata.CandidateUnchanged -eq $true) "Projections run $Run failed or changed candidate."
    VerifySource (Json (Join-Path $Root 'source-before.json')) (Json (Join-Path $Root 'source-after.json'))
    $before = Json (Join-Path $Root 'source-before.json')
    Require ($before.sourceTreeSha256 -ceq $Source.sourceTreeSha256) "Projections run $Run is not bound to the gate candidate."
    $report = Json (Join-Path $Root 'run-1.json')
    Require ($report.Profile -ceq $Profile -and $report.Passed -eq $true -and @($report.Scenarios).Count -eq 1) "Projections $Profile run $Run is incomplete."
    $item = $report.Scenarios[0]
    Require ($item.Configuration.Name -ceq $scenarioName -and $item.Configuration.Seconds -eq $Configuration.secondsPerRun -and $item.ExactStateVerified -eq $true) "Projections $Profile run $Run lacks exact-state verification."
    Require ([string]$item.PostgreSql -match '^18\.') "Projections run $Run used the wrong PostgreSQL version."
    AtLeast $item.MeasuredSeconds ($Configuration.secondsPerRun - 1) "Projections run $Run measured seconds"
    # One published WAL transaction may carry several committed operations.
    Require ($item.Committed -gt 0 -and $item.WalTransactions -gt 0 -and
        $item.StandbyFeedbackUpdates -eq $item.WalTransactions -and
        $item.InboxEffects -ge $item.Committed) "Projections run $Run has missing durable WAL feedback or inbox effects."
    # The historical report field is named TransactionsPerSecond but counts committed operations,
    # including batched backlog operations, over the whole pipeline and subscriber drain.
    AtLeast $item.TransactionsPerSecond $limits.minimumCommittedOperationsPerSecond "Projections $Profile run $Run committed operations/s"
    foreach ($name in @('Commit', 'Projection', 'Inbox')) {
        $latency = $item.$name
        Require ($latency.Samples -gt 0) "Projections $Profile run $Run has no $name latency samples."
        Positive $latency.P99Milliseconds "Projections $Profile run $Run $name p99"
    }
    AtMost $item.Commit.P99Milliseconds $limits.maximumCommitP99Milliseconds "Projections $Profile run $Run commit p99"
    AtMost $item.DrainSeconds $limits.maximumDrainSeconds "Projections $Profile run $Run drain seconds"
    $offer = $item.OfferWindow
    Require ($null -ne $offer -and $offer.Offered -gt 0 -and $offer.Accepted -gt 0 -and
        $offer.Accepted + $offer.Rejected -eq $offer.Offered -and $offer.Inbox -le $offer.Accepted -and
        $offer.PendingInboxAtEnd -eq $offer.Accepted - $offer.Inbox) "Projections $Profile run $Run has inconsistent offer-window evidence."
    if ($Profile -eq 'capacity') {
        Require ($item.Configuration.Backlog -eq 0 -and $item.Configuration.OfferedPerSecond -eq 20 -and
            $item.Configuration.PayloadBytes -eq 4096 -and $item.Configuration.Tenants -eq 32 -and
            $item.Configuration.Fanout -eq 64) "Projections capacity run $Run has the wrong steady workload."
        AtLeast ($offer.Offered / $item.MeasuredSeconds) $limits.minimumOfferedOperationsPerSecond "Projections capacity run $Run offered operations/s"
        AtLeast ($offer.Accepted / $offer.Offered) $limits.minimumAdmissionRatio "Projections capacity run $Run admission ratio"
        AtLeast $offer.AcceptedPerSecond $limits.minimumAcceptedOperationsPerSecond "Projections capacity run $Run accepted operations/s"
        AtLeast $offer.InboxPerSecond $limits.minimumInboxOperationsPerSecondDuringOffer "Projections capacity run $Run inbox operations/s during offer"
        AtLeast ($offer.Inbox / $offer.Accepted) $limits.minimumInboxCoverageDuringOffer "Projections capacity run $Run inbox coverage during offer"
        AtMost $item.Projection.P99Milliseconds $limits.maximumProjectionP99Milliseconds "Projections capacity run $Run projection p99"
        AtMost $item.Inbox.P99Milliseconds $limits.maximumInboxP99Milliseconds "Projections capacity run $Run inbox p99"
        Require ($item.LiveCoverage.Samples -gt 0) "Projections capacity run $Run has no Live coverage latency samples."
        Positive $item.LiveCoverage.P99Milliseconds "Projections capacity run $Run Live coverage p99"
        AtMost $item.LiveCoverage.P99Milliseconds $limits.maximumLiveCoverageP99Milliseconds "Projections capacity run $Run Live coverage p99"
    } else {
        Require ($item.Configuration.Backlog -eq 10000 -and $item.Configuration.OfferedPerSecond -eq 1500 -and
            $item.Configuration.PayloadBytes -eq 4096 -and $item.Configuration.Tenants -eq 32 -and
            $item.Configuration.Fanout -eq 64 -and $item.Rejected -gt 0 -and
            # The harness counter includes an item handed to a writer until that writer decrements it.
            # The bounded channel itself holds at most QueueCapacity; at most Writers handoffs can overlap.
            $item.PeakQueued -le ($item.Configuration.QueueCapacity + $item.Configuration.Writers)) "Projections overload run $Run lacks bounded admission and rejection."
    }
    Positive $item.Runtime.MaximumOwnedStorageBytes "Projections run $Run owned storage peak"
    AtMost $item.Runtime.MaximumOwnedStorageBytes $limits.maximumOwnedStorageBytes "Projections $Profile run $Run owned storage peak"
    AtMost $item.Runtime.MaximumRetainedSlotBytes $limits.maximumRetainedSlotBytes "Projections $Profile run $Run retained slot peak"
    $walDelta = (Number $item.After.WalBytes 'Projections after WAL bytes') - (Number $item.Before.WalBytes 'Projections before WAL bytes')
    Require ($walDelta -ge 0) "Projections run $Run WAL counter regressed."
    AtMost ($walDelta / $item.Committed) $limits.maximumWalBytesPerCommittedOperation "Projections $Profile run $Run WAL bytes/committed operation"
    $windows = @($item.ServiceWindows)
    Require ($windows.Count -ge [math]::Floor($Configuration.secondsPerRun / 15) -and $windows[-1].ElapsedSeconds -ge $Configuration.secondsPerRun - 20) "Projections run $Run lacks continuous service-window evidence."
    $physical = @($windows | Where-Object { $_.OwnedStorageBytes -gt 0 -and $_.PhysicalSampleAgeSeconds -ne $null -and $_.PhysicalSampleAgeSeconds -le 30 })
    Require ($physical.Count -ge [math]::Floor($Configuration.secondsPerRun / 15) -and $physical[-1].ElapsedSeconds -ge $Configuration.secondsPerRun - 20) "Projections $Profile run $Run lacks current physical samples."
    AtMost (($physical | Measure-Object -Property OwnedStorageBytes -Maximum).Maximum) $limits.maximumOwnedStorageBytes "Projections $Profile run $Run sampled owned storage"
    AtMost (($physical | Measure-Object -Property RetainedSlotBytes -Maximum).Maximum) $limits.maximumRetainedSlotBytes "Projections $Profile run $Run sampled retained slot"
    if ($Profile -eq 'soak') {
        $middle = @($windows | Where-Object { $_.ElapsedSeconds -ge $Configuration.secondsPerRun / 2 })[0]
        for ($tenant = 0; $tenant -lt $item.Configuration.Tenants; $tenant++) {
            Require ($windows[-1].Delivered[$tenant] -gt $middle.Delivered[$tenant]) "Projections overload run $Run starved tenant $tenant in the final half."
        }
    }
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
        if ($runsJobsCampaign) {
            $jobsDirectory = if ($Product -eq 'Jobs') { "run-$run/jobs" } else { "run-$run/jobs-workflows" }
            $jobFingerprints += VerifyJobs (Join-Path $evidence $jobsDirectory) $budget.jobsWorkflows $run $budget.postgreSql15Image
        }
        if ($scope.Campaigns -contains 'projections-capacity') {
            VerifyProjections (Join-Path $evidence "run-$run/projections-capacity") $budget.projections $run $Source 'capacity'
        }
        if ($scope.Campaigns -contains 'projections-overload') {
            VerifyProjections (Join-Path $evidence "run-$run/projections-overload") $budget.projections $run $Source 'soak'
        }
        if ($scope.Campaigns -contains 'documents') {
            VerifyDocuments (Join-Path $evidence "run-$run/documents") $budget.documents $run
        }
    }
    if ($runsJobsCampaign) {
        Require (@($jobFingerprints | Sort-Object -Unique).Count -eq 1) 'Jobs/Workflows repeats used different source fingerprints.'
    }
}

Push-Location -LiteralPath $repository
try {
    Require ($ExpectedCommit -match '^[0-9a-fA-F]{40}$') 'ExpectedCommit must be a full 40-character SHA.'
    $head = (& git rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and [string]::Equals($head, $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) 'The checkout is not the requested exact commit.'
    Require (@(& git status --porcelain --untracked-files=normal).Count -eq 0) 'The checkout must be clean before qualification.'
    $expectedQualification = if ($Product -eq 'Jobs') {
        'manual-exact-candidate-jobs-capacity'
    } else {
        'manual-exact-candidate-local-capacity'
    }
    Require ($budget.schemaVersion -eq 1 -and $budget.repetitions -eq 2 -and
        [string]$budget.qualification -ceq $expectedQualification) 'Unsupported capacity budget contract.'
    $processor = [string](Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
    Require ($processor.Contains([string]$budget.referenceProcessor, [StringComparison]::OrdinalIgnoreCase)) 'This is not the specified ecosystem performance reference processor.'
    $durations = @()
    if ($runsJobsCampaign) { $durations += $budget.jobsWorkflows.secondsPerProduct }
    if ($scope.Campaigns -contains 'projections-capacity') { $durations += $budget.projections.secondsPerRun }
    if ($scope.Campaigns -contains 'documents') { $durations += $budget.documents.sustainedSeconds }
    Require ($durations.Count -gt 0) 'The capacity scope selects no sustained campaign.'
    foreach ($seconds in $durations) {
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
            foreach ($name in $scope.Campaigns) { New-Item -ItemType Directory -Path (Join-Path $runRoot $name) -Force | Out-Null }
            if ($runsJobsCampaign) {
                $jobsDirectory = if ($Product -eq 'Jobs') { 'jobs' } else { 'jobs-workflows' }
                $jobs = Join-Path $runRoot $jobsDirectory
                $jobsOutput = if ($Product -eq 'Jobs') { 'jobs.json' } else { 'jobs-workflows.json' }
                InvokeCampaign "$Product Jobs run $run" {
                    if ($Product -eq 'Jobs') {
                        & ./eng/jobs-storage-campaign.ps1 -Version 15 -Seconds $budget.jobsWorkflows.secondsPerProduct -PayloadMode SeededHighEntropy -Product Jobs -FixtureName bluetusk-jobs-release-pg15 -NoBuild -Output (Join-Path $jobs $jobsOutput)
                    } else {
                        & ./eng/jobs-storage-campaign.ps1 -Version 15 -Seconds $budget.jobsWorkflows.secondsPerProduct -PayloadMode SeededHighEntropy -NoBuild -Output (Join-Path $jobs $jobsOutput)
                    }
                } (Join-Path $jobs 'campaign.log')
                [void](VerifyJobs $jobs $budget.jobsWorkflows $run $budget.postgreSql15Image)
                VerifyLiveBinaries (Join-Path $evidence 'binary-snapshot')
            }
            if ($scope.Campaigns -contains 'projections-capacity') {
                foreach ($profile in @('capacity', 'soak')) {
                    $name = if ($profile -eq 'capacity') { 'projections-capacity' } else { 'projections-overload' }
                    $projections = Join-Path $runRoot $name
                    InvokeCampaign "Projections $profile run $run" {
                        & ./docs/projections/evidence/run-load.ps1 -Profile $profile -Seconds $budget.projections.secondsPerRun -Repetitions 1 -NoBuild -OutputDirectory $projections
                    } (Join-Path $projections 'campaign.log')
                    VerifyProjections $projections $budget.projections $run $source $profile
                    VerifyLiveBinaries (Join-Path $evidence 'binary-snapshot')
                }
            }
            if ($scope.Campaigns -contains 'documents') {
                $documents = Join-Path $runRoot 'documents'
                InvokeCampaign "Documents run $run" {
                    & ./eng/run-documents-load.ps1 -CellSeconds $budget.documents.cellSeconds -SustainedSeconds $budget.documents.sustainedSeconds `
                        -MaximumDatabaseBytes $budget.documents.maximumDatabaseBytes -MinimumFilesystemAvailableBytes $budget.documents.minimumFilesystemAvailableBytes `
                        -IdleDrainSeconds $budget.documents.idleDrainSeconds -StorageMode $budget.documents.storageMode -NoBuild -OutputDirectory $documents
                } (Join-Path $documents 'campaign.log')
                VerifyDocuments $documents $budget.documents $run
                VerifyLiveBinaries (Join-Path $evidence 'binary-snapshot')
            }
        }
        CaptureSource (Join-Path $evidence 'source-after.json')
        VerifySource $source (Json (Join-Path $evidence 'source-after.json'))
        VerifyEvidence $source
        $files = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | ForEach-Object {
            [ordered]@{ Path = [IO.Path]::GetRelativePath($evidence, $_.FullName).Replace('\', '/'); Sha256 = (Hash $_.FullName) }
        } | Sort-Object Path)
        [ordered]@{ SchemaVersion = 1; Family = $Product; CandidateSha = $head; SourceTreeSha256 = $source.sourceTreeSha256;
            BudgetSha256 = (Hash (Join-Path $evidence 'budgets.json')); Repetitions = $budget.repetitions;
            QualifiedLocalCapacity = $true; ProductionQualified = $false; Files = $files } |
            ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'manifest.json') -Encoding utf8
        Write-Output "Manual local $Product capacity gate passed for $head. Production qualification remains false."
    } else {
        $manifest = Json (Join-Path $evidence 'manifest.json')
        Require ($manifest.SchemaVersion -eq 1 -and $manifest.CandidateSha -ceq $head -and $manifest.QualifiedLocalCapacity -eq $true -and $manifest.ProductionQualified -eq $false) 'Invalid or mismatched gate manifest.'
        # Exact declared family and layout: a combined 'All' campaign, or another family's
        # campaigns relabelled, can never verify as a single family's capacity evidence.
        Assert-EcosystemCapacityEvidenceScope -EvidenceRoot $evidence -Product $Product -Repetitions $budget.repetitions
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
        Write-Output "Archived $Product capacity evidence verified for $head."
    }
} finally { Pop-Location }
