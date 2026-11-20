[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $OutputPath,
    [Parameter(Mandatory)][ValidateSet(10, 1000, 100000)][int] $Results,
    [Parameter(Mandatory)][ValidateSet(1, 64, 1000, 10000)][int] $Subscribers,
    [ValidateRange(3, 50)][int] $Trials = 3,
    [ValidateRange(1, 60)][int] $WarmupSeconds = 5,
    [ValidateRange(1, 60)][int] $MeasurementSeconds = 10,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{2,32}$')][string] $RunId = ('live-signalr-' + [guid]::NewGuid().ToString('N').Substring(0, 12)),
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{1,63}$')][string] $Owner = 'live-signalr-probe',
    [string] $DotNetPath = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
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
if ($Results -notin $contract.workloads.Live.resultCounts -or $Subscribers -notin $contract.workloads.Live.subscriberCounts -or
    'affected-key-update' -cnotin $contract.workloads.Live.scenarios)
{ throw 'The requested Live SignalR workload is absent from the performance contract.' }

$os = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { throw 'Use Windows or Linux.' }
$dotnet = (Get-Command $DotNetPath -CommandType Application -TotalCount 1).Source
$work = Join-Path $artifacts "live-signalr/$RunId"
if (Test-Path -LiteralPath $work) { throw 'Use a distinct Live SignalR run ID.' }
$null = New-Item -ItemType Directory -Path $work, $output
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
$workload = "$os|Live|results=$Results|subscribers=$Subscribers|scenario=affected-key-update"
$started = [DateTimeOffset]::UtcNow

try
{
    & (Join-Path $PSScriptRoot 'start-performance-fixture.ps1') -RunId $RunId -Owner $Owner -CaptureProfile native `
        -OutputPath ([IO.Path]::GetRelativePath($root, $fixture)) -SourceCommit $ExpectedCommit -LogicalReplication
    $manifest = Get-Content -LiteralPath (Join-Path $fixture 'fixture.json') -Raw | ConvertFrom-Json
    if ($manifest.postgreSqlImage -cne $plan.fixture.postgreSqlImage) { throw 'The Live fixture image differs from the performance contract.' }
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = [IO.File]::ReadAllText((Join-Path $fixture 'connection.secret'))
    for ($trial = 0; $trial -lt $Trials; $trial++)
    {
        $roles = if ($trial % 2 -eq 0) { 'candidate', 'reference' } else { 'reference', 'candidate' }
        foreach ($role in $roles)
        {
            $name = "$role-$trial.json"
            $path = Join-Path $output $name
            & $dotnet $assembly $path $ExpectedCommit $role $trial $Results $Subscribers $WarmupSeconds $MeasurementSeconds
            if ($LASTEXITCODE) { throw "Live SignalR $role trial $trial failed." }
            $sample = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $runtime ??= $sample.environment.runtime
            if ($sample.sourceCommit -cne $ExpectedCommit -or $sample.workloadKey -cne $workload -or $sample.trial -ne $trial -or
                $sample.role -cne $role -or $sample.diagnostic -ne $true -or $sample.synthetic -ne $false -or
                $sample.environment.runtime -cne $runtime -or $sample.environment.os -cne $os -or
                $sample.environment.architecture -cne 'x64' -or $sample.environment.transport -cne 'signalr-json-websockets' -or
                $sample.environment.source -cne 'postgresql-pgoutput' -or $sample.environment.resultCount -ne $Results -or
                $sample.environment.subscriberCount -ne $Subscribers -or $sample.warmupSeconds -ne $WarmupSeconds -or
                $sample.measurementSeconds -ne $MeasurementSeconds -or $sample.measurement.elapsedTicks -lt ($MeasurementSeconds * $sample.frequency) -or
                $sample.measurement.completedOperations -lt 1 -or
                $sample.verifiedDeliveries -ne ($Subscribers * $sample.measurement.completedOperations) -or
                ($role -ceq 'candidate' -and ($sample.persistedUpdates -ne $sample.measurement.completedOperations -or
                    -not $sample.implementation.EndsWith("+$ExpectedCommit", [StringComparison]::Ordinal))) -or
                ($role -ceq 'reference' -and $sample.implementation -cnotmatch '^ASP\.NET Core SignalR 10\.'))
            { throw 'The capture does not identify the exact assemblies, matched workload, timing and delivery proof.' }

            if ($implementations.ContainsKey($role) -and $implementations[$role] -cne $sample.implementation)
            { throw "The $role implementation changed between Live SignalR trials." }
            $implementations[$role] = $sample.implementation
            $records.Add([ordered]@{
                workloadKey = $workload; role = $role; trial = $trial; path = $name
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            })
            Write-Output "Captured $role Live SignalR trial $($trial + 1)/$Trials."
        }
    }

    Assert-Source
    [ordered]@{
        schemaVersion = 1; evidenceKind = 'bluetusk-performance-trial-index'; family = 'Live'
        sourceCommit = $ExpectedCommit; os = $os; diagnostic = $true; synthetic = $false; trials = $Trials
        warmupSeconds = $WarmupSeconds; measurementSeconds = $MeasurementSeconds; leadershipGatePassed = $false
        startedUtc = $started.ToString('o'); completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        candidateImplementation = $implementations.candidate; referenceImplementation = $implementations.reference
        containerImageDigests = @($manifest.containerImageDigests); records = @($records)
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'trial-index.json') -Encoding utf8NoBOM
}
finally
{
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = $previousConnection
    & (Join-Path $PSScriptRoot 'stop-performance-fixture.ps1') -RunId $RunId -Owner $Owner
}
