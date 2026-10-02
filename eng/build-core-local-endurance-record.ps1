[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [Parameter(Mandatory)][ValidateSet('streams-release-endurance.yml', 'sync-release-endurance.yml',
        'live-control-plane-release-endurance.yml')][string] $ProducerFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $CaptureLogPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string[]] $Containers
)
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
$rolePrefix = switch ($ProducerFile)
{
    'streams-release-endurance.yml' { 'streams' }
    'sync-release-endurance.yml' { 'sync' }
    'live-control-plane-release-endurance.yml' { 'live-control-plane' }
}
$reportPath = Resolve-CoreEvidenceFile $root "$rolePrefix/report.json"
$provenancePath = Resolve-CoreEvidenceFile $root "$rolePrefix/candidate-sbom/build-provenance.json"
$report = Read-CoreEvidenceJson $reportPath
$reportHash = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash
$provenanceHash = (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash
$connectorHash = $null
$captureStartedUtc = ([DateTimeOffset]$report.startedAt).UtcDateTime
$captureCompletedUtc = ([DateTimeOffset]$report.completedAt).UtcDateTime
$logPath = (Resolve-Path -LiteralPath $CaptureLogPath).Path
if ((Get-Item -LiteralPath $logPath).PSIsContainer -or (Get-Item -LiteralPath $logPath).Length -le 0)
{ throw 'Retain the actual nonempty capture log before emitting a local record.' }
$images = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$resources = @(foreach ($container in $Containers)
{
    # Never serialize Docker's Env or command line: they may contain credentials.
    $identity = (& docker inspect --format '{{.Id}}|{{.Config.Image}}|{{index .Config.Labels "bluetusk.owner"}}|{{index .Config.Labels "bluetusk.commit"}}' $container).Trim().Split('|')
    if ($LASTEXITCODE -ne 0 -or $identity.Count -ne 4 -or $identity[0] -cnotmatch '^[0-9a-f]{64}$' -or
        $identity[1] -cnotmatch '^\S+@sha256:[0-9a-f]{64}$' -or
        $identity[2] -cne 'v1-local-qualification' -or $identity[3] -cne $ExpectedCommit)
    { throw 'Local record emission requires inspected, owned, exact-source digest-pinned Docker resources.' }
    $null = $images.Add($identity[1])
    [ordered]@{ name = $container; containerId = $identity[0]; imageReference = $identity[1]
        owner = $identity[2]; sourceCommit = $identity[3] }
})
if ($resources.Count -eq 0 -or -not $images.Contains($contract.endurancePostgreSqlImage))
{ throw 'Local records require the configured Core PostgreSQL fixture.' }
$dockerOs = (& docker info --format '{{.OSType}}').Trim()
if ($LASTEXITCODE -ne 0 -or $dockerOs -cne 'linux' -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64)
{ throw 'Local captures require Linux Docker and an x64 host.' }
$common = @{ ReportPath = $reportPath; ExpectedCommit = $ExpectedCommit; CandidateProvenancePath = $provenancePath }
switch ($rolePrefix)
{
    'streams' {
        & (Join-Path $PSScriptRoot 'verify-streams-endurance-report.ps1') @common -ReleaseTrack Core `
            -RequiredDuration '3.00:00:00' -MinimumTransactions 100000 -ExpectedPostgreSqlImage $contract.endurancePostgreSqlImage
    }
    'sync' {
        $destinations = @($images | Where-Object { $_ -notmatch '^(?:postgres:|mcr.microsoft.com/dotnet/)' })
        if ($destinations.Count -ne 5) { throw 'Sync local captures must identify all five external destination fixtures.' }
        & (Join-Path $PSScriptRoot 'verify-sync-endurance-report.ps1') @common -ReleaseTrack Core `
            -RequiredDuration '1.00:00:00' -MinimumCycles 100 -ExpectedPostgreSqlImage $contract.endurancePostgreSqlImage `
            -ExpectedDestinationImages $destinations
        $connectorPath = Resolve-CoreEvidenceFile $root 'sync/sync-connector-validation.json'
        $connectorHash = (Get-FileHash -LiteralPath $connectorPath -Algorithm SHA256).Hash
        & (Join-Path $PSScriptRoot 'verify-sync-connector-evidence.ps1') -EvidencePath $connectorPath -ExpectedCommit $ExpectedCommit
        $connector = Read-CoreEvidenceJson $connectorPath
        # A separately retained exact-source functional run may precede or follow
        # endurance. Bind the complete execution interval without changing the
        # unchanged 24-hour report or claiming per-cycle TRX coverage.
        $connectorStartedUtc = ([DateTimeOffset]$connector.startedAtUtc).UtcDateTime
        $connectorCompletedUtc = ([DateTimeOffset]$connector.completedAtUtc).UtcDateTime
        if ($connectorStartedUtc -lt $captureStartedUtc) { $captureStartedUtc = $connectorStartedUtc }
        if ($connectorCompletedUtc -gt $captureCompletedUtc) { $captureCompletedUtc = $connectorCompletedUtc }
    }
    'live-control-plane' {
        & (Join-Path $PSScriptRoot 'verify-live-control-plane-endurance-report.ps1') @common `
            -RequiredDuration '1.00:00:00' -MinimumCycles 100000
    }
}
# Emit only after the unchanged release-duration payload reader passes. Raw
# logs, failed captures and original report/provenance files remain untouched.
if ((Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash -cne $reportHash -or
    (Get-FileHash -LiteralPath $provenancePath -Algorithm SHA256).Hash -cne $provenanceHash -or
    ($null -ne $connectorHash -and (Get-FileHash -LiteralPath $connectorPath -Algorithm SHA256).Hash -cne $connectorHash))
{ throw 'Local endurance report or provenance changed during verification.' }
$id = [Guid]::NewGuid().ToString('D')
$directory = Join-Path $root "executions/$id"
$null = New-Item -ItemType Directory -Path $directory -ErrorAction Stop
Copy-Item -LiteralPath $logPath -Destination (Join-Path $directory 'capture.log')
$resources | ConvertTo-Json -Depth 8 -AsArray | Set-Content -LiteralPath (Join-Path $directory 'docker-resources.json') -Encoding utf8NoBOM
function Bind-File([string] $Path)
{
    $file = Resolve-CoreEvidenceFile $root $Path
    return [ordered]@{ path = $Path; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $file).Length }
}
$artifacts = @(foreach ($role in $contract.requiredArtifactRoles)
{
    $binding = $contract.artifactBindings.PSObject.Properties[$role].Value
    if ($binding.workflowFile -ceq $ProducerFile)
    { [ordered]@{ role = $role; file = Bind-File $binding.path } }
})
$manifest = [ordered]@{ schemaVersion = 1; kind = 'LocalDocker'; captureId = $id; producerFile = $ProducerFile
    scope = 'Core'; releaseVersion = '1.1.0'; sourceCommit = $ExpectedCommit; toolSourceCommit = $toolCommit
    sourceTreeDirty = $false; startedUtc = $captureStartedUtc.ToString('O')
    completedUtc = $captureCompletedUtc.ToString('O'); exitCode = 0
    environment = @{ hostOs = $(if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { throw 'Unsupported local host.' })
        architecture = 'x64'; dockerOs = $dockerOs }
    containerImageDigests = @($images | Sort-Object)
    logs = @((Bind-File "executions/$id/capture.log"), (Bind-File "executions/$id/docker-resources.json"))
    artifacts = $artifacts }
$manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $directory 'local-run.json') -Encoding utf8NoBOM
$record = [ordered]@{ kind = 'LocalDocker'; producerFile = $ProducerFile
    capture = @{ id = $id; manifest = Bind-File "executions/$id/local-run.json" } }
$record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $directory 'producer-record.json') -Encoding utf8NoBOM
Write-Output $record
Write-Information 'Retained local endurance payload record. This does not certify GitHub identity, performance leadership, operational approvals or publication.' -InformationAction Continue
