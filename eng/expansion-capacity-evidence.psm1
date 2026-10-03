Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Archived-evidence contract for the Events, Schema, Sql and Studio release capacity gates. The
# pure functions here verify an evidence directory against the candidate budget contract; the
# checkout binding (exact HEAD, clean tree, reference host) is enforced by
# verify-expansion-capacity.ps1. Synthetic inputs can exercise every guard, but only a real
# workflow_dispatch run produces evidence that readiness accepts.
$script:CapacityFamilies = [ordered]@{
    Events = @{ Harness = 'BlueTusk.Events.LoadHarness'; Confirmation = 'RUN-EVENTS-RELEASE-CAPACITY' }
    Schema = @{ Harness = 'BlueTusk.Schema.LoadHarness'; Confirmation = 'RUN-SCHEMA-RELEASE-CAPACITY' }
    Sql = @{ Harness = 'BlueTusk.Sql.LoadHarness'; Confirmation = 'RUN-SQL-RELEASE-CAPACITY' }
    Studio = @{ Harness = 'BlueTusk.Studio.LoadHarness'; Confirmation = 'RUN-STUDIO-RELEASE-CAPACITY' }
}
$script:RunFiles = @('capacity.json', 'fixture.json', 'samples.csv', 'source-after.json', 'workload.log')
$script:TopFiles = @('budgets.json', 'manifest.json', 'runner.json', 'source-after.json', 'source-before.json')
$script:Collect = $null

if (-not ('BlueTusk.Eng.ExpansionCapacitySamples' -as [type]))
{
    # Recomputes every summary from the raw samples with the harness's exact nearest-rank rule.
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
namespace BlueTusk.Eng
{
    public sealed class CapacitySampleGroup
    {
        public string Kind = "";
        public string Name = "";
        public long Accepted;
        public long Rejected;
        public long Errors;
        public List<long> AcceptedLatency = new List<long>();
        public List<long> RejectedLatency = new List<long>();
        public long MaximumStartMicroseconds;
        public string Problem;
    }

    public static class ExpansionCapacitySamples
    {
        public static Dictionary<string, CapacitySampleGroup> Read(string path, long expectedRows)
        {
            var groups = new Dictionary<string, CapacitySampleGroup>(StringComparer.Ordinal);
            var slots = new HashSet<string>(StringComparer.Ordinal);
            long rows = 0;
            using (var reader = new StreamReader(path))
            {
                var header = reader.ReadLine();
                if (header != "kind,name,worker,slot,start_us,latency_us,outcome") { throw new InvalidDataException("Raw sample header is not the capacity contract."); }
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = line.Split(',');
                    if (parts.Length != 7 || (parts[0] != "operation" && parts[0] != "series")) { throw new InvalidDataException("Raw sample row " + (rows + 2) + " is malformed."); }
                    int worker; long slot, start, latency;
                    if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out worker) ||
                        !long.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out slot) ||
                        !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
                        !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out latency))
                    { throw new InvalidDataException("Raw sample row " + (rows + 2) + " has a non-integral field."); }
                    var key = parts[0] + "/" + parts[1];
                    CapacitySampleGroup group;
                    if (!groups.TryGetValue(key, out group)) { group = new CapacitySampleGroup { Kind = parts[0], Name = parts[1] }; groups.Add(key, group); }
                    if (!slots.Add(key + "/" + worker + "/" + slot)) { throw new InvalidDataException("Raw sample row " + (rows + 2) + " repeats a worker slot."); }
                    if (parts[0] == "series" && parts[6] != "accepted") { throw new InvalidDataException("Series sample row " + (rows + 2) + " is not an accepted observation."); }
                    switch (parts[6])
                    {
                        case "accepted": group.Accepted++; group.AcceptedLatency.Add(latency); break;
                        case "rejected": group.Rejected++; group.RejectedLatency.Add(latency); break;
                        case "error": group.Errors++; break;
                        default: throw new InvalidDataException("Raw sample row " + (rows + 2) + " has an unknown outcome.");
                    }
                    if (start > group.MaximumStartMicroseconds) { group.MaximumStartMicroseconds = start; }
                    rows++;
                }
            }
            if (rows != expectedRows) { throw new InvalidDataException("Raw sample row count differs from the report."); }
            return groups;
        }

        public static double[] Summary(List<long> values)
        {
            if (values.Count == 0) { return null; }
            var sorted = values.ToArray();
            Array.Sort(sorted);
            return new double[] { sorted.Length, At(sorted, 0.5), At(sorted, 0.95), At(sorted, 0.99), sorted[sorted.Length - 1] / 1000.0 };
        }

        private static double At(long[] rows, double fraction)
        {
            return rows[Math.Max(0, (int)Math.Ceiling(rows.Length * fraction) - 1)] / 1000.0;
        }
    }
}
'@
}

function Assert-CapacityCondition([bool] $Condition, [string] $Message)
{
    if (-not $Condition) { throw $Message }
}

