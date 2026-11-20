[CmdletBinding(DefaultParameterSetName = 'Selected')]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $OutputPath,
    [Parameter(Mandatory, ParameterSetName = 'Selected')][ValidateSet(10, 1000, 100000)][int[]] $Results,
    [Parameter(Mandatory, ParameterSetName = 'Selected')][ValidateSet(1, 64, 1000, 10000)][int[]] $Subscribers,
    [Parameter(ParameterSetName = 'Selected')][ValidateSet('affected-key-update', 'churn', 'slow-client')]
    [string[]] $Scenario = @('affected-key-update'),
    [Parameter(Mandatory, ParameterSetName = 'Contract')][switch] $AllWorkloads,
    [Parameter(ParameterSetName = 'Contract')][switch] $Qualification,
    [ValidateRange(3, 50)][int] $Trials = 3,
    [ValidateRange(1, 60)][int] $WarmupSeconds = 5,
    [ValidateRange(1, 60)][int] $MeasurementSeconds = 10,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{2,32}$')][string] $RunId = ('live-signalr-' + [guid]::NewGuid().ToString('N').Substring(0, 12)),
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{1,63}$')][string] $Owner = 'live-signalr-probe',
    [string] $DotNetPath = 'dotnet',
    [Parameter(ParameterSetName = 'Selected')][switch] $ProfileStages,
    [switch] $Describe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('slow-client' -cin $Scenario -and $ProfileStages)
{ throw 'Per-transaction stage profiles require a one-transaction workload.' }
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$output = [IO.Path]::GetFullPath($OutputPath, $root)
$store = if ($env:BLUETUSK_EVIDENCE_STORE) { [IO.Path]::GetFullPath($env:BLUETUSK_EVIDENCE_STORE) } else { $artifacts }
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$separator = [IO.Path]::DirectorySeparatorChar
if ((-not $output.StartsWith("$artifacts$separator", $comparison) -and
     -not $output.StartsWith($store.TrimEnd($separator) + $separator, $comparison)) -or (Test-Path -LiteralPath $output))
{ throw 'Use a new raw directory inside artifacts or the configured evidence store.' }

function Assert-Source
{
    $head = & git -C $root rev-parse HEAD
    if ($LASTEXITCODE -or $head -cne $ExpectedCommit) { throw 'The Live SignalR checkout differs from the source commit.' }
    $changes = & git -C $root status --porcelain
    if ($LASTEXITCODE -or $changes) { throw 'Live SignalR capture needs a clean checkout.' }
}

Assert-Source
$release = & (Join-Path $PSScriptRoot 'resolve-core-performance-release.ps1') -ExpectedCommit $ExpectedCommit
$plan = Get-Content -LiteralPath $release.PlanPath -Raw | ConvertFrom-Json
$contract = Get-Content -LiteralPath $release.ContractPath -Raw | ConvertFrom-Json
$diagnostic = -not $Qualification
if ($Qualification -and ($Trials -lt $plan.statistics.minimumQualificationTrials -or
    $WarmupSeconds -lt $plan.timing.qualificationWarmupSeconds -or
    $MeasurementSeconds -lt $plan.timing.qualificationMeasurementSeconds))
{ throw 'Live qualification requires the committed minimum trial count and timing.' }

