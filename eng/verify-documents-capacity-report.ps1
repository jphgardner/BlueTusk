[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReportPath,
    [Parameter(Mandatory)][ValidateRange(60, 86400)][int]$MinimumSustainedSeconds,
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$MaximumDocumentsRelationBytes,
    [Parameter(Mandatory)][ValidateRange(0, [long]::MaxValue)][long]$MaximumLateGrowthBytesPerMinute,
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$MaximumWalBytesPerTransition,
    [Parameter(Mandatory)][ValidateRange(1, [double]::MaxValue)][double]$MinimumTransitionsPerSecond,
    [Parameter(Mandatory)][ValidateRange(0.001, [double]::MaxValue)][double]$MaximumSaveP99Milliseconds,
    [string]$ExpectedSourceFingerprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$report = Get-Content -LiteralPath (Resolve-Path -LiteralPath $ReportPath).Path -Raw |
    ConvertFrom-Json -Depth 100
$failures = [Collections.Generic.List[string]]::new()

if ($report.Scenarios.Count -ne 16 -or
    @($report.Scenarios | Where-Object { $_.Verified -ne $true }).Count -ne 0)
{
    $failures.Add('The complete 15-cell sweep and sustained scenario must pass logical verification.')
}
if ($report.Recovery.ChildHardKilled -ne $true -or
    $report.Recovery.NoPartialBatch -ne $true -or
    $report.Recovery.AllAcknowledgedWritesSurvivedReopen -ne $true -or
    $report.Recovery.TenantIsolation -ne $true -or
    $report.Recovery.StaleRevisionRejectedAfterReinsert -ne $true)
{
    $failures.Add('The hard-killed writer recovery contract is incomplete.')
}
if ($report.SustainedSeconds -lt $MinimumSustainedSeconds)
{
    $failures.Add("Requested sustained seconds $($report.SustainedSeconds) are below $MinimumSustainedSeconds.")
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceFingerprint) -and
    -not [string]::Equals([string]$report.Environment.SourceFingerprint,
        $ExpectedSourceFingerprint, [StringComparison]::Ordinal))
{
    $failures.Add('The report source fingerprint differs from the expected candidate.')
}

$sustained = @($report.Scenarios | Where-Object { $_.Name -eq 'sustained-fixed-cardinality-churn' })
if ($sustained.Count -ne 1)
{
    throw "Expected one sustained fixed-cardinality scenario, found $($sustained.Count)."
}
$scenario = $sustained[0]
$maintenance = $scenario.Maintenance
if ($null -eq $maintenance) { throw 'The sustained scenario has no physical maintenance observations.' }
$payloadDistribution = if ($scenario.PSObject.Properties.Match('PayloadDistribution').Count -eq 1)
{ [string]$scenario.PayloadDistribution } else { 'unrecorded' }

if ($scenario.MeasuredSeconds -lt $MinimumSustainedSeconds -or
    $scenario.PayloadBytes -ne 65536 -or $scenario.Tenants -ne 8 -or
    $scenario.Writers -ne 32 -or $scenario.RowsPerWriterTenant -ne 8 -or
    $payloadDistribution -ne 'distinct-stable-per-document' -or
    $scenario.TenantProgress.Count -ne 8 -or
    @($scenario.TenantProgress | Where-Object { $_ -le 0 }).Count -ne 0 -or
    [long]($scenario.TenantProgress | Measure-Object -Sum).Sum -ne [long]$scenario.CommittedDocuments -or
    $scenario.ReplaceSaves -le 0 -or $scenario.PatchSaves -le 0 -or
    $scenario.DeleteReinsertCycles -le 0 -or $scenario.RejectedBoundedWrites -le 0)
{
    $failures.Add('The sustained workload duration, fixed cardinality, progress, or mixed operations are incomplete.')
}
if ($scenario.CommittedDocuments -le 0 -or
    $scenario.CommittedDocumentsPerSecond -lt $MinimumTransitionsPerSecond)
{
    $failures.Add("Throughput $($scenario.CommittedDocumentsPerSecond) is below $MinimumTransitionsPerSecond transitions/s.")
}
if ($scenario.SaveLatency.Count -le 0 -or
    $scenario.SaveLatency.P99 -gt $MaximumSaveP99Milliseconds)
{
    $failures.Add("Save p99 $($scenario.SaveLatency.P99) ms exceeds $MaximumSaveP99Milliseconds ms.")
}
$walBytes = [long]$scenario.After.WalBytes - [long]$scenario.Before.WalBytes
if ($walBytes -lt 0 -or $scenario.CommittedDocuments -le 0 -or
    $walBytes / [double]$scenario.CommittedDocuments -gt $MaximumWalBytesPerTransition)
{
    $failures.Add("WAL $walBytes bytes for $($scenario.CommittedDocuments) transitions exceeds $MaximumWalBytesPerTransition bytes/transition.")
}

