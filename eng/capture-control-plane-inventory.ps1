[CmdletBinding()]
param(
    [string] $DotNetPath = 'dotnet',
    [ValidateRange(3, 50)][int] $Trials = 3,
    [ValidateRange(1, 60)][int] $WarmupSeconds = 5,
    [ValidateRange(1, 60)][int] $MeasurementSeconds = 10
)

# Capture one diagnostic comparison of the Dashboard inventory endpoint.
# The candidate and published reference each run Kestrel and the HTTP clients
# in one process, so their resource samples include both.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$os = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { throw 'Use Windows or Linux.' }
$dotnet = (Get-Command $DotNetPath -CommandType Application -TotalCount 1).Source
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not find the candidate source commit.' }
$runId = 'cp-inventory-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$owner = 'control-plane-inventory-probe'
$output = Join-Path $root "artifacts/control-plane-inventory/$runId"
$raw = Join-Path $output 'raw/controlplane'
$fixture = Join-Path $output 'fixture'
New-Item -ItemType Directory -Path $raw -Force | Out-Null
$project = Join-Path $PSScriptRoot 'ControlPlaneInventoryProbe/ControlPlaneInventoryProbe.csproj'
$workload = "$os|ControlPlane|sources=100|clients=32"
$assemblies = @{}
$implementations = @{}
$records = [Collections.Generic.List[object]]::new()
foreach ($role in 'candidate', 'reference')
{
    $build = Join-Path $output "build/$role"
    $reference = ($role -ceq 'reference').ToString().ToLowerInvariant()
    $log = Join-Path $output "build-$role.log"
    & $dotnet build $project -c Release --artifacts-path $build -p:BlueTuskRuntimeTargetFrameworks=net10.0 "-p:UsePublishedControlPlane=$reference" *> $log
    if ($LASTEXITCODE -ne 0) { throw "Building the $role inventory probe failed. See '$log'." }
    $assemblies[$role] = Join-Path $build 'bin/ControlPlaneInventoryProbe/release/ControlPlaneInventoryProbe.dll'
}
$previousConnection = $env:BLUETUSK_INVENTORY_PROBE_CONNECTION
try
{
    & (Join-Path $PSScriptRoot 'start-performance-fixture.ps1') -RunId $runId -Owner $owner -CaptureProfile native `
        -OutputPath ([IO.Path]::GetRelativePath($root, $fixture)) -SourceCommit $commit
    $manifest = Get-Content -LiteralPath (Join-Path $fixture 'fixture.json') -Raw | ConvertFrom-Json
    $env:BLUETUSK_INVENTORY_PROBE_CONNECTION = Get-Content -LiteralPath (Join-Path $fixture 'connection.secret') -Raw
    & $dotnet $assemblies['candidate'] seed
    if ($LASTEXITCODE -ne 0) { throw 'Could not register the inventory sources.' }
    $runtime = $null
    for ($trial = 0; $trial -lt $Trials; $trial++)
    {
        $order = if ($trial % 2 -eq 0) { @('candidate', 'reference') } else { @('reference', 'candidate') }
        foreach ($role in $order)
        {
            $relativePath = "$role-$trial.json"
            $path = Join-Path $raw $relativePath
            & $dotnet $assemblies[$role] $path $role $commit $trial $WarmupSeconds $MeasurementSeconds
            if ($LASTEXITCODE -ne 0) { throw "Inventory probe $role trial $trial failed." }
            $sample = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $runtime ??= $sample.environment.runtime
            if ($sample.workloadKey -cne $workload -or $sample.environment.architecture -cne 'x64' -or
                $sample.environment.runtime -cne $runtime -or $sample.measurement.completedOperations -le 0)
            { throw 'Inventory trials require nonempty measurements of the same workload and runtime.' }
            if (($role -ceq 'candidate' -and -not $sample.implementation.EndsWith("+$commit", [StringComparison]::Ordinal)) -or
                ($role -ceq 'reference' -and $sample.implementation -cne '1.0.0+7380d7b028c72b2aae348b778711d104d022a3f8'))
            { throw "The $role trial used a different Control Plane assembly." }
            if ($implementations.ContainsKey($role) -and $implementations[$role] -cne $sample.implementation)
            { throw "The $role assembly changed between inventory trials." }
            $implementations[$role] = $sample.implementation
            $records.Add([ordered]@{
                workloadKey = $workload
                trial = $trial
                role = $role
                path = $relativePath
                sha256 = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
            })
        }
    }
    if ((& git -C $root rev-parse HEAD).Trim() -cne $commit) { throw 'The checkout commit changed during the probe.' }
    [ordered]@{
        schemaVersion = 1
        evidenceKind = 'bluetusk-performance-trial-index'
        family = 'ControlPlane'
        sourceCommit = $commit
        os = $os
        diagnostic = $true
        synthetic = $false
        trials = $Trials
        candidateImplementation = $implementations['candidate']
        referenceImplementation = $implementations['reference']
        containerImageDigests = @($manifest.containerImageDigests)
        leadershipGatePassed = $false
        records = @($records)
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $raw 'trial-index.json') -Encoding utf8NoBOM
    Write-Output "Captured $($records.Count) diagnostic inventory trials in '$raw'."
}
finally
{
    $env:BLUETUSK_INVENTORY_PROBE_CONNECTION = $previousConnection
    try { & (Join-Path $PSScriptRoot 'stop-performance-fixture.ps1') -RunId $runId -Owner $owner }
    finally
    {
        [IO.File]::Delete((Join-Path $fixture 'connection.secret'))
        [IO.File]::Delete((Join-Path $fixture 'container-connection.secret'))
    }
}