# A budget comparison. Strict verification throws on the first breach; a diagnostic evaluation
# records every comparison, passing or not, and never certifies anything.
function Test-CapacityBudget([string] $Name, [object] $Observed, [object] $Limit, [ValidateSet('Minimum', 'Maximum')][string] $Kind)
{
    Assert-CapacityCondition ($null -ne $Observed -and $null -ne $Limit) "$Name or its budget is missing."
    $value = [double]$Observed
    $bound = [double]$Limit
    Assert-CapacityCondition ([double]::IsFinite($value) -and [double]::IsFinite($bound)) "$Name or its budget is not finite."
    $passed = if ($Kind -eq 'Minimum') { $value -ge $bound } else { $value -le $bound }
    if ($null -ne $script:Collect)
    {
        $script:Collect.Add([pscustomobject][ordered]@{ Name = $Name; Kind = $Kind; Observed = $value; Limit = $bound; Passed = $passed })
        return
    }
    $relation = if ($Kind -eq 'Minimum') { 'is below its minimum' } else { 'exceeds its maximum' }
    Assert-CapacityCondition $passed "$Name $relation $bound (observed $value)."
}

function Get-CapacityProperty([object] $Object, [string] $Name, [string] $Context)
{
    Assert-CapacityCondition ($Object -is [pscustomobject] -and $null -ne $Object.PSObject.Properties[$Name]) "$Context is missing '$Name'."
    return ,$Object.PSObject.Properties[$Name].Value
}

function Read-CapacityJson([string] $Path)
{
    Assert-CapacityCondition (Test-Path -LiteralPath $Path -PathType Leaf) "Missing evidence: $([IO.Path]::GetFileName($Path))"
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 64
}