$live = $contract.workloads.Live
if ($AllWorkloads)
{
    $Results = $live.resultCounts
    $Subscribers = $live.subscriberCounts
    $Scenario = $live.scenarios
}
$os = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { throw 'Use Windows or Linux.' }
$cases = @(foreach ($scenarioName in $Scenario)
{
    foreach ($rowCount in $Results)
    {
        foreach ($subscriberCount in $Subscribers)
        {
            if ($rowCount -notin $live.resultCounts -or $subscriberCount -notin $live.subscriberCounts -or
                $scenarioName -cnotin $live.scenarios)
            { throw 'The requested Live SignalR workload is absent from the performance contract.' }

            [pscustomobject]@{
                Scenario = $scenarioName; Results = $rowCount; Subscribers = $subscriberCount
                Key = "$os|Live|results=$rowCount|subscribers=$subscriberCount|scenario=$scenarioName"
            }
        }
    }
})
if ($cases.Count -eq 0 -or @($cases.Key | Sort-Object -Unique).Count -ne $cases.Count)
{ throw 'Select at least one workload and provide each workload once.' }
if ($Describe)
{
    [ordered]@{
        os = $os; diagnostic = $diagnostic; trials = $Trials; records = 2 * $Trials * $cases.Count
        warmupSeconds = $WarmupSeconds; measurementSeconds = $MeasurementSeconds
        workloads = @($cases.Key)
    } | ConvertTo-Json -Depth 4
    return
}
$dotnet = (Get-Command $DotNetPath -CommandType Application -TotalCount 1).Source
$work = Join-Path $artifacts "live-signalr/$RunId"
if (Test-Path -LiteralPath $work) { throw 'Use a distinct Live SignalR run ID.' }
$null = New-Item -ItemType Directory -Path $work, $output
$profiles = Join-Path $work 'stage-profiles'
if ($ProfileStages) { $null = New-Item -ItemType Directory -Path $profiles }
$build = Join-Path $work 'build'
$project = Join-Path $PSScriptRoot 'LiveSignalRProbe/LiveSignalRProbe.csproj'
$buildLog = Join-Path $work 'build.log'
& $dotnet build $project -c Release --artifacts-path $build -p:BlueTuskRuntimeTargetFrameworks=net10.0 --maxcpucount:2 *> $buildLog
if ($LASTEXITCODE) { throw "Building the Live SignalR probe failed. See '$buildLog'." }
$assembly = Join-Path $build 'bin/LiveSignalRProbe/release/LiveSignalRProbe.dll'
$fixture = Join-Path $work 'fixture'
$previousConnection = $env:BLUETUSK_BENCHMARK_CONNECTION_STRING
$records = [Collections.Generic.List[object]]::new()
$implementations = @{}
$runtime = $null
$totalSamples = 0L
$started = [DateTimeOffset]::UtcNow

