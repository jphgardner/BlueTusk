[CmdletBinding()]
param()

# Offline self-test for the Events, Schema, Sql and Studio release capacity gates. It checks the
# workflow, budget and readiness-mapping source contracts, then builds synthetic evidence
# directories and proves that the archived-evidence verifier accepts a complete campaign and
# rejects every substitution. Synthetic evidence is never capacity evidence.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'expansion-capacity-evidence.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'expansion-candidate-evidence.psm1') -Force
$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'expansion-release-policy.json') -Raw | ConvertFrom-Json
$verifier = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'verify-expansion-capacity.ps1') -Raw
$module = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'expansion-capacity-evidence.psm1') -Raw
$build = Get-Content -LiteralPath (Join-Path $root '.github/workflows/build.yml') -Raw
$solution = Get-Content -LiteralPath (Join-Path $root 'BlueTusk.slnx') -Raw
$host18 = 'postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873'
$commit = 'a' * 40
$rejected = 0

function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Hash([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-Rejected([string] $Name, [scriptblock] $Action, [string] $Guard)
{
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($Guard))
    {
        throw "Capacity case '$Name' did not fail at '$Guard': '$failure'."
    }
    $script:rejected++
}

# Committed ceilings. A contract may tighten these, never weaken them silently.
$pinned = @{
    Events = @{
        Operations = @{ append = @(38, 500, 0); read = @(38, 500, 0); replay = @(76, 1000, 0) }
        Series = @{ delivery = 2000 }; Metrics = @{ replayDrainSeconds = 60 }
        Storage = @{ maximumOwnedRelationBytes = 2147483648; maximumDatabaseBytes = 8589934592
            maximumOwnedGrowthBytesPerUnit = 4096; maximumWalBytesPerUnit = 16384 }
    }
    Schema = @{
        Operations = @{ 'capture-relations' = @(15.2, 1000, 0.02); 'capture-catalog' = @(1.9, 5000, 0.02); 'application-write' = @(76, 500, 0) }
        Series = @{}; Metrics = @{}
        Storage = @{ maximumOwnedRelationBytes = 1073741824; maximumDatabaseBytes = 8589934592; maximumLateOwnedGrowthBytesPerMinute = 16777216 }
    }
    Sql = @{
        Operations = @{ 'point-lookup' = @(475, 500, 0); 'range-page' = @(95, 500, 0); aggregate = @(9.5, 1000, 0); 'application-write' = @(152, 500, 0) }
        Series = @{}; Metrics = @{}
        Storage = @{ maximumOwnedRelationBytes = 1073741824; maximumDatabaseBytes = 8589934592; maximumLateOwnedGrowthBytesPerMinute = 16777216 }
    }
    Studio = @{
        Operations = @{ query = @(38, 1000, 0); explain = @(1.9, 1000, 0); schema = @(0.95, 2000, 0); 'event-page' = @(9.5, 1000, 0) }
        Series = @{}; Metrics = @{}
        Storage = @{ maximumOwnedRelationBytes = 1073741824; maximumDatabaseBytes = 8589934592
            maximumOwnedGrowthBytesPerUnit = 1024; maximumWalBytesPerUnit = 16384 }
    }
}

function Assert-PinnedCeilings([object] $Budget, [string] $Family)
{
    $limits = $pinned[$Family]
    Require ($Budget.repetitions -ge 2 -and $Budget.secondsPerRun -ge 1800 -and [string]$Budget.postgreSqlImage -ceq $host18) (
        "The $Family capacity budget cannot silently weaken its duration, repetitions or pinned PostgreSQL 18 image.")
    Require (@($Budget.operations.PSObject.Properties).Count -eq $limits.Operations.Count) "The $Family capacity budget cannot silently drop an operation."
    foreach ($name in $limits.Operations.Keys)
    {
        $operation = $Budget.operations.PSObject.Properties[$name]
        Require ($null -ne $operation) "The $Family capacity budget cannot silently drop '$name'."
        $value = $operation.Value
        Require ($value.minimumAcceptedPerSecond -ge $limits.Operations[$name][0] -and
            $value.maximumP99Milliseconds -le $limits.Operations[$name][1] -and
            $value.maximumRejectedFraction -le $limits.Operations[$name][2] -and
            $value.maximumScheduleSkippedFraction -le 0.05 -and $value.maximumErrors -eq 0) (
            "The $Family '$name' budget cannot silently weaken its product limits.")
    }
    foreach ($name in $limits.Series.Keys)
    {
        Require ($null -ne $Budget.series.PSObject.Properties[$name] -and
            $Budget.series.$name.maximumP99Milliseconds -le $limits.Series[$name]) "The $Family '$name' series cannot silently weaken."
    }
    foreach ($name in $limits.Metrics.Keys)
    {
        Require ($null -ne $Budget.metrics.PSObject.Properties[$name] -and
            $Budget.metrics.$name.maximum -le $limits.Metrics[$name]) "The $Family '$name' metric cannot silently weaken."
    }
    foreach ($name in $limits.Storage.Keys)
    {
        Require ($null -ne $Budget.storage.PSObject.Properties[$name] -and
            $Budget.storage.$name -le $limits.Storage[$name]) "The $Family storage budget cannot silently weaken '$name'."
    }
}

# 1. Source contracts.
Require ($build.Contains('./eng/test-expansion-capacity-contract.ps1')) 'build.yml must run the expansion capacity self-test.'
Require ($verifier.Contains("[ValidateSet('Events', 'Schema', 'Sql', 'Studio', IgnoreCase = `$false)]") -and
    $verifier.Contains('ProducerRunId applies only to archived Verify mode.') -and
    $verifier.Contains('[Security.Cryptography.RandomNumberGenerator]::GetBytes(24)') -and
    [regex]::Matches($verifier, 'POSTGRES_PASSWORD=').Count -eq 1 -and
    $verifier.Contains('"bluetusk.owner=$owner"') -and $verifier.Contains('"bluetusk.run=$runId"') -and
    $verifier.Contains('"bluetusk.family=$Family"') -and $verifier.Contains('Refusing to remove a $Kind outside this $Family campaign')) (
    'The capacity runner must use a fresh fixture password and remove only its own labelled fixture.')
Require ($module.Contains('Diagnostic capacity evidence can never satisfy a release capacity role.') -and
    $module.Contains("Assert-ExpansionCapacitySamples -Path `$samplesPath -Report `$report -Label `$label")) (
    'The capacity verifier must reject diagnostic evidence and recompute summaries from raw samples.')
foreach ($family in Get-ExpansionCapacityFamilies)
{
    $definition = Get-ExpansionCapacityDefinition -Family $family
    Require ([string]$policy.families.$family.capacityWorkflow -ceq $definition.WorkflowFile) "$family capacity must use the policy workflow name."
    $workflow = Get-Content -LiteralPath (Join-Path $root ".github/workflows/$($definition.WorkflowFile)") -Raw
    Require ($workflow -match '(?m)^  workflow_dispatch:\s*$' -and $workflow -notmatch '(?m)^  (?:push|pull_request|schedule):\s*$') "$family capacity must be manual-only."
    Require ($workflow.Contains('ref: ${{ inputs.candidate_sha }}') -and $workflow.Contains('runs-on: [self-hosted, windows, x64, bluetusk-benchmark]') -and
        $workflow.Contains('group: bluetusk-reference-host') -and $workflow.Contains("'$($definition.Confirmation)'") -and
        $workflow.Contains('$env:GITHUB_SHA -cne $env:CANDIDATE_SHA')) "$family capacity must bind the exact candidate on the reference runner."
    foreach ($mode in @('Preflight', 'Run', 'Verify'))
    {
        Require ($workflow.Contains("./eng/verify-expansion-capacity.ps1 -Mode $mode -Family $family -ExpectedCommit `$env:CANDIDATE_SHA -EvidenceRoot `$env:EVIDENCE_ROOT")) (
            "$family capacity omits its family-scoped $mode step.")
    }
    Require ($workflow.Contains("EVIDENCE_ROOT: artifacts/$($definition.Slug)-release-capacity/") -and
        $workflow.Contains("name: expansion-$($definition.Slug)-capacity-`${{ inputs.candidate_sha }}") -and
        [regex]::Matches($workflow, 'name: expansion-').Count -eq 1 -and
        $workflow.Contains("name: $($definition.Slug)-capacity-partial-") -and $workflow.Contains('retention-days: 90') -and
        $workflow.Contains("label=bluetusk.owner=$($definition.FixtureOwner)")) "$family capacity must publish exactly one retained family artifact and clean only its fixtures."
    $readiness = Get-ExpansionReadinessContract -Family $family -Commit $commit
    Require ((Get-ExpansionCapacityArtifactName -Family $family -Commit $commit) -ceq
        @($readiness.Roles | Where-Object Role -ceq 'capacity')[0].ArtifactName) "$family capacity artifact must use the readiness binding name."
    $role = Get-ExpansionRoleVerifier -Family $family -Role capacity
    Require ($role.Script -ceq 'verify-expansion-capacity.ps1' -and $role.EvidenceParameter -ceq 'EvidenceRoot' -and
        $role.Arguments.Mode -ceq 'Verify' -and $role.Arguments.Family -ceq $family -and $role.ProducerRunParameter -ceq 'ProducerRunId') (
        "$family capacity must map readiness to the archived Verify mode bound to its producing run.")
    $project = Join-Path $root $definition.HarnessProject
    Require ((Test-Path -LiteralPath $project -PathType Leaf) -and
        (Get-Content -LiteralPath $project -Raw).Contains("<BlueTuskProductFamily>$family</BlueTuskProductFamily>") -and
        $solution.Contains("Path=`"$($definition.HarnessProject)`"")) "$family capacity harness must be a registered $family project."
    $budget = Get-Content -LiteralPath (Join-Path $PSScriptRoot $definition.BudgetFile) -Raw | ConvertFrom-Json
    Assert-ExpansionCapacityBudget -Budget $budget -Family $family
    Assert-PinnedCeilings $budget $family
    foreach ($name in @('scope', 'workload', 'durationAndRepeats', 'offeredRates') + @($budget.operations.PSObject.Properties |
        ForEach-Object { "$($_.Name).maximumP99Milliseconds" }))
    {
        Require ($null -ne $budget.rationale.PSObject.Properties[$name] -and ([string]$budget.rationale.$name).Length -ge 40) (
            "The $family capacity contract must record its '$name' rationale.")
    }
    $weakened = $budget | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $first = @($weakened.operations.PSObject.Properties)[0].Value
    $first.maximumP99Milliseconds = $first.maximumP99Milliseconds * 2
    Assert-Rejected "$family-weakened-p99" { Assert-PinnedCeilings $weakened $family } 'cannot silently weaken'
    $shortened = $budget | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $shortened.secondsPerRun = 600
    Assert-Rejected "$family-shortened" { Assert-ExpansionCapacityBudget -Budget $shortened -Family $family } 'at least two 30-minute runs'
    $unpinned = $budget | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $unpinned.postgreSqlImage = 'postgres:18-alpine'
    Assert-Rejected "$family-unpinned-image" { Assert-ExpansionCapacityBudget -Budget $unpinned -Family $family } 'digest PostgreSQL image'
}
Assert-Rejected 'unknown-family' { Get-ExpansionCapacityDefinition -Family events } 'Unknown expansion capacity family'
Assert-Rejected 'diagnostic-in-verify' {
    & (Join-Path $PSScriptRoot 'verify-expansion-capacity.ps1') -Mode Verify -Family Events -ExpectedCommit $commit -DiagnosticSeconds 60 } 'apply only to Run mode'
Assert-Rejected 'producer-in-run' {
    & (Join-Path $PSScriptRoot 'verify-expansion-capacity.ps1') -Mode Run -Family Events -ExpectedCommit $commit -ProducerRunId 7 } 'ProducerRunId applies only'

# 2. Synthetic evidence and rejected substitutions.
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-expansion-capacity-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

function Write-Json([object] $Value, [string] $Path)
{
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function New-SyntheticBudget([string] $Family)
{
    # The real contract with a sparse schedule, so a full-length campaign stays small offline.
    $definition = Get-ExpansionCapacityDefinition -Family $Family
    $budget = Get-Content -LiteralPath (Join-Path $PSScriptRoot $definition.BudgetFile) -Raw | ConvertFrom-Json
    foreach ($operation in @($budget.operations.PSObject.Properties))
    {
        $operation.Value.workers = 1
        $operation.Value.offerIntervalMilliseconds = 60000
        $operation.Value.minimumAcceptedPerSecond = 0.01
    }
    foreach ($counter in @($budget.counters.PSObject.Properties))
    {
        if ($null -ne $counter.Value.PSObject.Properties['minimumPerSecond']) { $counter.Value.minimumPerSecond = 0.01 }
    }
    $path = Join-Path $temporaryRoot "$($definition.Slug)-synthetic-budget.json"
    Write-Json $budget $path
    return $path
}

function Update-SyntheticManifest([string] $Root)
{
    $manifest = Get-Content -LiteralPath (Join-Path $Root 'manifest.json') -Raw | ConvertFrom-Json
    $manifest.Files = @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        -not ($_.DirectoryName -eq $Root -and $_.Name -cin @('manifest.json', 'diagnostic-evaluation.json')) } | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/'); Sha256 = (Hash $_.FullName) } } | Sort-Object { $_.Path })
    Write-Json $manifest (Join-Path $Root 'manifest.json')
}

function Write-SyntheticSamples([string] $Path, [object] $Report, [string] $Text)
{
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
    $Report.RawSamples.Sha256 = Hash $Path
    $Report.RawSamples.Rows = ($Text.Split("`n") | Where-Object { $_.Length -gt 0 }).Count - 1
}

function New-SyntheticEvidence
{
    param([string] $Name, [string] $Family, [string] $BudgetPath, [switch] $Diagnostic, [long] $LatencyMicroseconds = 2000,
        [int] $Errors = 0, [int] $Skipped = 0, [long] $OwnedGrowthBytes = 10000)
    # Synthetic layout only: placeholder values exercise the verifier and are never evidence.
    $definition = Get-ExpansionCapacityDefinition -Family $Family
    $budget = Get-Content -LiteralPath $BudgetPath -Raw | ConvertFrom-Json
    $root = Join-Path $temporaryRoot $Name
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $seconds = if ($Diagnostic) { 600 } else { [int]$budget.secondsPerRun }
    $repetitions = if ($Diagnostic) { 1 } else { [int]$budget.repetitions }
    $source = [ordered]@{ formatVersion = 1; commit = $commit; dirty = $false; sourceTreeSha256 = ('b' * 64); productionQualified = $false }
    Copy-Item -LiteralPath $BudgetPath -Destination (Join-Path $root 'budgets.json')
    Write-Json $source (Join-Path $root 'source-before.json')
    Write-Json $source (Join-Path $root 'source-after.json')
    Write-Json ([ordered]@{ Family = $Family; Processor = 'AMD Ryzen 7 5800X 8-Core Processor'; Fixture = 'synthetic self-test; NOT capacity evidence' }) (Join-Path $root 'runner.json')
    $snapshot = Join-Path $root 'binary-snapshot'
    [IO.Directory]::CreateDirectory($snapshot) | Out-Null
    $harness = Join-Path $snapshot "$($definition.Harness).dll"
    [IO.File]::WriteAllText($harness, "synthetic $Family harness", [Text.UTF8Encoding]::new($false))
    Write-Json ([ordered]@{ Harness = $definition.Harness; Files = @([ordered]@{ Name = "$($definition.Harness).dll"; Sha256 = (Hash $harness) }) }) (Join-Path $snapshot 'binaries.json')
    $owner = $definition.FixtureOwner
    $runId = '4242'
    $digest = ([string]$budget.postgreSqlImage -split '@')[-1]
    for ($run = 1; $run -le $repetitions; $run++)
    {
        $runRoot = Join-Path $root "run-$run"
        [IO.Directory]::CreateDirectory($runRoot) | Out-Null
        Write-Json $source (Join-Path $runRoot 'source-after.json')
        Write-Json ([ordered]@{ Container = "synthetic-$run"; ImageReference = [string]$budget.postgreSqlImage; ImageId = 'sha256:' + ('1' * 64)
            RepoDigests = @("postgres@$digest"); Labels = [ordered]@{ 'bluetusk.owner' = $owner; 'bluetusk.run' = $runId; 'bluetusk.family' = $Family } }) (Join-Path $runRoot 'fixture.json')
        Set-Content -LiteralPath (Join-Path $runRoot 'workload.log') -Value "synthetic $Family run $run" -Encoding utf8NoBOM
        $lines = [Text.StringBuilder]::new()
        [void]$lines.Append("kind,name,worker,slot,start_us,latency_us,outcome`n")
        $operations = foreach ($property in @($budget.operations.PSObject.Properties))
        {
            $schedule = $property.Value
            $planned = [long]$schedule.workers * $seconds * 1000 / $schedule.offerIntervalMilliseconds
            $accepted = [Collections.Generic.List[long]]::new()
            $counts = @{ accepted = 0; error = 0 }
            for ($slot = $Skipped; $slot -lt $planned; $slot++)
            {
                $outcome = if ($slot -lt $Skipped + $Errors) { 'error' } else { 'accepted' }
                $latency = $LatencyMicroseconds + ($slot % 7) * 100
                if ($outcome -eq 'accepted') { $accepted.Add($latency) }
                $counts[$outcome]++
                [void]$lines.Append("operation,$($property.Name),0,$slot,$($slot * $schedule.offerIntervalMilliseconds * 1000 + $run),$latency,$outcome`n")
            }
            $summary = [BlueTusk.Eng.ExpansionCapacitySamples]::Summary($accepted)
            [ordered]@{ Name = $property.Name; Workers = $schedule.workers; OfferIntervalMilliseconds = $schedule.offerIntervalMilliseconds
                PlannedSlots = $planned; Offered = $planned - $Skipped; Accepted = $counts.accepted; Rejected = 0; Errors = $counts.error
                ScheduleSkipped = $Skipped
                Latency = [ordered]@{ Count = [long]$summary[0]; P50Milliseconds = $summary[1]; P95Milliseconds = $summary[2]; P99Milliseconds = $summary[3]; MaximumMilliseconds = $summary[4] }
                RejectedLatency = $null; ErrorTypes = @($(if ($Errors -gt 0) { 'SyntheticException' })) }
        }
        $counters = [ordered]@{}
        foreach ($counter in @($budget.counters.PSObject.Properties))
        {
            if ($null -ne $counter.Value.PSObject.Properties['equalsOperationAccepted'])
            { $counters[$counter.Name] = @($operations | Where-Object { $_.Name -ceq $counter.Value.equalsOperationAccepted })[0].Accepted }
            else { $counters[$counter.Name] = 1000 }
            if ($null -ne $counter.Value.PSObject.Properties['equalsCounter']) { $counters[[string]$counter.Value.equalsCounter] = 1000 }
        }
        if ($null -ne $budget.storage.PSObject.Properties['unitCounter']) { $counters[[string]$budget.storage.unitCounter] = 1000 }
        $series = foreach ($property in @($budget.series.PSObject.Properties))
        {
            $counters[[string]$property.Value.countCounter] = 1000
            $values = [Collections.Generic.List[long]]::new()
            for ($item = 0; $item -lt 1000; $item++)
            {
                $values.Add(50000 + $item)
                [void]$lines.Append("series,$($property.Name),0,$item,$($item * 1000),$(50000 + $item),accepted`n")
            }
            $summary = [BlueTusk.Eng.ExpansionCapacitySamples]::Summary($values)
            [ordered]@{ Name = $property.Name; Latency = [ordered]@{ Count = [long]$summary[0]; P50Milliseconds = $summary[1]
                P95Milliseconds = $summary[2]; P99Milliseconds = $summary[3]; MaximumMilliseconds = $summary[4] } }
        }
        $metrics = [ordered]@{}
        foreach ($metric in @($budget.metrics.PSObject.Properties)) { $metrics[$metric.Name] = 1.0 }
        $interval = [int]$budget.sampleIntervalSeconds
        $samples = for ($elapsed = 0; $elapsed -le $seconds; $elapsed += $interval)
        {
            [ordered]@{ ElapsedSeconds = [double]$elapsed; OwnedRelationBytes = 10000000 + [long]($OwnedGrowthBytes * $elapsed / $seconds)
                DatabaseBytes = 50000000; WalInsertBytes = 1000000 + $elapsed * 100; DatabaseBackends = 10
                ProcessCpuMilliseconds = [double]$elapsed * 10; WorkingSetBytes = 100000000; ManagedHeapBytes = 1000000
                AllocatedBytes = 1000000 + $elapsed; Gen2Collections = 1 }
        }
        $after = [ordered]@{}
        foreach ($key in $samples[-1].Keys) { $after[$key] = $samples[-1][$key] }
        $after.ElapsedSeconds = [double]$seconds + 1
        $samplesPath = Join-Path $runRoot 'samples.csv'
        $text = $lines.ToString()
        [IO.File]::WriteAllText($samplesPath, $text, [Text.UTF8Encoding]::new($false))
        $parameters = [ordered]@{}
        foreach ($parameter in @($budget.parameters.PSObject.Properties)) { $parameters[$parameter.Name] = $parameter.Value }
        $started = [DateTimeOffset]::new(2026, 10, 1, 0, 0, 0, [TimeSpan]::Zero).AddHours($run)
        $report = [ordered]@{ FormatVersion = 1; Family = $Family; Workload = [string]$budget.workload; CandidateSha = $commit
            SourceTreeSha256 = $source.sourceTreeSha256; HarnessBinarySha256 = (Hash $harness); PostgreSqlImage = [string]$budget.postgreSqlImage
            PostgreSqlVersion = "PostgreSQL $($budget.postgreSqlMajorVersion).0 synthetic"; FixtureOwner = $owner; FixtureRunKind = 'github'; FixtureRunId = $runId
            OperatingSystem = 'synthetic'; Runtime = 'synthetic'; ProcessorCount = 16
            StartedUtc = $started.ToString('o'); CompletedUtc = $started.AddSeconds($seconds + 5).ToString('o')
            DurationSeconds = $seconds; MeasuredSeconds = [double]$seconds + 0.25; DrainSeconds = 1.5; SampleIntervalSeconds = $interval
            Parameters = $parameters; Operations = @($operations); Series = @($series); Counters = $counters; Metrics = $metrics
            Invariants = @(@($budget.requiredInvariants) | ForEach-Object { [ordered]@{ Name = $_; Passed = $true; Detail = 'synthetic' } })
            ResourceSamples = @($samples); AfterDrain = $after
            RawSamples = [ordered]@{ Name = 'samples.csv'; Sha256 = (Hash $samplesPath); Rows = ($text.Split("`n").Count - 2) }
            ProductionQualified = $false; Passed = $true }
        Write-Json $report (Join-Path $runRoot 'capacity.json')
    }
    Write-Json ([ordered]@{ SchemaVersion = 1; Family = $Family; Qualification = $definition.Qualification; CandidateSha = $commit
        SourceTreeSha256 = $source.sourceTreeSha256; HarnessBinarySha256 = (Hash $harness); BudgetSha256 = (Hash $BudgetPath)
        Repetitions = $repetitions; SecondsPerRun = $seconds; Diagnostic = [bool]$Diagnostic; QualifiedLocalCapacity = -not $Diagnostic
        ProductionQualified = $false; FixtureOwner = $owner; FixtureRunKind = 'github'; FixtureRunId = $runId; Files = @() }) (Join-Path $root 'manifest.json')
    Update-SyntheticManifest $root
    return $root
}

function Copy-Evidence([string] $Source, [string] $Name)
{
    $target = Join-Path $temporaryRoot $Name
    Copy-Item -LiteralPath $Source -Destination $target -Recurse
    return $target
}

function Edit-Report([string] $Root, [int] $Run, [scriptblock] $Change)
{
    $path = Join-Path $Root "run-$Run/capacity.json"
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    & $Change $report
    Write-Json $report $path
    Update-SyntheticManifest $Root
}

function Verify([string] $Root, [string] $Family, [string] $BudgetPath, [string] $Commit = $commit, [string] $Producer = '4242', [switch] $Evaluation)
{
    return Assert-ExpansionCapacityEvidence -EvidenceRoot $Root -Family $Family -ExpectedCommit $Commit -BudgetPath $BudgetPath -ProducerRunId $Producer -Evaluation:$Evaluation
}

try
{
    $budgets = @{}
    $evidence = @{}
    foreach ($family in Get-ExpansionCapacityFamilies)
    {
        $budgets[$family] = New-SyntheticBudget $family
        $evidence[$family] = New-SyntheticEvidence -Name "positive-$family" -Family $family -BudgetPath $budgets[$family]
        $result = Verify $evidence[$family] $family $budgets[$family]
        Require ($result.Runs -eq 2 -and -not $result.Diagnostic) "Complete synthetic $family evidence must verify."
    }

    $events = $evidence.Events
    $eventsBudget = $budgets.Events
    Assert-Rejected 'wrong-commit' { Verify $events Events $eventsBudget -Commit ('c' * 40) } 'another candidate commit'
    Assert-Rejected 'wrong-family-artifact' { Verify $events Schema $budgets.Schema } 'another family'
    Assert-Rejected 'sql-as-studio' { Verify $evidence.Sql Studio $budgets.Studio } 'another family'
    $relabelled = Copy-Evidence $events 'relabelled-family'
    $manifest = Get-Content -LiteralPath (Join-Path $relabelled 'manifest.json') -Raw | ConvertFrom-Json
    $manifest.Family = 'Schema'; $manifest.Qualification = 'manual-exact-candidate-schema-capacity'; $manifest.FixtureOwner = 'schema-release-capacity'
    Write-Json $manifest (Join-Path $relabelled 'manifest.json')
    Assert-Rejected 'relabelled-family' { Verify $relabelled Schema $budgets.Schema } "does not contain the 'Schema' harness"
    Assert-Rejected 'candidate-budget-changed' {
        Verify $events Events (Join-Path $PSScriptRoot 'events-capacity-budgets.json') } 'budget snapshot differs'
    Assert-Rejected 'producer-run' { Verify $events Events $eventsBudget -Producer '9999' } 'another workflow run'

    $edited = Copy-Evidence $events 'edited-samples'
    $samples = Join-Path $edited 'run-1/samples.csv'
    [IO.File]::WriteAllText($samples, (Get-Content -LiteralPath $samples -Raw).Replace(',2000,accepted', ',1999,accepted'), [Text.UTF8Encoding]::new($false))
    Assert-Rejected 'edited-samples' { Verify $edited Events $eventsBudget } 'artifact hash differs: run-1/samples.csv'
    $forged = Copy-Evidence $events 'edited-samples-rebound'
    Edit-Report $forged 1 {
        param($report)
        $path = Join-Path $forged 'run-1/samples.csv'
        Write-SyntheticSamples $path $report ((Get-Content -LiteralPath $path -Raw) -replace ',(\d+),accepted\n', ",1,accepted`n")
    }
    Assert-Rejected 'edited-samples-rebound' { Verify $forged Events $eventsBudget } 'differs from its raw samples'
    $dropped = Copy-Evidence $events 'dropped-samples'
    Edit-Report $dropped 2 {
        param($report)
        $path = Join-Path $dropped 'run-2/samples.csv'
        $kept = @((Get-Content -LiteralPath $path) | Where-Object { $_ -notmatch '^operation,read,0,(5|6),' })
        Write-SyntheticSamples $path $report (($kept -join "`n") + "`n")
    }
    Assert-Rejected 'dropped-samples' { Verify $dropped Events $eventsBudget } 'outcome counts differ'
    $summary = Copy-Evidence $events 'edited-summary'
    Edit-Report $summary 1 { param($report) $report.Operations[0].Latency.P99Milliseconds = 0.5 }
    Assert-Rejected 'edited-summary' { Verify $summary Events $eventsBudget } 'differs from its raw samples'

    $missing = Copy-Evidence $events 'missing-file'
    [IO.File]::Delete((Join-Path $missing 'run-2/fixture.json'))
    Update-SyntheticManifest $missing
    Assert-Rejected 'missing-file' { Verify $missing Events $eventsBudget } 'run 2 does not contain exactly the run evidence files'
    $unlisted = Copy-Evidence $events 'missing-inventory'
    [IO.File]::Delete((Join-Path $unlisted 'source-after.json'))
    Assert-Rejected 'missing-source' { Verify $unlisted Events $eventsBudget } 'top-level files outside its contract'
    $missingRun = Copy-Evidence $events 'missing-run'
    Remove-Item -LiteralPath (Join-Path $missingRun 'run-2') -Recurse -Force
    Update-SyntheticManifest $missingRun
    Assert-Rejected 'missing-run' { Verify $missingRun Events $eventsBudget } 'does not contain exactly 2 runs'
    $extra = Copy-Evidence $events 'extra-file'
    Set-Content -LiteralPath (Join-Path $extra 'run-1/notes.txt') -Value 'extra' -Encoding utf8NoBOM
    Update-SyntheticManifest $extra
    Assert-Rejected 'extra-file' { Verify $extra Events $eventsBudget } 'run 1 does not contain exactly the run evidence files'
    $duplicate = Copy-Evidence $events 'duplicate-run'
    Remove-Item -LiteralPath (Join-Path $duplicate 'run-2') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $duplicate 'run-1') -Destination (Join-Path $duplicate 'run-2') -Recurse
    Update-SyntheticManifest $duplicate
    Assert-Rejected 'duplicate-run' { Verify $duplicate Events $eventsBudget } "duplicates another run's report"
    $binary = Copy-Evidence $events 'binary-tamper'
    Set-Content -LiteralPath (Join-Path $binary 'binary-snapshot/BlueTusk.Events.LoadHarness.dll') -Value 'other' -Encoding utf8NoBOM
    Update-SyntheticManifest $binary
    Assert-Rejected 'binary-tamper' { Verify $binary Events $eventsBudget } 'binary snapshot hash differs'

    $failed = Copy-Evidence $events 'failed-invariant'
    Edit-Report $failed 2 { param($report) $report.Invariants[0].Passed = $false }
    Assert-Rejected 'failed-invariant' { Verify $failed Events $eventsBudget } 'is missing or failed'
    $image = Copy-Evidence $evidence.Sql 'wrong-image'
    Edit-Report $image 1 { param($report) $report.PostgreSqlImage = 'postgres:17-alpine@sha256:' + ('0' * 64) }
    Assert-Rejected 'wrong-image' { Verify $image Sql $budgets.Sql } 'wrong PostgreSQL image'
    $drift = Copy-Evidence $evidence.Studio 'parameter-drift'
    Edit-Report $drift 1 { param($report) $report.Parameters.tenants = 1 }
    Assert-Rejected 'parameter-drift' { Verify $drift Studio $budgets.Studio } 'workload parameters differ'
    $owner = Copy-Evidence $events 'foreign-owner'
    $manifest = Get-Content -LiteralPath (Join-Path $owner 'manifest.json') -Raw | ConvertFrom-Json
    $manifest.FixtureOwner = 'w6b-capacity'
    Write-Json $manifest (Join-Path $owner 'manifest.json')
    Assert-Rejected 'foreign-owner' { Verify $owner Events $eventsBudget } 'invalid fixture owner identity'
    $unqualified = Copy-Evidence $events 'unqualified'
    $manifest = Get-Content -LiteralPath (Join-Path $unqualified 'manifest.json') -Raw | ConvertFrom-Json
    $manifest.QualifiedLocalCapacity = $false
    Write-Json $manifest (Join-Path $unqualified 'manifest.json')
    Assert-Rejected 'unqualified' { Verify $unqualified Events $eventsBudget } 'does not record a completed qualification'

    $slow = New-SyntheticEvidence -Name 'latency-breach' -Family Events -BudgetPath $eventsBudget -LatencyMicroseconds 900000
    Assert-Rejected 'latency-breach' { Verify $slow Events $eventsBudget } "'append' p99 ms exceeds its maximum 500"
    $faulty = New-SyntheticEvidence -Name 'error-breach' -Family Schema -BudgetPath $budgets.Schema -Errors 1
    Assert-Rejected 'error-breach' { Verify $faulty Schema $budgets.Schema } 'errors exceeds its maximum 0'
    $behind = New-SyntheticEvidence -Name 'schedule-breach' -Family Sql -BudgetPath $budgets.Sql -Skipped 5
    Assert-Rejected 'schedule-breach' { Verify $behind Sql $budgets.Sql } 'schedule-skipped fraction exceeds'
    $grown = New-SyntheticEvidence -Name 'storage-breach' -Family Studio -BudgetPath $budgets.Studio -OwnedGrowthBytes 5000000000
    Assert-Rejected 'storage-breach' { Verify $grown Studio $budgets.Studio } 'owned relation peak bytes exceeds'
    $churn = New-SyntheticEvidence -Name 'late-growth-breach' -Family Schema -BudgetPath $budgets.Schema -OwnedGrowthBytes 900000000
    Assert-Rejected 'late-growth-breach' { Verify $churn Schema $budgets.Schema } 'late owned growth bytes/min exceeds'

    $diagnostic = New-SyntheticEvidence -Name 'diagnostic' -Family Events -BudgetPath $eventsBudget -Diagnostic
    Assert-Rejected 'diagnostic-as-release' { Verify $diagnostic Events $eventsBudget } 'Diagnostic capacity evidence can never satisfy'
    $evaluation = Verify $diagnostic Events $eventsBudget -Evaluation
    Require ($evaluation.Diagnostic -and @($evaluation.Findings | Where-Object { -not $_.Passed -and $_.Name -ceq 'Seconds per run' }).Count -eq 1 -and
        @($evaluation.Findings | Where-Object { -not $_.Passed -and $_.Name -ceq 'Repetitions' }).Count -eq 1 -and
        @($evaluation.Findings | Where-Object { $_.Passed }).Count -gt 0) 'A diagnostic evaluation must record every comparison and the missing duration and repetitions.'
    Assert-Rejected 'evaluate-release-evidence' { Verify $events Events $eventsBudget -Evaluation } 'Only diagnostic evidence'
}
finally
{
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedRoot).StartsWith('bluetusk-expansion-capacity-tests-', [StringComparison]::Ordinal)))
    {
        throw 'Refusing to remove a capacity test directory outside the explicit temporary root.'
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

Write-Output (
    'Events, Schema, Sql and Studio capacity source contracts and archived-evidence verifier passed with ' +
    "$rejected rejected substitutions; no workload was run or qualified.")
