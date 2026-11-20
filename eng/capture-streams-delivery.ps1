[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $OutputPath,
    [ValidateRange(3, 50)][int] $Trials = 10,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{2,29}$')][string] $RunId = ('streams-' + [guid]::NewGuid().ToString('N').Substring(0, 12)),
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{1,63}$')][string] $Owner = 'streams-benchmark'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
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

$workloads = foreach ($scenario in 'commit-to-delivery', 'catch-up')
{
    foreach ($changes in $contract.workloads.Streams.transactionChanges)
    {
        # Bound row counts while exercising the same transactions on both sides.
        $warmup = if ($scenario -ceq 'catch-up') { 0 } elseif ($changes -eq 1) { 20 } else { 2 }
        $measurement = if ($changes -eq 1) { 1000 } else { 8 }
        @{ Scenario = $scenario; Changes = $changes; WarmupTransactions = $warmup; MeasurementTransactions = $measurement }
    }
}

$workloadNumber = 0
foreach ($workload in $workloads)
{
    $workloadNumber++
    $key = "$os|Streams|changes=$($workload.Changes)|scenario=$($workload.Scenario)"
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
            if ($role -ceq 'candidate') { $arguments.BenchmarkAssemblyPath = $assembly }
            & (Join-Path $PSScriptRoot "capture-streams-delivery-$role.ps1") @arguments
            $capturePath = Join-Path $arguments.OutputPath "$role.json"
            $capture = Get-Content -LiteralPath $capturePath -Raw | ConvertFrom-Json
            if ($capture.workloadKey -cne $key -or $capture.role -cne $role -or $capture.trial -ne $trial -or
                $capture.sourceCommit -cne $ExpectedCommit -or $capture.environment.postgreSqlImage -cne $plan.fixture.postgreSqlImage -or
                $capture.measurement.completedOperations -ne $workload.Changes * [long]$workload.MeasurementTransactions -or
                $capture.diagnostic -ne $true -or $capture.synthetic -ne $false)
            { throw "The Streams diagnostic trial '$id' differs from its requested source, role or workload." }
            if ($implementations.ContainsKey($role) -and $implementations[$role] -cne $capture.implementation)
            { throw "The $role implementation changed between Streams trials." }
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

[ordered]@{
    schemaVersion = 1; evidenceKind = 'bluetusk-performance-trial-index'; family = 'Streams'; sourceCommit = $ExpectedCommit
    os = $os; diagnostic = $true; synthetic = $false; trials = $Trials; leadershipGatePassed = $false
    startedUtc = $started.ToString('o'); completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    candidateImplementation = $implementations.candidate; referenceImplementation = $implementations.reference
    containerImageDigests = @([string]$plan.families.Streams.referenceImage, [string]$plan.fixture.postgreSqlImage)
    records = @($records)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'trial-index.json') -Encoding utf8NoBOM
Write-Output "Captured $($records.Count) paired Streams diagnostic trials in '$output'."