$samples = @($maintenance.DuringWrites)
$minimumSamples = [math]::Floor($MinimumSustainedSeconds / 10)
if ($samples.Count -lt $minimumSamples -or
    $samples[0].ElapsedSeconds -gt 15 -or
    $samples[-1].ElapsedSeconds -lt $scenario.MeasuredSeconds - 15 -or
    $maintenance.Observer.DatabaseProbeTimeouts -ne 0 -or
    $maintenance.Observer.MaximumMaintenanceGapSeconds -gt 45)
{
    $failures.Add('Maintenance observations are too sparse, incomplete, or timed out.')
}
$previous = 0.0
foreach ($sample in $samples)
{
    if ($sample.ElapsedSeconds -le $previous -or
        $sample.ElapsedSeconds - $previous -gt 45 -or
        $sample.Observation.Documents.TotalBytes -le 0 -or
        $sample.Observation.Toast.TotalBytes -gt $sample.Observation.Documents.TotalBytes)
    {
        $failures.Add('Maintenance time series has a gap, invalid ordering, or inconsistent relation sizes.')
        break
    }
    $previous = [double]$sample.ElapsedSeconds
}
if ($null -eq $maintenance.AfterIdleDrain -or
    $maintenance.BeforeWrites.Documents.TotalBytes -le 0 -or
    $maintenance.AfterWrites.Documents.TotalBytes -le 0)
{
    $failures.Add('The pre-write, post-write, or idle-drain physical observation is missing.')
}

# pg_total_relation_size(documents) already includes TOAST. Never add the child again.
$peakBytes = [math]::Max([long]$maintenance.BeforeWrites.Documents.TotalBytes,
    [long]$maintenance.AfterWrites.Documents.TotalBytes)
$peakBytes = [math]::Max($peakBytes,
    [long]$maintenance.AfterHotKeyAndVerification.Documents.TotalBytes)
foreach ($sample in $samples)
{
    $peakBytes = [math]::Max($peakBytes, [long]$sample.Observation.Documents.TotalBytes)
}
foreach ($sample in @($maintenance.DuringIdleDrain))
{
    $peakBytes = [math]::Max($peakBytes, [long]$sample.Observation.Documents.TotalBytes)
}
if ($null -ne $maintenance.AfterIdleDrain)
{
    $peakBytes = [math]::Max($peakBytes, [long]$maintenance.AfterIdleDrain.Documents.TotalBytes)
}
if ($peakBytes -gt $MaximumDocumentsRelationBytes)
{
    $failures.Add("Documents relation peak $peakBytes bytes exceeds $MaximumDocumentsRelationBytes bytes.")
}

$late = @($samples | Where-Object { $_.ElapsedSeconds -ge $scenario.MeasuredSeconds / 2 })
if ($late.Count -lt [math]::Max(3, [math]::Floor($minimumSamples / 2)) -or
    $late[-1].ElapsedSeconds - $late[0].ElapsedSeconds -lt $MinimumSustainedSeconds / 3)
{
    $failures.Add('The late physical-growth window does not cover enough sustained time.')
}
else
{
    $lateGrowth = [long]$maintenance.AfterWrites.Documents.TotalBytes -
        [long]$late[0].Observation.Documents.TotalBytes
    $lateMinutes = ($scenario.MeasuredSeconds - $late[0].ElapsedSeconds) / 60.0
    $growthPerMinute = $lateGrowth / $lateMinutes
    if ($growthPerMinute -gt $MaximumLateGrowthBytesPerMinute)
    {
        $failures.Add("Late relation growth $([math]::Round($growthPerMinute)) bytes/min exceeds $MaximumLateGrowthBytesPerMinute bytes/min.")
    }
}

if ($failures.Count -gt 0)
{
    foreach ($failure in $failures) { Write-Error -Message $failure -ErrorAction Continue }
    throw "Documents capacity report failed $($failures.Count) qualification check(s)."
}

Write-Output "Documents capacity report passed: $($scenario.CommittedDocuments) transitions, $peakBytes peak relation bytes, $walBytes WAL bytes."
