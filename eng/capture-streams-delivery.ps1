[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $OutputPath,
    [ValidateSet('Streams','Sync')][string] $Family = 'Streams',
    [ValidateSet('postgresql','redis','nats')][string] $SyncDestination = 'postgresql',
    [ValidateRange(3, 50)][int] $Trials = 10,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{2,32}$')][string] $RunId = ('streams-' + [guid]::NewGuid().ToString('N').Substring(0, 12)),
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{1,63}$')][string] $Owner = 'streams-benchmark'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'streams-performance/streams-fixture.ps1')
$output = [IO.Path]::GetFullPath($OutputPath, $root)
$artifacts = Join-Path $root 'artifacts'
$separator = [IO.Path]::DirectorySeparatorChar
$evidenceStore = if ($env:BLUETUSK_EVIDENCE_STORE) { [IO.Path]::GetFullPath($env:BLUETUSK_EVIDENCE_STORE) } else { $artifacts }
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if ((-not $output.StartsWith("$artifacts$separator", $pathComparison) -and
     -not $output.StartsWith($evidenceStore.TrimEnd($separator) + $separator, $pathComparison)) -or
    (Test-Path -LiteralPath $output))
{ throw 'Streams capture requires a new raw directory inside artifacts or the configured evidence store.' }

$contract = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-leadership-contract.json') -Raw | ConvertFrom-Json
$plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-evidence-plan.json') -Raw | ConvertFrom-Json
$assembly = Join-Path $root 'benchmarks/BlueTusk.Benchmarks/bin/Release/net10.0/BlueTusk.Benchmarks.dll'
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'Build the Release benchmark assembly before capturing Streams.' }
$os = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { throw 'Use a Windows or Linux benchmark host.' }
$work = Join-Path $artifacts ('streams-delivery/' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work, $output -Force
$records = [Collections.Generic.List[object]]::new()
$implementations = @{}
$started = [DateTimeOffset]::UtcNow

$workloads = if ($Family -ceq 'Sync')
{
    foreach ($changes in $contract.workloads.Sync.mutationCounts)
    {
        @{ Sink = $SyncDestination; Scenario = 'commit-to-delivery'; Changes = $changes; WarmupTransactions = 4; MeasurementTransactions = 20 }
    }
}
else
{
    foreach ($scenario in $contract.workloads.Streams.scenarios)
    {
        foreach ($changes in $contract.workloads.Streams.transactionChanges)
        {
            # Bound row counts while exercising the same transactions on both sides.
            $warmup = if ($scenario -cne 'commit-to-delivery') { 0 } elseif ($changes -eq 1) { 20 } else { 2 }
            $measurement = if ($changes -eq 1) { 1000 } else { 8 }
            @{ Sink = 'count'; Scenario = $scenario; Changes = $changes; WarmupTransactions = $warmup; MeasurementTransactions = $measurement }
        }
    }
    if (@($contract.workloads.Streams.spoolBytes).Count -ne 1 -or $contract.workloads.Streams.spoolBytes[0] -ne $streamsSpillBytes)
    { throw 'The Streams spill harness requires the contracted 4 MiB workload.' }
    foreach ($changes in $contract.workloads.Streams.transactionChanges)
    {
        @{ Sink = 'count'; Scenario = 'spill'; Changes = $changes; WarmupTransactions = 2; MeasurementTransactions = 8 }
    }
}

$workloadNumber = 0
foreach ($workload in $workloads)
{
    $workloadNumber++
    $key = if ($Family -ceq 'Sync') { "$os|Sync|mutations=$($workload.Changes)|destination=$SyncDestination" }
        else { "$os|Streams|changes=$($workload.Changes)|$(Get-StreamsWorkloadSuffix $workload.Scenario)" }
    for ($trial = 0; $trial -lt $Trials; $trial++)
    {
        $order = if ($trial % 2 -eq 0) { @('candidate', 'reference') } else { @('reference', 'candidate') }
        foreach ($role in $order)
        {
            $id = "$workloadNumber-$role-$trial"
            $arguments = $workload + @{
                ExpectedCommit = $ExpectedCommit; Trial = $trial; OutputPath = (Join-Path $work $id)
                RunId = "$RunId-$workloadNumber-$($role[0])$trial"; Owner = $Owner
            }
            if ($role -ceq 'candidate' -or $workload.Sink -cin @('redis', 'nats')) { $arguments.BenchmarkAssemblyPath = $assembly }
            & (Join-Path $PSScriptRoot "capture-streams-delivery-$role.ps1") @arguments
            $capturePath = Join-Path $arguments.OutputPath "$role.json"
            $capture = Get-Content -LiteralPath $capturePath -Raw | ConvertFrom-Json
            if ($capture.family -cne $Family -or $capture.workloadKey -cne $key -or $capture.role -cne $role -or $capture.trial -ne $trial -or
                $capture.sourceCommit -cne $ExpectedCommit -or $capture.environment.postgreSqlImage -cne $plan.fixture.postgreSqlImage -or
                $capture.measurement.completedOperations -ne $workload.Changes * [long]$workload.MeasurementTransactions -or
                $capture.diagnostic -ne $true -or $capture.synthetic -ne $false)
            { throw "The $Family diagnostic trial '$id' differs from its requested source, role or workload." }
            if ($workload.Sink -ceq 'redis' -and
                ($capture.environment.destinationImage -cne $plan.families.Sync.destinationImages.redis -or
                 $capture.environment.destinationProof.recoveredFromAppendOnlyFile -ne $true))
            { throw "The Redis trial '$id' has no matching append-only recovery proof." }
            if ($workload.Sink -ceq 'nats' -and
                ($capture.environment.destinationImage -cne $plan.families.Sync.destinationImages.nats -or
                 $capture.environment.destinationProof.recoveredFromJetStream -ne $true))
            { throw "The NATS trial '$id' has no matching JetStream recovery proof." }
            if ($implementations.ContainsKey($role) -and $implementations[$role] -cne $capture.implementation)
            { throw "The $role implementation changed between $Family trials." }
            $implementations[$role] = $capture.implementation
            $path = "$id.json"
            $destination = Join-Path $output $path
            [IO.File]::Copy($capturePath, $destination)
            $records.Add([ordered]@{
                workloadKey = $key; role = $role; trial = $trial; path = $path
                sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            })
        }
    }
}

$images = @([string]$plan.families.$Family.referenceImage, [string]$plan.fixture.postgreSqlImage)
if ($Family -ceq 'Sync' -and $SyncDestination -cin @('redis', 'nats')) { $images += [string]$plan.families.Sync.destinationImages.$SyncDestination }
[ordered]@{
    schemaVersion = 1; evidenceKind = 'bluetusk-performance-trial-index'; family = $Family; sourceCommit = $ExpectedCommit
    os = $os; diagnostic = $true; synthetic = $false; trials = $Trials; leadershipGatePassed = $false
    startedUtc = $started.ToString('o'); completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    candidateImplementation = $implementations.candidate; referenceImplementation = $implementations.reference
    containerImageDigests = $images
    records = @($records)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'trial-index.json') -Encoding utf8NoBOM
Write-Output "Captured $($records.Count) paired $Family diagnostic trials in '$output'."
