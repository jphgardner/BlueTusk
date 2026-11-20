[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [string] $DotNetPath = 'dotnet',
    [string] $OutputPath,
    [ValidateRange(3, 50)][int] $Trials = 3,
    [ValidateRange(1, 60)][int] $WarmupSeconds = 5,
    [ValidateRange(1, 60)][int] $MeasurementSeconds = 10,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{2,40}$')][string] $RunId = ('provider-primary-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)),
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{1,63}$')][string] $Owner = 'provider-primary-probe',
    [switch] $Qualification
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$os = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { throw 'Use Windows or Linux.' }
$dotnet = (Get-Command $DotNetPath -CommandType Application -TotalCount 1).Source
$resolved = & (Join-Path $PSScriptRoot 'resolve-core-performance-release.ps1') -ExpectedCommit $ExpectedCommit
if ($resolved.Release -cne '1.2.0') { throw 'The Provider primary probe requires an exact 1.2 candidate.' }
$contract = Get-Content -LiteralPath $resolved.ContractPath -Raw | ConvertFrom-Json
$plan = Get-Content -LiteralPath $resolved.PlanPath -Raw | ConvertFrom-Json
$approved = $contract.primaryHotPaths.workloads.Provider
if ($approved.feature -cne 'parameterized-scalar' -or $approved.concurrency -ne 64 -or $approved.variant -cne 'native')
{ throw 'The probe must measure the approved native parameterized Provider query with 64 clients.' }
$referenceVersion = "$($contract.primaryHotPaths.referenceVersion)+$($contract.primaryHotPaths.referenceSourceCommit)"
$sampleCapacity = [int][Math]::Floor($plan.timing.maximumTotalSamples / $approved.concurrency)
$evidenceClass = if ($Qualification) { 'qualification' } else { 'diagnostic' }

function Assert-Source
{
    $head = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -or $head -cne $ExpectedCommit) { throw 'The Provider checkout changed during capture.' }
    $dirty = & git -C $root status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -or $dirty) { throw 'The Provider primary probe requires a clean checkout.' }
}

Assert-Source
if ($Qualification -and ($Trials -lt $plan.statistics.minimumQualificationTrials -or
        $WarmupSeconds -lt $plan.timing.qualificationWarmupSeconds -or $MeasurementSeconds -lt $plan.timing.qualificationMeasurementSeconds))
{ throw 'Provider primary qualification requires the committed minimum trial count and timing.' }

$artifacts = Join-Path $root 'artifacts'
$work = Join-Path $artifacts "provider-primary/$RunId"
$raw = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath, $root) } else { Join-Path $work 'raw/provider-primary-hot-path' }
$evidenceStore = if ($env:BLUETUSK_EVIDENCE_STORE) { [IO.Path]::GetFullPath($env:BLUETUSK_EVIDENCE_STORE) } else { $artifacts }
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$separator = [IO.Path]::DirectorySeparatorChar
if ((-not $raw.StartsWith("$artifacts$separator", $pathComparison) -and
     -not $raw.StartsWith($evidenceStore.TrimEnd($separator) + $separator, $pathComparison)) -or
    (Test-Path -LiteralPath $raw) -or (Test-Path -LiteralPath $work))
{ throw 'Provider primary capture requires new directories inside artifacts or the configured evidence store.' }

$null = New-Item -ItemType Directory -Path $raw, $work -Force
$project = Join-Path $PSScriptRoot 'ProviderPrimaryProbe/ProviderPrimaryProbe.csproj'
$assemblies = @{}
foreach ($role in 'candidate', 'reference')
{
    $published = ($role -ceq 'reference').ToString().ToLowerInvariant()
    $build = Join-Path $work "build/$role"
    $log = Join-Path $work "build-$role.log"
    & $dotnet build $project -c Release --artifacts-path $build -p:BlueTuskRuntimeTargetFrameworks=net10.0 "-p:UsePublishedProvider=$published" *> $log
    if ($LASTEXITCODE) { throw "Building the $role Provider probe failed. See '$log'." }
    $assemblies[$role] = Join-Path $build 'bin/ProviderPrimaryProbe/release/ProviderPrimaryProbe.dll'
}