function Get-CapacityHash([string] $Path)
{
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ExpansionCapacityFamilies { return @($script:CapacityFamilies.Keys) }

function Get-ExpansionCapacityDefinition
{
    param([Parameter(Mandatory)][string] $Family)
    Assert-CapacityCondition (@($script:CapacityFamilies.Keys | Where-Object { $_ -ceq $Family }).Count -eq 1) (
        "Unknown expansion capacity family '$Family'.")
    $slug = $Family.ToLowerInvariant()
    $definition = $script:CapacityFamilies[$Family]
    return [pscustomobject][ordered]@{
        Family = $Family
        Slug = $slug
        BudgetFile = "$slug-capacity-budgets.json"
        Qualification = "manual-exact-candidate-$slug-capacity"
        WorkflowFile = "$slug-release-capacity.yml"
        Confirmation = $definition.Confirmation
        Harness = $definition.Harness
        HarnessProject = "benchmarks/$($definition.Harness)/$($definition.Harness).csproj"
        HarnessBinaryDirectory = "benchmarks/$($definition.Harness)/bin/Release/net10.0"
        FixtureOwner = "$slug-release-capacity"
    }
}

function Get-ExpansionCapacityArtifactName
{
    param([Parameter(Mandatory)][string] $Family,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $Commit)
    $definition = Get-ExpansionCapacityDefinition -Family $Family
    return "expansion-$($definition.Slug)-capacity-$Commit"
}

function Assert-ExpansionCapacityBudget
{
    # The shape every family contract must keep. Ceilings themselves are pinned by
    # test-expansion-capacity-contract.ps1 so they cannot be weakened silently.
    param([Parameter(Mandatory)][object] $Budget, [Parameter(Mandatory)][string] $Family)
    $definition = Get-ExpansionCapacityDefinition -Family $Family
    foreach ($name in @('schemaVersion', 'family', 'qualification', 'workload', 'referenceProcessor', 'postgreSqlImage',
        'postgreSqlMajorVersion', 'fixture', 'repetitions', 'secondsPerRun', 'sampleIntervalSeconds', 'parameters',
        'operations', 'series', 'metrics', 'counters', 'storage', 'requiredInvariants', 'rationale'))
    {
        [void](Get-CapacityProperty $Budget $name "The $Family capacity budget")
    }
    Assert-CapacityCondition ($Budget.schemaVersion -eq 1 -and [string]$Budget.family -ceq $Family -and
        [string]$Budget.qualification -ceq $definition.Qualification) "The $Family capacity budget has the wrong schema, family or qualification."
    Assert-CapacityCondition ($Budget.repetitions -ge 2 -and $Budget.secondsPerRun -ge 1800 -and
        $Budget.sampleIntervalSeconds -ge 1 -and $Budget.sampleIntervalSeconds -le 10) (
        "The $Family capacity budget must require at least two 30-minute runs with dense resource samples.")
    Assert-CapacityCondition ([string]$Budget.postgreSqlImage -cmatch '^postgres:[0-9a-z.-]+@sha256:[0-9a-f]{64}$' -and
        [string]$Budget.postgreSqlImage -cmatch "^postgres:$($Budget.postgreSqlMajorVersion)[-.]") (
        "The $Family capacity budget must pin a digest PostgreSQL image of its declared major version.")
    $operations = @($Budget.operations.PSObject.Properties)
    Assert-CapacityCondition ($operations.Count -gt 0) "The $Family capacity budget declares no operation."
    foreach ($operation in $operations)
    {
        foreach ($name in @('workers', 'offerIntervalMilliseconds', 'minimumAcceptedPerSecond', 'maximumScheduleSkippedFraction',
            'maximumRejectedFraction', 'maximumErrors', 'maximumP99Milliseconds'))
        {
            [void](Get-CapacityProperty $operation.Value $name "The $Family '$($operation.Name)' budget")
        }
        Assert-CapacityCondition (($Budget.secondsPerRun * 1000) % $operation.Value.offerIntervalMilliseconds -eq 0 -and
            $operation.Value.minimumAcceptedPerSecond -gt 0 -and $operation.Value.maximumErrors -eq 0) (
            "The $Family '$($operation.Name)' budget must have a whole-slot schedule, a positive rate floor and zero errors.")
    }
    Assert-CapacityCondition (@($Budget.requiredInvariants).Count -gt 0) "The $Family capacity budget requires no invariant."
    $storage = $Budget.storage
    [void](Get-CapacityProperty $storage 'maximumOwnedRelationBytes' "The $Family storage budget")
    [void](Get-CapacityProperty $storage 'maximumDatabaseBytes' "The $Family storage budget")
    $unit = $storage.PSObject.Properties['unitCounter']
    $late = $storage.PSObject.Properties['maximumLateOwnedGrowthBytesPerMinute']
    Assert-CapacityCondition (($null -ne $unit -and $null -ne $storage.PSObject.Properties['maximumOwnedGrowthBytesPerUnit'] -and
        $null -ne $storage.PSObject.Properties['maximumWalBytesPerUnit']) -or $null -ne $late) (
        "The $Family storage budget must bound growth per unit or late growth per minute.")
}

function Assert-CapacityNameSet([string[]] $Expected, [string[]] $Actual, [string] $Message)
{
    $left = [string[]]@($Expected | Sort-Object -CaseSensitive)
    $right = [string[]]@($Actual | Sort-Object -CaseSensitive)
    Assert-CapacityCondition ($left.Count -eq $right.Count -and
        @(for ($index = 0; $index -lt $left.Count; $index++) { if ($left[$index] -cne $right[$index]) { $index } }).Count -eq 0) $Message
}

function Assert-ExpansionCapacityLayout
{
    param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][string] $Family,
        [Parameter(Mandatory)][int] $Repetitions, [switch] $Diagnostic)
    $definition = Get-ExpansionCapacityDefinition -Family $Family
    $entries = @(Get-ChildItem -LiteralPath $Root -Force -Recurse)
    Assert-CapacityCondition (@($entries | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -eq 0) (
        'Capacity evidence must not contain links or junctions.')
    $top = @(Get-ChildItem -LiteralPath $Root -Force)
    # Only a diagnostic campaign may carry its (unhashed) budget evaluation beside the evidence.
    $optional = if ($Diagnostic) { @('diagnostic-evaluation.json') } else { @() }
    Assert-CapacityNameSet $script:TopFiles @($top | Where-Object { -not $_.PSIsContainer -and $_.Name -cnotin $optional } | ForEach-Object Name) (
        "Capacity evidence for '$Family' contains top-level files outside its contract.")
    Assert-CapacityNameSet (@('binary-snapshot') + @(1..$Repetitions | ForEach-Object { "run-$_" })) @(
        $top | Where-Object { $_.PSIsContainer } | ForEach-Object Name) "Capacity evidence for '$Family' does not contain exactly $Repetitions runs."
    for ($run = 1; $run -le $Repetitions; $run++)
    {
        $runEntries = @(Get-ChildItem -LiteralPath (Join-Path $Root "run-$run") -Force)
        Assert-CapacityCondition (@($runEntries | Where-Object { $_.PSIsContainer }).Count -eq 0) "Capacity run $run contains a directory."
        Assert-CapacityNameSet $script:RunFiles @($runEntries | ForEach-Object Name) "Capacity run $run does not contain exactly the run evidence files."
    }
    $snapshot = @(Get-ChildItem -LiteralPath (Join-Path $Root 'binary-snapshot') -Force)
    Assert-CapacityCondition (@($snapshot | Where-Object { $_.PSIsContainer }).Count -eq 0 -and
        @($snapshot | Where-Object { $_.Name -ceq 'binaries.json' }).Count -eq 1 -and
        @($snapshot | Where-Object { $_.Name -ceq "$($definition.Harness).dll" }).Count -eq 1) (
        "Capacity binary snapshot does not contain the '$Family' harness.")
}

