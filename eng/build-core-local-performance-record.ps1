[CmdletBinding()]
param(
    # Candidate evidence root (beneath repository artifacts) that holds performance/performance-leadership-evidence.json.
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    # The retained capture/assembly log of the local core-performance-evidence run.
    [Parameter(Mandatory)][string] $CaptureLogPath,
    # Host evidence store for this run (<store>/<os>/raw/...). Each OS is re-checked from raw before emission.
    [Parameter(Mandatory)][string] $RawEvidenceStore,
    [string] $CheckerPath = (Join-Path $PSScriptRoot 'PerformanceEvidenceChecker/bin/Release/net10.0/PerformanceEvidenceChecker.dll')
)

# Emits a schema-5 LocalDocker producer record for core-performance-evidence.yml.
# Design decision: the record's single environment.hostOs names the PHYSICAL host that ran both legs
# (the Windows PC; the Linux leg runs in a Linux container on it, hence dockerOs=linux). The measured
# Windows and Linux environments are bound inside the performance evidence itself, through its two
# hash-bound environment manifests, which this record also retains as a log.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1') -Force
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$prefix = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $root.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))
{ throw 'Local execution records must remain beneath repository artifacts.' }
$toolCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$status = @(& git -C $repositoryRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0 -or $toolCommit -cnotmatch '^[0-9a-f]{40}$')
{ throw 'Local record emission requires clean, committed verifier tools.' }
$contract = Get-CoreCandidateContract
$producer = 'core-performance-evidence.yml'
$binding = $contract.artifactBindings.performanceManifest
if ($binding.workflowFile -cne $producer -or $producer -cnotin @($contract.localEligibleProducers))
{ throw 'The Core contract no longer accepts a local performance producer.' }
$evidencePath = Resolve-CoreEvidenceFile $root $binding.path
$evidenceHash = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash

# 1. The unchanged release verifier must pass (this rejects diagnostic, synthetic, partial and tied evidence).
& (Join-Path $PSScriptRoot 'verify-performance-leadership-evidence.ps1') -EvidencePath $evidencePath -ExpectedCommit $ExpectedCommit -Scope Core | Out-Null
$evidence = Read-CoreEvidenceJson $evidencePath
$performanceRoot = Split-Path -Parent $evidencePath

# 2. Re-run the independent checker from the retained raw samples; summaries must be byte-identical.
$store = (Resolve-Path -LiteralPath $RawEvidenceStore).Path
$started = [Collections.Generic.List[DateTimeOffset]]::new()
$completed = [Collections.Generic.List[DateTimeOffset]]::new()
$images = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
$environmentBindings = [Collections.Generic.List[object]]::new()
$recheck = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-performance-recheck-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $recheck
try
{
    foreach ($environment in $evidence.environments)
    {
        $os = [string]$environment.os
        foreach ($name in 'summary.json', 'raw-samples.json', 'check-report.json', 'environment-manifest.json')
        {
            $bundled = Join-Path $performanceRoot "$os/$name"
            $retained = Join-Path $store "$os/$name"
            if ((Get-FileHash -LiteralPath $bundled -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $retained -Algorithm SHA256).Hash)
            { throw "The $os $name in the evidence differs from the retained raw evidence store." }
        }
        & dotnet $CheckerPath check (Join-Path $store $os) $ExpectedCommit $os (Join-Path $PSScriptRoot 'performance-variant-map.json') (Join-Path $recheck "$os.json")
        if ($LASTEXITCODE -ne 0) { throw "The independent re-check of the retained $os raw samples failed." }
        $manifest = Read-CoreEvidenceJson (Join-Path $performanceRoot $environment.environmentManifestPath)
        foreach ($image in @($manifest.containerImageDigests)) { $null = $images.Add([string]$image) }
        if ($manifest.captureWindow.startedUtc) { $started.Add([DateTimeOffset]$manifest.captureWindow.startedUtc) }
        if ($manifest.captureWindow.completedUtc) { $completed.Add([DateTimeOffset]$manifest.captureWindow.completedUtc) }
        $environmentBindings.Add([ordered]@{ os = $os; manifestSha256 = $environment.environmentManifestSha256
            rawSamplesSha256 = $environment.rawSamplesSha256; topology = $manifest.topology; host = $manifest.host })
    }
}
finally
{
    Remove-Item -LiteralPath $recheck -Recurse -Force -ErrorAction SilentlyContinue
}
if ($started.Count -ne @($evidence.environments).Count -or $completed.Count -ne @($evidence.environments).Count)
{ throw 'Every measured environment must record its capture window.' }
if (-not $images.Contains($contract.endurancePostgreSqlImage))
{ throw 'Local performance records require the configured Core PostgreSQL fixture image.' }
$dockerOs = (& docker info --format '{{.OSType}}').Trim()
if ($LASTEXITCODE -ne 0 -or $dockerOs -cne 'linux' -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64)
{ throw 'Local captures require Linux Docker and an x64 host.' }
$logPath = (Resolve-Path -LiteralPath $CaptureLogPath).Path
if ((Get-Item -LiteralPath $logPath).PSIsContainer -or (Get-Item -LiteralPath $logPath).Length -le 0)
{ throw 'Retain the actual nonempty capture log before emitting a local record.' }
if ((Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash -cne $evidenceHash)
{ throw 'Performance evidence changed during verification.' }

$id = [Guid]::NewGuid().ToString('D')
$directory = Join-Path $root "executions/$id"
$null = New-Item -ItemType Directory -Path $directory -ErrorAction Stop
Copy-Item -LiteralPath $logPath -Destination (Join-Path $directory 'capture.log')
$environmentBindings | ConvertTo-Json -Depth 8 -AsArray | Set-Content -LiteralPath (Join-Path $directory 'measured-environments.json') -Encoding utf8NoBOM
function Bind-File([string] $Path)
{
    $file = Resolve-CoreEvidenceFile $root $Path
    return [ordered]@{ path = $Path; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $file).Length }
}
$manifest = [ordered]@{ schemaVersion = 1; kind = 'LocalDocker'; captureId = $id; producerFile = $producer
    scope = 'Core'; releaseVersion = '1.1.0'; sourceCommit = $ExpectedCommit; toolSourceCommit = $toolCommit
    sourceTreeDirty = $false
    startedUtc = ($started | Sort-Object | Select-Object -First 1).UtcDateTime.ToString('O')
    completedUtc = ($completed | Sort-Object | Select-Object -Last 1).UtcDateTime.ToString('O')
    exitCode = 0
    environment = @{ hostOs = $(if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { throw 'Unsupported local host.' })
        architecture = 'x64'; dockerOs = $dockerOs }
    containerImageDigests = @($images)
    logs = @((Bind-File "executions/$id/capture.log"), (Bind-File "executions/$id/measured-environments.json"))
    artifacts = @([ordered]@{ role = 'performanceManifest'; file = Bind-File $binding.path }) }
$manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $directory 'local-run.json') -Encoding utf8NoBOM
$record = [ordered]@{ kind = 'LocalDocker'; producerFile = $producer
    capture = @{ id = $id; manifest = Bind-File "executions/$id/local-run.json" } }
$record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $directory 'producer-record.json') -Encoding utf8NoBOM
Write-Output $record
Write-Information 'Retained a local Core performance producer record after the release verifier and an independent raw re-check passed. This does not certify GitHub identity, approvals or publication.' -InformationAction Continue