try
{
    & (Join-Path $PSScriptRoot 'start-performance-fixture.ps1') -RunId $RunId -Owner $Owner -CaptureProfile native `
        -OutputPath ([IO.Path]::GetRelativePath($root, $fixture)) -SourceCommit $ExpectedCommit -LogicalReplication
    $manifest = Get-Content -LiteralPath (Join-Path $fixture 'fixture.json') -Raw | ConvertFrom-Json
    if ($manifest.postgreSqlImage -cne $plan.fixture.postgreSqlImage) { throw 'The Live fixture image differs from the performance contract.' }
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = [IO.File]::ReadAllText((Join-Path $fixture 'connection.secret'))
    foreach ($case in $cases)
    {
        $prefix = if ($cases.Count -gt 1) { "$($case.Scenario)-r$($case.Results)-s$($case.Subscribers)-" } else { '' }
        $eventsPerTransaction = if ($case.Scenario -ceq 'churn') { 2 } else { 1 }
        $testSlowClient = $case.Scenario -ceq 'slow-client'
        $transactionsPerOperation = if ($testSlowClient) { 16 } else { 1 }
        for ($trial = 0; $trial -lt $Trials; $trial++)
        {
            $roles = if ($trial % 2 -eq 0) { 'candidate', 'reference' } else { 'reference', 'candidate' }
            foreach ($role in $roles)
            {
                $name = "$prefix$role-$trial.json"
                $path = Join-Path $output $name
                $probeArguments = @($path, $ExpectedCommit, $role, $trial, $case.Results, $case.Subscribers, $case.Scenario, $WarmupSeconds, $MeasurementSeconds)
                if ($Qualification) { $probeArguments += '--qualification' }
                if ($ProfileStages) { $probeArguments += (Join-Path $profiles $name) }
                & $dotnet $assembly @probeArguments
                if ($LASTEXITCODE) { throw "Live SignalR $role trial $trial failed." }
                $sample = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                $runtime ??= $sample.environment.runtime
                $completed = $sample.measurement.completedOperations
                $totalSamples += $completed
                if ($totalSamples -gt $plan.timing.maximumTotalSamples)
                { throw 'Live SignalR capture exceeded the evidence sample limit.' }
                $expectedEvents = $completed * $eventsPerTransaction * $transactionsPerOperation
                $expectedResets = if ($testSlowClient) { $completed } else { 0 }
                if ($sample.sourceCommit -cne $ExpectedCommit -or $sample.workloadKey -cne $case.Key -or $sample.trial -ne $trial -or
                    $sample.role -cne $role -or $sample.diagnostic -ne $diagnostic -or $sample.synthetic -ne $false -or
                    $sample.environment.runtime -cne $runtime -or $sample.environment.os -cne $os -or
                    $sample.environment.architecture -cne 'x64' -or $sample.environment.transport -cne 'signalr-json-websockets' -or
                    $sample.environment.source -cne 'postgresql-pgoutput' -or $sample.environment.stageProfiling -ne [bool]$ProfileStages -or
                    $sample.environment.scenario -cne $case.Scenario -or $sample.environment.eventsPerTransaction -ne $eventsPerTransaction -or
                    $sample.environment.transactionsPerOperation -ne $transactionsPerOperation -or
                    $sample.environment.slowSubscriberCount -ne [int]$testSlowClient -or
                    $sample.environment.resultCount -ne $case.Results -or
                    $sample.environment.subscriberCount -ne $case.Subscribers -or $sample.warmupSeconds -ne $WarmupSeconds -or
                    $sample.measurementSeconds -ne $MeasurementSeconds -or $sample.measurement.elapsedTicks -lt ($MeasurementSeconds * $sample.frequency) -or
                    $completed -lt 1 -or $sample.verifiedDeliveries -ne ($case.Subscribers * $expectedEvents) -or
                    $sample.slowClientResets -ne $expectedResets -or
                    $sample.slowClientReplayedDeliveries -lt $expectedResets -or
                    $sample.slowClientReplayedDeliveries -gt ($completed * ($transactionsPerOperation - 1)) -or
                    ($role -ceq 'candidate' -and ($sample.persistedUpdates -ne $expectedEvents -or
                        -not $sample.implementation.EndsWith("+$ExpectedCommit", [StringComparison]::Ordinal))) -or
                    ($role -ceq 'reference' -and $sample.implementation -cnotmatch '^ASP\.NET Core SignalR 10\.'))
                { throw 'The capture does not identify the exact assemblies, matched workload, timing and delivery proof.' }

                if ($implementations.ContainsKey($role) -and $implementations[$role] -cne $sample.implementation)
                { throw "The $role implementation changed between Live SignalR trials." }
                $implementations[$role] = $sample.implementation
                $records.Add([ordered]@{
                    workloadKey = $case.Key; role = $role; trial = $trial; path = $name
                    sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                })
                Write-Output "Captured $role trial $($trial + 1)/$Trials for $($case.Key)."
            }
        }
    }

    Assert-Source
    [ordered]@{
        schemaVersion = 1; evidenceKind = 'bluetusk-performance-trial-index'; family = 'Live'
        sourceCommit = $ExpectedCommit; os = $os; diagnostic = $diagnostic; synthetic = $false; trials = $Trials
        warmupSeconds = $WarmupSeconds; measurementSeconds = $MeasurementSeconds; leadershipGatePassed = $false
        startedUtc = $started.ToString('o'); completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        candidateImplementation = $implementations.candidate; referenceImplementation = $implementations.reference
        containerImageDigests = @($manifest.containerImageDigests); records = @($records)
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'trial-index.json') -Encoding utf8NoBOM
    if ($ProfileStages) { Write-Output "Live stage profiles: $profiles" }
}
finally
{
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = $previousConnection
    & (Join-Path $PSScriptRoot 'stop-performance-fixture.ps1') -RunId $RunId -Owner $Owner
}