function Assert-ExpansionCapacitySamples
{
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][object] $Report,
        [Parameter(Mandatory)][string] $Label)
    $groups = [BlueTusk.Eng.ExpansionCapacitySamples]::Read($Path, [long]$Report.RawSamples.Rows)
    $measuredMicroseconds = [double]$Report.MeasuredSeconds * 1000000
    function SameSummary([object] $Summary, [double[]] $Expected, [string] $Name)
    {
        if ($null -eq $Expected) { Assert-CapacityCondition ($null -eq $Summary) "$Name summary exists without raw samples."; return }
        Assert-CapacityCondition ($null -ne $Summary -and [long]$Summary.Count -eq [long]$Expected[0] -and
            [math]::Abs([double]$Summary.P50Milliseconds - $Expected[1]) -lt 1e-9 -and
            [math]::Abs([double]$Summary.P95Milliseconds - $Expected[2]) -lt 1e-9 -and
            [math]::Abs([double]$Summary.P99Milliseconds - $Expected[3]) -lt 1e-9 -and
            [math]::Abs([double]$Summary.MaximumMilliseconds - $Expected[4]) -lt 1e-9) "$Name summary differs from its raw samples."
    }
    $operations = @($Report.Operations)
    foreach ($operation in $operations)
    {
        $key = "operation/$($operation.Name)"
        Assert-CapacityCondition ($groups.ContainsKey($key)) "$Label '$($operation.Name)' has no raw samples."
        $group = $groups[$key]
        Assert-CapacityCondition ($group.Accepted -eq [long]$operation.Accepted -and $group.Rejected -eq [long]$operation.Rejected -and
            $group.Errors -eq [long]$operation.Errors) "$Label '$($operation.Name)' outcome counts differ from its raw samples."
        Assert-CapacityCondition ($group.MaximumStartMicroseconds -le $measuredMicroseconds) "$Label '$($operation.Name)' has samples after the measured window."
        SameSummary $operation.Latency ([BlueTusk.Eng.ExpansionCapacitySamples]::Summary($group.AcceptedLatency)) "$Label '$($operation.Name)' latency"
        SameSummary $operation.RejectedLatency ([BlueTusk.Eng.ExpansionCapacitySamples]::Summary($group.RejectedLatency)) "$Label '$($operation.Name)' rejected latency"
    }
    $series = @($Report.Series)
    foreach ($item in $series)
    {
        $key = "series/$($item.Name)"
        Assert-CapacityCondition ($groups.ContainsKey($key)) "$Label series '$($item.Name)' has no raw samples."
        SameSummary $item.Latency ([BlueTusk.Eng.ExpansionCapacitySamples]::Summary($groups[$key].AcceptedLatency)) "$Label series '$($item.Name)'"
    }
    Assert-CapacityCondition ($groups.Count -eq $operations.Count + $series.Count) "$Label raw samples contain an undeclared operation or series."
}

