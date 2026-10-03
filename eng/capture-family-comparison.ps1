[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Streams', 'Sync', 'Live', 'ControlPlane', 'primary-hot-path')][string] $Family,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $OutputPath,
    [ValidateRange(1, 50)][int] $Trials = 10,
    [switch] $Diagnostic,
    # Print the harness contract for this family and exit successfully without capturing.
    [switch] $Describe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Comparison harnesses for the non-Provider Core families are NOT implemented yet. This entry point
# exists so the workflow, plan and assembler are complete and pluggable; it fails closed until a
# harness writes a performance-trial-index/1 input. Nothing here may write placeholder measurements.
$plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-evidence-plan.json') -Raw | ConvertFrom-Json
$contract = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-leadership-contract.json') -Raw | ConvertFrom-Json
$specification = switch ($Family)
{
    'Streams' { [ordered]@{
        reference = $plan.families.Streams.reference; referenceImage = $plan.families.Streams.referenceImage; mode = 'cross-runtime'
        workloads = "changes {$($contract.workloads.Streams.transactionChanges -join ',')} x scenario {$($contract.workloads.Streams.scenarios -join ',')} plus spoolBytes $($contract.workloads.Streams.spoolBytes -join ',')"
        harness = 'Same pgoutput slot configuration, publication, row shape and sink (an in-process counting sink for BlueTusk; the Debezium Server HTTP sink into an equivalent counting endpoint). One process restart per trial; record commit-to-delivery ticks per change and process/JVM counters.' } }
    'Sync' { [ordered]@{
        reference = $plan.families.Sync.reference; referenceImage = $plan.families.Sync.referenceImage; mode = 'cross-runtime'
        workloads = "mutations {$($contract.workloads.Sync.mutationCounts -join ',')} x destination {$($contract.workloads.Sync.destinations -join ',')}"
        harness = 'Debezium Server 3.6.1.Final sinks (or the native destination client where Debezium has none) against the same digest-pinned destination containers; record source-commit-to-destination-acknowledgement ticks.' } }
    'Live' { [ordered]@{
        reference = $plan.families.Live.reference; referenceImage = $null; mode = 'cross-runtime'
        workloads = "results {$($contract.workloads.Live.resultCounts -join ',')} x subscribers {$($contract.workloads.Live.subscriberCounts -join ',')} x scenario {$($contract.workloads.Live.scenarios -join ',')}"
        harness = 'An ASP.NET Core SignalR 10 hub that re-queries and pushes the same result set on the same change feed, same serializer and same clients; record change-to-client-receipt ticks.' } }
    'ControlPlane' { [ordered]@{
        reference = $plan.families.ControlPlane.reference; referenceImage = $null; mode = 'unique'
        workloads = "sources {$($contract.workloads.ControlPlane.sourceCounts -join ',')} x API clients {$($contract.workloads.ControlPlane.apiClientCounts -join ',')}"
        harness = 'BlueTusk 1.0.0 packages and the candidate in separate processes against the same seeded inventory; record per-request API latency, allocation, CPU and RSS.' } }
    'primary-hot-path' { [ordered]@{
        reference = $plan.primaryHotPaths.reference; referenceImage = $null; mode = 'unique-primary'
        workloads = ($plan.primaryHotPaths.definitions.PSObject.Properties | ForEach-Object { "$($_.Name): $($_.Value)" }) -join '; '
        harness = "Definitions are $($plan.primaryHotPaths.status). Each family's hot path runs against BlueTusk 1.0.0 in the same runtime." } }
}
$specification.format = 'performance-trial-index/1 (see docs/operations/core-performance-evidence.md)'
if ($Describe)
{
    $specification | ConvertTo-Json -Depth 4
    return
}
throw ("The $Family comparison harness is not implemented; the capture fails closed and writes nothing. " +
    "Required: $($specification.harness) Reference: $($specification.reference)" +
    $(if ($specification.referenceImage) { " ($($specification.referenceImage))" } else { '' }) + '. ' +
    'Implement it as a performance-trial-index/1 producer; the generator, checker and assembler already accept it.')
