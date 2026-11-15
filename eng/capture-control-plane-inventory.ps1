[CmdletBinding()]
param(
    [string] $DotNetPath = 'dotnet',
    [ValidateRange(3, 50)][int] $Trials = 3,
    [ValidateRange(1, 60)][int] $WarmupSeconds = 5,
    [ValidateRange(1, 60)][int] $MeasurementSeconds = 10
)

# Capture diagnostic comparisons of the Dashboard inventory endpoint.
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
$contract = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-leadership-contract.json') -Raw | ConvertFrom-Json
$dimensions = $contract.workloads.ControlPlane
$workloads = @(foreach ($sourceCount in $dimensions.sourceCounts)
{
    foreach ($clientCount in $dimensions.apiClientCounts)
    {
        @{
            sourceCount = $sourceCount
            clientCount = $clientCount
            key = "$os|ControlPlane|sources=$sourceCount|clients=$clientCount"
        }
    }
})
$assemblies = @{}
$implementations = @{}
$records = [Collections.Generic.List[object]]::new()

function Wait-WindowsLoopbackCapacity
{
    if (-not $IsWindows) { return }

    # Each fresh trial can open three sockets per client across HTTP and PostgreSQL.
    # Let closed loopback connections drain before making the next measurement.
    $network = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (@($network.GetActiveTcpConnections() | Where-Object {
        $_.State -eq 'TimeWait' -and $_.RemoteEndPoint.Address.Equals([Net.IPAddress]::Loopback)
    }).Count -gt 2048)
    {
        if ($deadline.Elapsed.TotalMinutes -ge 5) { throw 'Windows loopback connections did not drain within five minutes.' }
        if ($deadline.Elapsed.TotalSeconds -lt 1) { Write-Output 'Waiting for closed Windows loopback connections to drain.' }
        Start-Sleep -Seconds 5
    }
}

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
    foreach ($sourceCount in $dimensions.sourceCounts)
    {
        & $dotnet $assemblies['candidate'] seed $sourceCount
        if ($LASTEXITCODE -ne 0) { throw "Could not register $sourceCount inventory sources." }
    }
    $runtime = $null
    foreach ($workload in $workloads)
    {
        Write-Output "Measuring '$($workload.key)'."
        for ($trial = 0; $trial -lt $Trials; $trial++)
        {
            $order = if ($trial % 2 -eq 0) { @('candidate', 'reference') } else { @('reference', 'candidate') }
            foreach ($role in $order)
            {
                Wait-WindowsLoopbackCapacity
                $relativePath = "$($workload.sourceCount)-sources-$($workload.clientCount)-clients-$role-$trial.json"
                $path = Join-Path $raw $relativePath
                & $dotnet $assemblies[$role] $path $role $commit $trial $WarmupSeconds $MeasurementSeconds $workload.sourceCount $workload.clientCount
                if ($LASTEXITCODE -ne 0) { throw "Inventory probe $role trial $trial failed for '$($workload.key)'." }
                $sample = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                $runtime ??= $sample.environment.runtime
                if ($sample.workloadKey -cne $workload.key -or $sample.environment.architecture -cne 'x64' -or
                    $sample.environment.runtime -cne $runtime -or $sample.measurement.completedOperations -le 0 -or
                    $sample.measurement.workers.Count -ne $workload.clientCount)
                { throw 'Inventory trials require nonempty measurements of the same workload and runtime.' }
                if (($role -ceq 'candidate' -and -not $sample.implementation.EndsWith("+$commit", [StringComparison]::Ordinal)) -or
                    ($role -ceq 'reference' -and $sample.implementation -cne '1.0.0+7380d7b028c72b2aae348b778711d104d022a3f8'))
                { throw "The $role trial used a different Control Plane assembly." }
                if ($implementations.ContainsKey($role) -and $implementations[$role] -cne $sample.implementation)
                { throw "The $role assembly changed between inventory trials." }
                $implementations[$role] = $sample.implementation
                $records.Add([ordered]@{
                    workloadKey = $workload.key
                    trial = $trial
                    role = $role
                    path = $relativePath
                    sha256 = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
                })
                Write-Output "Captured $role trial $($trial + 1)/$Trials."
            }
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