function Assert-ExpansionCapacityRun
{
    param([Parameter(Mandatory)][string] $RunRoot, [Parameter(Mandatory)][int] $Run,
        [Parameter(Mandatory)][object] $Budget, [Parameter(Mandatory)][object] $Manifest,
        [Parameter(Mandatory)][string] $Family)
    $label = "$Family run $Run"
    $report = Read-CapacityJson (Join-Path $RunRoot 'capacity.json')
    $seconds = [int]$Manifest.SecondsPerRun
    Assert-CapacityCondition ($report.FormatVersion -eq 1 -and [string]$report.Family -ceq $Family -and
        [string]$report.Workload -ceq [string]$Budget.workload) "$label is not a $Family capacity report for the budget workload."
    Assert-CapacityCondition ($report.Passed -eq $true -and $report.ProductionQualified -eq $false) "$label lacks a completed report with passing invariants."
    Assert-CapacityCondition ([string]$report.CandidateSha -ceq [string]$Manifest.CandidateSha -and
        [string]$report.SourceTreeSha256 -ceq [string]$Manifest.SourceTreeSha256 -and
        [string]$report.HarnessBinarySha256 -ceq [string]$Manifest.HarnessBinarySha256) "$label is not bound to the exact candidate source and harness binary."
    Assert-CapacityCondition ([string]$report.PostgreSqlImage -ceq [string]$Budget.postgreSqlImage -and
        [string]$report.PostgreSqlVersion -match "^PostgreSQL $($Budget.postgreSqlMajorVersion)\.") "$label used the wrong PostgreSQL image or version."
    Assert-CapacityCondition ([string]$report.FixtureOwner -ceq [string]$Manifest.FixtureOwner -and
        [string]$report.FixtureRunKind -ceq [string]$Manifest.FixtureRunKind -and
        [string]$report.FixtureRunId -ceq [string]$Manifest.FixtureRunId) "$label used a fixture owned by another campaign."
    $fixture = Read-CapacityJson (Join-Path $RunRoot 'fixture.json')
    $digest = ([string]$Budget.postgreSqlImage -split '@')[-1]
    Assert-CapacityCondition ([string]$fixture.ImageReference -ceq [string]$Budget.postgreSqlImage -and
        [string]$fixture.ImageId -cmatch '^sha256:[0-9a-f]{64}$' -and
        @($fixture.RepoDigests | Where-Object { ([string]$_).EndsWith("@$digest", [StringComparison]::Ordinal) }).Count -gt 0 -and
        [string]$fixture.Labels.'bluetusk.owner' -ceq [string]$Manifest.FixtureOwner -and
        [string]$fixture.Labels.'bluetusk.run' -ceq [string]$Manifest.FixtureRunId -and
        [string]$fixture.Labels.'bluetusk.family' -ceq $Family) "$label fixture differs from the pinned, owned PostgreSQL image."
    $source = Read-CapacityJson (Join-Path $RunRoot 'source-after.json')
    Assert-CapacityCondition ([string]$source.commit -ceq [string]$Manifest.CandidateSha -and $source.dirty -eq $false -and
        [string]$source.sourceTreeSha256 -ceq [string]$Manifest.SourceTreeSha256) "$label changed candidate source."

    $parameters = @($report.Parameters.PSObject.Properties)
    $expectedParameters = @($Budget.parameters.PSObject.Properties)
    Assert-CapacityCondition ($parameters.Count -eq $expectedParameters.Count -and @($expectedParameters | Where-Object {
        $null -eq $report.Parameters.PSObject.Properties[$_.Name] -or [long]$report.Parameters.($_.Name) -ne [long]$_.Value }).Count -eq 0) (
        "$label workload parameters differ from the budget contract.")
    Assert-CapacityCondition ($report.DurationSeconds -eq $seconds -and $report.SampleIntervalSeconds -eq $Budget.sampleIntervalSeconds) (
        "$label has the wrong configured duration or sample interval.")
    Test-CapacityBudget "$label configured seconds" $report.DurationSeconds $Budget.secondsPerRun Minimum
    Assert-CapacityCondition ([double]$report.MeasuredSeconds -ge $seconds - 1 -and [double]$report.MeasuredSeconds -le $seconds + 60) (
        "$label measured window does not cover its configured duration.")

    $budgetOperations = @($Budget.operations.PSObject.Properties)
    $operations = @($report.Operations)
    Assert-CapacityNameSet @($budgetOperations | ForEach-Object Name) @($operations | ForEach-Object { [string]$_.Name }) (
        "$label operations differ from the budget contract.")
    foreach ($operation in $operations)
    {
        $limit = $Budget.operations.([string]$operation.Name)
        $name = "$label '$($operation.Name)'"
        $planned = [long]$limit.workers * [long]$seconds * 1000 / [long]$limit.offerIntervalMilliseconds
        Assert-CapacityCondition ($operation.Workers -eq $limit.workers -and $operation.OfferIntervalMilliseconds -eq $limit.offerIntervalMilliseconds -and
            [long]$operation.PlannedSlots -eq $planned) "$name has the wrong fixed offered-rate schedule."
        Assert-CapacityCondition ([long]$operation.Offered + [long]$operation.ScheduleSkipped -eq $planned -and
            [long]$operation.Offered -eq [long]$operation.Accepted + [long]$operation.Rejected + [long]$operation.Errors -and
            [long]$operation.Accepted -gt 0) "$name has incomplete offered, skipped and outcome accounting."
        Assert-CapacityCondition ($null -ne $operation.Latency -and [long]$operation.Latency.Count -eq [long]$operation.Accepted) "$name latency samples differ from accepted operations."
        if ([long]$operation.Rejected -eq 0) { Assert-CapacityCondition ($null -eq $operation.RejectedLatency) "$name has unexpected rejection timing." }
        else { Assert-CapacityCondition ($null -ne $operation.RejectedLatency -and [long]$operation.RejectedLatency.Count -eq [long]$operation.Rejected) "$name lacks rejection timing." }
        Test-CapacityBudget "$name accepted/s" ([double]$operation.Accepted / [double]$report.MeasuredSeconds) $limit.minimumAcceptedPerSecond Minimum
        Test-CapacityBudget "$name schedule-skipped fraction" ([double]$operation.ScheduleSkipped / [double]$planned) $limit.maximumScheduleSkippedFraction Maximum
        Test-CapacityBudget "$name rejected fraction" ([double]$operation.Rejected / [double][math]::Max(1, [long]$operation.Offered)) $limit.maximumRejectedFraction Maximum
        Test-CapacityBudget "$name errors" ([long]$operation.Errors) $limit.maximumErrors Maximum
        Test-CapacityBudget "$name p99 ms" $operation.Latency.P99Milliseconds $limit.maximumP99Milliseconds Maximum
    }

    $counters = $report.Counters
    $series = @($report.Series)
    Assert-CapacityNameSet @($Budget.series.PSObject.Properties | ForEach-Object Name) @($series | ForEach-Object { [string]$_.Name }) (
        "$label latency series differ from the budget contract.")
    foreach ($item in $series)
    {
        $limit = $Budget.series.([string]$item.Name)
        $expectedCount = [long](Get-CapacityProperty $counters ([string]$limit.countCounter) "$label counters")
        Assert-CapacityCondition ([long]$item.Latency.Count -eq $expectedCount -and $expectedCount -gt 0) "$label series '$($item.Name)' does not cover every counted item."
        Test-CapacityBudget "$label series '$($item.Name)' p99 ms" $item.Latency.P99Milliseconds $limit.maximumP99Milliseconds Maximum
    }
    foreach ($metric in @($Budget.metrics.PSObject.Properties))
    {
        $observed = Get-CapacityProperty $report.Metrics $metric.Name "$label metrics"
        if ($null -ne $metric.Value.PSObject.Properties['maximum']) { Test-CapacityBudget "$label $($metric.Name)" $observed $metric.Value.maximum Maximum }
        if ($null -ne $metric.Value.PSObject.Properties['minimum']) { Test-CapacityBudget "$label $($metric.Name)" $observed $metric.Value.minimum Minimum }
    }
    foreach ($counter in @($Budget.counters.PSObject.Properties))
    {
        $observed = [long](Get-CapacityProperty $counters $counter.Name "$label counters")
        if ($null -ne $counter.Value.PSObject.Properties['minimumPerSecond'])
        { Test-CapacityBudget "$label $($counter.Name)/s" ($observed / [double]$report.MeasuredSeconds) $counter.Value.minimumPerSecond Minimum }
        if ($null -ne $counter.Value.PSObject.Properties['equalsCounter'])
        {
            $other = [long](Get-CapacityProperty $counters ([string]$counter.Value.equalsCounter) "$label counters")
            Assert-CapacityCondition ($observed -eq $other) "$label counter $($counter.Name) differs from $($counter.Value.equalsCounter)."
        }
        if ($null -ne $counter.Value.PSObject.Properties['equalsOperationAccepted'])
        {
            $operation = @($operations | Where-Object { [string]$_.Name -ceq [string]$counter.Value.equalsOperationAccepted })
            Assert-CapacityCondition ($operation.Count -eq 1 -and $observed -eq [long]$operation[0].Accepted) "$label counter $($counter.Name) differs from accepted operations."
        }
    }
    $invariants = @($report.Invariants)
    foreach ($required in @($Budget.requiredInvariants))
    {
        $found = @($invariants | Where-Object { [string]$_.Name -ceq [string]$required })
        Assert-CapacityCondition ($found.Count -eq 1 -and $found[0].Passed -eq $true) "$label invariant '$required' is missing or failed."
    }
    Assert-CapacityCondition (@($invariants | Where-Object { $_.Passed -ne $true }).Count -eq 0) "$label has a failed invariant."

    $samplesPath = Join-Path $RunRoot 'samples.csv'
    Assert-CapacityCondition ([string]$report.RawSamples.Name -ceq 'samples.csv' -and
        [string]$report.RawSamples.Sha256 -ceq (Get-CapacityHash $samplesPath) -and [long]$report.RawSamples.Rows -gt 0) (
        "$label raw samples differ from the report binding.")
    Assert-ExpansionCapacitySamples -Path $samplesPath -Report $report -Label $label

    $interval = [int]$Budget.sampleIntervalSeconds
    $resources = @($report.ResourceSamples)
    Assert-CapacityCondition ($resources.Count -ge [math]::Floor($seconds / $interval) -and [double]$resources[0].ElapsedSeconds -eq 0 -and
        [double]$resources[-1].ElapsedSeconds -ge $seconds - 2 * $interval) "$label resource samples are sparse or do not cover the run."
    for ($index = 0; $index -lt $resources.Count; $index++)
    {
        $sample = $resources[$index]
        Assert-CapacityCondition ([long]$sample.OwnedRelationBytes -gt 0 -and [long]$sample.DatabaseBytes -gt 0 -and
            [long]$sample.WalInsertBytes -gt 0 -and [long]$sample.WorkingSetBytes -gt 0) "$label resource sample $index is invalid."
        if ($index -gt 0)
        {
            $previous = $resources[$index - 1]
            Assert-CapacityCondition ([double]$sample.ElapsedSeconds -gt [double]$previous.ElapsedSeconds -and
                [double]$sample.ElapsedSeconds - [double]$previous.ElapsedSeconds -le 2 * $interval -and
                [long]$sample.WalInsertBytes -ge [long]$previous.WalInsertBytes -and
                [double]$sample.ProcessCpuMilliseconds -ge [double]$previous.ProcessCpuMilliseconds) "$label resource time series is broken at sample $index."
        }
    }
    $after = $report.AfterDrain
    Assert-CapacityCondition ([double]$after.ElapsedSeconds -ge [double]$resources[-1].ElapsedSeconds -and
        [long]$after.OwnedRelationBytes -gt 0 -and [long]$after.WalInsertBytes -ge [long]$resources[-1].WalInsertBytes) "$label lacks a valid post-drain sample."
    $all = @($resources) + @($after)
    $storage = $Budget.storage
    Test-CapacityBudget "$label owned relation peak bytes" (($all | Measure-Object -Property OwnedRelationBytes -Maximum).Maximum) $storage.maximumOwnedRelationBytes Maximum
    Test-CapacityBudget "$label database peak bytes" (($all | Measure-Object -Property DatabaseBytes -Maximum).Maximum) $storage.maximumDatabaseBytes Maximum
    if ($null -ne $storage.PSObject.Properties['unitCounter'])
    {
        $units = [long](Get-CapacityProperty $counters ([string]$storage.unitCounter) "$label counters")
        Assert-CapacityCondition ($units -gt 0) "$label storage unit counter is empty."
        Test-CapacityBudget "$label owned growth bytes/$($storage.unitCounter)" (([double]$after.OwnedRelationBytes - [double]$resources[0].OwnedRelationBytes) / $units) $storage.maximumOwnedGrowthBytesPerUnit Maximum
        Test-CapacityBudget "$label WAL bytes/$($storage.unitCounter)" (([double]$after.WalInsertBytes - [double]$resources[0].WalInsertBytes) / $units) $storage.maximumWalBytesPerUnit Maximum
    }
    if ($null -ne $storage.PSObject.Properties['maximumLateOwnedGrowthBytesPerMinute'])
    {
        $late = @($resources | Where-Object { [double]$_.ElapsedSeconds -ge [double]$report.MeasuredSeconds / 2 })
        Assert-CapacityCondition ($late.Count -ge 3 -and ([double]$late[-1].ElapsedSeconds - [double]$late[0].ElapsedSeconds) -ge $seconds / 3) "$label has no complete late storage window."
        $growth = ([double]$late[-1].OwnedRelationBytes - [double]$late[0].OwnedRelationBytes) / (([double]$late[-1].ElapsedSeconds - [double]$late[0].ElapsedSeconds) / 60.0)
        Test-CapacityBudget "$label late owned growth bytes/min" $growth $storage.maximumLateOwnedGrowthBytesPerMinute Maximum
    }
    return $report
}