$workload = "$os|Provider|primary-hot-path"
$records = [Collections.Generic.List[object]]::new()
$implementations = @{}
$runtime = $null
$fixture = Join-Path $work 'fixture'
$previousConnection = $env:BLUETUSK_BENCHMARK_CONNECTION_STRING
$started = [DateTimeOffset]::UtcNow
try
{
    & (Join-Path $PSScriptRoot 'start-performance-fixture.ps1') -RunId $RunId -Owner $Owner -CaptureProfile native `
        -OutputPath ([IO.Path]::GetRelativePath($root, $fixture)) -SourceCommit $ExpectedCommit
    $manifest = Get-Content -LiteralPath (Join-Path $fixture 'fixture.json') -Raw | ConvertFrom-Json
    if ($manifest.postgreSqlImage -cne $plan.fixture.postgreSqlImage) { throw 'The Provider probe did not start the contracted PostgreSQL image.' }
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = Get-Content -LiteralPath (Join-Path $fixture 'connection.secret') -Raw
    for ($trial = 0; $trial -lt $Trials; $trial++)
    {
        $order = if ($trial % 2 -eq 0) { @('candidate','reference') } else { @('reference','candidate') }
        foreach ($role in $order)
        {
            $name = "$role-$trial.json"
            $path = Join-Path $raw $name
            & $dotnet $assemblies[$role] $path $ExpectedCommit $role $trial $WarmupSeconds $MeasurementSeconds $sampleCapacity $evidenceClass
            if ($LASTEXITCODE) { throw "The Provider $role trial $trial failed." }
            $sample = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $runtime ??= $sample.environment.runtime
            if ($sample.sourceCommit -cne $ExpectedCommit -or $sample.workloadKey -cne $workload -or $sample.trial -ne $trial -or
                $sample.role -cne $role -or $sample.diagnostic -ne (-not $Qualification) -or $sample.synthetic -ne $false -or
                $sample.environment.runtime -cne $runtime -or $sample.environment.os -cne $os -or $sample.environment.architecture -cne 'x64' -or
                $sample.environment.feature -cne $approved.feature -or $sample.environment.concurrency -ne $approved.concurrency -or
                $sample.environment.variant -cne $approved.variant -or $sample.environment.tlsActive -ne $false -or
                $sample.warmupSeconds -ne $WarmupSeconds -or $sample.measurementSeconds -ne $MeasurementSeconds -or
                $sample.measurement.workers.Count -ne $approved.concurrency -or
                $sample.measurement.elapsedTicks -lt $MeasurementSeconds * $sample.frequency -or
                ($role -ceq 'reference' -and $sample.implementation -cne $referenceVersion) -or
                ($role -ceq 'candidate' -and -not $sample.implementation.EndsWith("+$ExpectedCommit", [StringComparison]::Ordinal)))
            { throw 'Provider trials must match the approved workload, timing, runtime and exact assemblies.' }
            if ($implementations.ContainsKey($role) -and $implementations[$role] -cne $sample.implementation)
            { throw "The $role assembly changed during the Provider comparison." }
            $implementations[$role] = $sample.implementation
            $records.Add([ordered]@{
                workloadKey = $workload; trial = $trial; role = $role; path = $name
                sha256 = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
            })
            Write-Output "Captured $role Provider trial $($trial + 1)/$Trials."
        }
    }
    Assert-Source
    [ordered]@{
        schemaVersion = 1; evidenceKind = 'bluetusk-performance-trial-index'; family = 'Provider'
        sourceCommit = $ExpectedCommit; os = $os; diagnostic = -not $Qualification; synthetic = $false
        trials = $Trials; warmupSeconds = $WarmupSeconds; measurementSeconds = $MeasurementSeconds
        startedUtc = $started.ToString('o'); completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        candidateImplementation = $implementations.candidate; referenceImplementation = $implementations.reference
        containerImageDigests = @($manifest.containerImageDigests); leadershipGatePassed = $false; records = @($records)
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $raw 'trial-index.json') -Encoding utf8NoBOM
}
finally
{
    $env:BLUETUSK_BENCHMARK_CONNECTION_STRING = $previousConnection
    try { & (Join-Path $PSScriptRoot 'stop-performance-fixture.ps1') -RunId $RunId -Owner $Owner }
    finally
    {
        [IO.File]::Delete((Join-Path $fixture 'connection.secret'))
        [IO.File]::Delete((Join-Path $fixture 'container-connection.secret'))
    }
}
