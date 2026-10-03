[CmdletBinding()]
param(
    # A generated per-OS directory (contains raw-samples.json).
    [Parameter(Mandatory)][string] $OsRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][ValidateSet('windows', 'linux')][string] $Os,
    # How the measured client reached the server, for example "linux runner container on the fixture Docker network".
    [Parameter(Mandatory)][string] $Topology,
    # Self-test only: writes a manifest for synthetic fixtures without probing (or matching) this host.
    [switch] $Synthetic
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $OsRoot).Path
$manifestPath = Join-Path $root 'environment-manifest.json'
if (Test-Path -LiteralPath $manifestPath) { throw 'Environment manifests are never overwritten.' }
$rawPath = Join-Path $root 'raw-samples.json'
$raw = Get-Content -LiteralPath $rawPath -Raw | ConvertFrom-Json
if ($raw.sourceCommit -cne $ExpectedCommit -or $raw.os -cne $Os) { throw 'Raw samples belong to another commit or OS.' }
if ($raw.synthetic -and -not $Synthetic) { throw 'Synthetic raw samples need the explicit -Synthetic self-test switch.' }
if ($Synthetic -and -not $raw.synthetic) { throw '-Synthetic may only describe synthetic fixtures.' }

$runtimeOs = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { 'other' }
if (-not $Synthetic -and ($runtimeOs -cne $Os -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64))
{ throw "This manifest must be written on the measured $Os x64 host." }

$started = @()
$completed = @()
foreach ($rawInput in @($raw.inputs))
{
    $index = Get-Content -LiteralPath (Join-Path $root $rawInput.index) -Raw | ConvertFrom-Json
    foreach ($name in @('startedUtc')) { if ($index.PSObject.Properties[$name]) { $started += [DateTimeOffset]$index.$name } }
    foreach ($name in @('capturedUtc', 'completedUtc')) { if ($index.PSObject.Properties[$name]) { $completed += [DateTimeOffset]$index.$name } }
}

function Get-HostDescription
{
    if ($Synthetic) { return [ordered]@{ synthetic = $true; note = 'Synthetic self-test fixture; no host was measured.' } }
    $processor = try
    {
        if ($IsWindows) { (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim() }
        else { ((Get-Content /proc/cpuinfo | Where-Object { $_ -like 'model name*' } | Select-Object -First 1) -split ':', 2)[1].Trim() }
    }
    catch { 'unavailable' }
    $memory = try
    {
        if ($IsWindows) { [long](Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory }
        else { [long]((Get-Content /proc/meminfo | Select-Object -First 1) -replace '\D', '') * 1024 }
    }
    catch { $null }
    # Selected fields only: Docker's full info, container Env and command lines can carry credentials.
    $docker = try
    {
        $info = (& docker info --format '{{json .}}') | ConvertFrom-Json
        [ordered]@{ serverVersion = $info.ServerVersion; osType = $info.OSType; operatingSystem = $info.OperatingSystem
            kernelVersion = $info.KernelVersion; cpus = $info.NCPU; memoryBytes = $info.MemTotal }
    }
    catch { $null }
    return [ordered]@{
        osDescription = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        processor = $processor
        logicalProcessors = [Environment]::ProcessorCount
        physicalMemoryBytes = $memory
        dotnetRuntime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        insideContainer = (Test-Path -LiteralPath '/.dockerenv')
        docker = $docker
        runner = [ordered]@{ name = $env:RUNNER_NAME; os = $env:RUNNER_OS; workflowRunId = $env:GITHUB_RUN_ID; workflowRunAttempt = $env:GITHUB_RUN_ATTEMPT }
    }
}

$map = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-variant-map.json') -Raw | ConvertFrom-Json
$manifest = [ordered]@{
    schemaVersion = 1
    evidenceKind = 'bluetusk-performance-environment'
    sourceCommit = $ExpectedCommit
    os = $Os
    architecture = 'x64'
    diagnostic = [bool]$raw.diagnostic
    synthetic = [bool]$raw.synthetic
    rawSamplesSha256 = (Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash.ToLowerInvariant()
    containerImageDigests = @($raw.inputs | ForEach-Object { @($_.containerImageDigests) } | Sort-Object -Unique -CaseSensitive)
    captureWindow = [ordered]@{
        startedUtc = $(if ($started.Count) { ($started | Sort-Object | Select-Object -First 1).UtcDateTime.ToString('o') } else { $null })
        completedUtc = $(if ($completed.Count) { ($completed | Sort-Object | Select-Object -Last 1).UtcDateTime.ToString('o') } else { $null })
    }
    topology = $Topology
    committedVariantMapCrossOsProfile = $map.crossOsProfile
    inputs = @($raw.inputs | ForEach-Object { [ordered]@{ id = $_.id; family = $_.family; variant = $_.variant; captureProfile = $_.captureProfile; trials = $_.trials } })
    host = Get-HostDescription
}
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Output "Wrote $Os environment manifest binding $(@($manifest.containerImageDigests).Count) container images and raw samples $($manifest.rawSamplesSha256)."