function Assert-ExpansionCapacityEvidence
{
    # Verifies one evidence directory completely: budget binding, manifest, exact layout and
    # inventory, source captures, harness snapshot, and every run's raw samples and budgets.
    # -Evaluation returns every budget comparison of a diagnostic campaign instead of
    # failing on the first breach; it never makes diagnostic evidence acceptable.
    param([Parameter(Mandatory)][string] $EvidenceRoot,
        [Parameter(Mandatory)][string] $Family,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
        [Parameter(Mandatory)][string] $BudgetPath,
        [string] $ProducerRunId,
        [switch] $Candidate,
        [switch] $Evaluation)
    $definition = Get-ExpansionCapacityDefinition -Family $Family
    $root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
    $budget = Read-CapacityJson $BudgetPath
    Assert-ExpansionCapacityBudget -Budget $budget -Family $Family
    $manifest = Read-CapacityJson (Join-Path $root 'manifest.json')
    foreach ($name in @('SchemaVersion', 'Family', 'Qualification', 'CandidateSha', 'SourceTreeSha256', 'HarnessBinarySha256',
        'BudgetSha256', 'Repetitions', 'SecondsPerRun', 'Diagnostic', 'QualifiedLocalCapacity', 'ProductionQualified',
        'FixtureOwner', 'FixtureRunKind', 'FixtureRunId', 'Files'))
    {
        [void](Get-CapacityProperty $manifest $name 'The capacity manifest')
    }
    Assert-CapacityCondition ($manifest.SchemaVersion -eq 1 -and $manifest.Family -is [string] -and [string]$manifest.Family -ceq $Family -and
        [string]$manifest.Qualification -ceq $definition.Qualification) "Capacity manifest belongs to another family or qualification."
    Assert-CapacityCondition ([string]$manifest.CandidateSha -ceq $ExpectedCommit) 'Capacity manifest was produced for another candidate commit.'
    Assert-CapacityCondition ($manifest.ProductionQualified -eq $false) 'Capacity manifest overclaims production qualification.'
    $diagnostic = $manifest.Diagnostic -eq $true
    if (-not $Evaluation)
    {
        Assert-CapacityCondition (-not $diagnostic) 'Diagnostic capacity evidence can never satisfy a release capacity role.'
        Assert-CapacityCondition ($Candidate -or $manifest.QualifiedLocalCapacity -eq $true) 'Capacity manifest does not record a completed qualification.'
        Assert-CapacityCondition ($manifest.Repetitions -eq $budget.repetitions -and $manifest.SecondsPerRun -eq $budget.secondsPerRun) (
            'Capacity evidence does not contain the full repeated campaign.')
    }
    else
    {
        Assert-CapacityCondition ($diagnostic -and $manifest.QualifiedLocalCapacity -eq $false) 'Only diagnostic evidence may be evaluated without qualification.'
    }
    Assert-CapacityCondition ($manifest.FixtureOwner -is [string] -and [string]$manifest.FixtureOwner -cmatch '^[a-z0-9][a-z0-9-]{0,62}$' -and
        (($manifest.FixtureRunKind -ceq 'github' -and [string]$manifest.FixtureRunId -cmatch '^[1-9][0-9]*$' -and
          [string]$manifest.FixtureOwner -ceq $definition.FixtureOwner) -or
         ($manifest.FixtureRunKind -ceq 'local' -and [string]$manifest.FixtureRunId -cmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'))) (
        'Capacity manifest has an invalid fixture owner identity.')
    if (-not [string]::IsNullOrEmpty($ProducerRunId))
    {
        Assert-CapacityCondition ($manifest.FixtureRunKind -ceq 'github' -and [string]$manifest.FixtureRunId -ceq $ProducerRunId) (
            'Capacity evidence used a fixture from another workflow run.')
    }
    elseif (-not $Evaluation -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID))
    {
        Assert-CapacityCondition ($manifest.FixtureRunKind -ceq 'github' -and [string]$manifest.FixtureRunId -ceq $env:GITHUB_RUN_ID) (
            'Capacity evidence used a fixture from another workflow run.')
    }

    Assert-ExpansionCapacityLayout -Root $root -Family $Family -Repetitions ([int]$manifest.Repetitions) -Diagnostic:$diagnostic
    $budgetHash = Get-CapacityHash $BudgetPath
    Assert-CapacityCondition ([string]$manifest.BudgetSha256 -ceq $budgetHash -and
        (Get-CapacityHash (Join-Path $root 'budgets.json')) -ceq $budgetHash) 'Capacity budget snapshot differs from the candidate contract.'
    $listed = @($manifest.Files)
    $excluded = @('manifest.json', 'diagnostic-evaluation.json')
    $actual = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
        -not ($_.DirectoryName -eq $root -and $_.Name -cin $excluded) })
    Assert-CapacityCondition ($listed.Count -gt 0 -and $listed.Count -eq $actual.Count) 'Capacity artifact inventory differs from the manifest.'
    $seen = @{}
    foreach ($entry in $listed)
    {
        $relative = [string]$entry.Path
        Assert-CapacityCondition ($relative -cmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -and -not $relative.Contains('..') -and
            -not $seen.ContainsKey($relative)) 'Capacity manifest path is unsafe or repeated.'
        $seen[$relative] = $true
        $path = Join-Path $root $relative
        Assert-CapacityCondition (Test-Path -LiteralPath $path -PathType Leaf) "Capacity manifest artifact is missing: $relative"
        Assert-CapacityCondition ([string]$entry.Sha256 -ceq (Get-CapacityHash $path)) "Capacity manifest artifact hash differs: $relative"
    }

    $before = Read-CapacityJson (Join-Path $root 'source-before.json')
    $after = Read-CapacityJson (Join-Path $root 'source-after.json')
    foreach ($capture in @($before, $after))
    {
        Assert-CapacityCondition ([string]$capture.commit -ceq $ExpectedCommit -and $capture.dirty -eq $false -and
            [string]$capture.sourceTreeSha256 -cmatch '^[0-9a-f]{64}$') 'Source capture does not describe a clean exact candidate.'
    }
    Assert-CapacityCondition ([string]$before.sourceTreeSha256 -ceq [string]$after.sourceTreeSha256 -and
        [string]$before.sourceTreeSha256 -ceq [string]$manifest.SourceTreeSha256) 'Candidate source changed during measurement.'
    $runner = Read-CapacityJson (Join-Path $root 'runner.json')
    Assert-CapacityCondition ([string]$runner.Processor -match [regex]::Escape([string]$budget.referenceProcessor)) 'Capacity evidence used a different reference processor.'

    $binaries = Read-CapacityJson (Join-Path $root 'binary-snapshot/binaries.json')
    Assert-CapacityCondition ([string]$binaries.Harness -ceq $definition.Harness) "Capacity binary snapshot belongs to another harness than '$($definition.Harness)'."
    $records = @($binaries.Files)
    $snapshotFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'binary-snapshot') -File | Where-Object Name -cne 'binaries.json')
    Assert-CapacityCondition ($records.Count -gt 0 -and $records.Count -eq $snapshotFiles.Count) 'Capacity binary snapshot inventory differs.'
    foreach ($record in $records)
    {
        Assert-CapacityCondition ([string]$record.Name -cmatch '^[A-Za-z0-9_.-]+$' -and
            (Test-Path -LiteralPath (Join-Path $root "binary-snapshot/$($record.Name)") -PathType Leaf) -and
            [string]$record.Sha256 -ceq (Get-CapacityHash (Join-Path $root "binary-snapshot/$($record.Name)"))) 'Capacity binary snapshot hash differs.'
    }
    Assert-CapacityCondition ((Get-CapacityHash (Join-Path $root "binary-snapshot/$($definition.Harness).dll")) -ceq [string]$manifest.HarnessBinarySha256) (
        'Capacity harness executable differs from the manifest.')

    $findings = [Collections.Generic.List[object]]::new()
    # Assigned directly: emitting the empty list from an if-expression would enumerate it to null.
    $script:Collect = $null
    if ($Evaluation) { $script:Collect = $findings }
    try
    {
        if ($Evaluation)
        {
            Test-CapacityBudget 'Repetitions' $manifest.Repetitions $budget.repetitions Minimum
            Test-CapacityBudget 'Seconds per run' $manifest.SecondsPerRun $budget.secondsPerRun Minimum
        }
        $previousCompleted = [DateTimeOffset]::MinValue
        $reportHashes = @{}
        $reports = for ($run = 1; $run -le [int]$manifest.Repetitions; $run++)
        {
            $runRoot = Join-Path $root "run-$run"
            $hash = Get-CapacityHash (Join-Path $runRoot 'capacity.json')
            Assert-CapacityCondition (-not $reportHashes.ContainsKey($hash)) "$Family run $run duplicates another run's report."
            $reportHashes[$hash] = $true
            $report = Assert-ExpansionCapacityRun -RunRoot $runRoot -Run $run -Budget $budget -Manifest $manifest -Family $Family
            $started = [DateTimeOffset]$report.StartedUtc
            $completed = [DateTimeOffset]$report.CompletedUtc
            Assert-CapacityCondition ($started -gt $previousCompleted -and $completed -gt $started) "$Family run $run has overlapping or invalid timestamps."
            $previousCompleted = $completed
            $report
        }
    }
    finally { $script:Collect = $null }
    return [pscustomobject][ordered]@{
        Family = $Family
        Commit = $ExpectedCommit
        Runs = @($reports).Count
        Diagnostic = $diagnostic
        Findings = @($findings)
    }
}

Export-ModuleMember -Function Get-ExpansionCapacityFamilies, Get-ExpansionCapacityDefinition, Get-ExpansionCapacityArtifactName,
    Assert-ExpansionCapacityBudget, Assert-ExpansionCapacityLayout, Assert-ExpansionCapacitySamples, Assert-ExpansionCapacityRun,
    Assert-ExpansionCapacityEvidence
