<#
.SYNOPSIS
Retain a Core producer record from the live GitHub attempt and complete job metadata.
.DESCRIPTION
Records bind an actual successful manual workflow's head commit, workflow ID/path,
attempt and job lifecycle. This does not authenticate checked-out source, artifacts,
fixtures, payload qualification, operational approvals or release eligibility.
With ArtifactId and ArtifactName, it also retains the actual binary ZIP, checks
its GitHub digest/size and inventories safe member paths and decompressed hashes.
This delivery receipt does not authenticate product checkout or qualify payloads.
Rejected API captures are retained and do not produce a run record. Use a fresh
directory below this checkout's artifacts directory for every invocation.
.EXAMPLE
./eng/collect-core-github-run.ps1 -WorkflowFile security.yml -RunId 123 -RunAttempt 1 `
    -ExpectedCommit <full-candidate-sha> -OutputDirectory artifacts/core-security-capture
#>
[CmdletBinding(DefaultParameterSetName='Run')]
param([Parameter(Mandatory)][string] $WorkflowFile,
    [Parameter(Mandatory)][ValidateRange(1,[long]::MaxValue)][long] $RunId,
    [Parameter(Mandatory)][ValidateRange(1,[int]::MaxValue)][int] $RunAttempt,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk',
    [Parameter(Mandatory,ParameterSetName='Artifact')][ValidateRange(1,[long]::MaxValue)][long] $ArtifactId,
    [Parameter(Mandatory,ParameterSetName='Artifact')][ValidateNotNullOrEmpty()][string] $ArtifactName)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')
Import-Module (Join-Path $PSScriptRoot 'core-github-run-capture.psm1')
Import-Module (Join-Path $PSScriptRoot 'core-github-artifact-capture.psm1')
$captureArtifact = $PSCmdlet.ParameterSetName -ceq 'Artifact'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$contract = Get-CoreCandidateContract
if ($WorkflowFile -cnotin $contract.requiredWorkflows) { throw 'Unknown Core producer workflow.' }
$artifacts = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) { throw 'Choose a fresh capture directory below repository artifacts.' }
$ancestor = Split-Path $output -Parent
while ($ancestor)
{
    if (Test-Path -LiteralPath $ancestor)
    {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        { throw 'Capture ancestry must contain only directories, without symbolic links or junctions.' }
    }
    $parent = Split-Path $ancestor -Parent
    if ($parent -eq $ancestor) { break }
    $ancestor = $parent
}
$toolCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $toolCommit -cnotmatch '^[0-9a-f]{40}$' -or
    @(& git -C $repositoryRoot status --porcelain --untracked-files=normal).Count -ne 0)
{ throw 'The GitHub collector requires a clean committed tool checkout.' }
$candidateTime = & git -C $repositoryRoot show --no-patch --format=%cI "${ExpectedCommit}^{commit}"
if ($LASTEXITCODE -ne 0) { throw 'The exact candidate commit must be available locally.' }
$commitUtc = [DateTimeOffset]::Parse($candidateTime.Trim())
$null = Get-Command gh -CommandType Application -ErrorAction Stop
$null = [IO.Directory]::CreateDirectory($output)
$started = [DateTimeOffset]::UtcNow.ToString('O')
$recordPath = Join-Path $output 'record.json'
$recordRetained = $false
$artifactReceipt = $null
$artifactValidated = $false

function Write-Json([string] $Name, [object] $Value)
{
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 32))
    $file = [IO.File]::Open((Join-Path $output $Name), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $file.Write($bytes, 0, $bytes.Length) } finally { $file.Dispose() }
}
function Capture-Api([string] $Endpoint, [string] $Name, [switch] $Pages)
{
    $arguments = @('api', $Endpoint)
    if ($Pages) { $arguments += @('--paginate', '--slurp') }
    $lines = & gh @arguments 2> (Join-Path $output "$Name.stderr.log")
    if ($LASTEXITCODE -ne 0) { throw "GitHub API capture failed for $Name; stderr is retained." }
    $path = Join-Path $output "$Name.json"
    [IO.File]::WriteAllText($path, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    return Read-CoreEvidenceJson $path -Array:$Pages
}
function Capture-Archive([string] $Endpoint)
{
    # Binary stdout must never pass through PowerShell text redirection.
    # gh follows the API's download redirect; no signed URL or token is retained.
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Get-Command gh -CommandType Application).Source)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $null = $startInfo.Environment.Remove('GH_DEBUG')
    $null = $startInfo.Environment.Remove('GH_FORCE_TTY')
    foreach ($argument in @('api', $Endpoint)) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $destination = $null
    $stderr = $null
    $processStarted = $false
    try
    {
        if (-not $process.Start()) { throw 'Artifact download process did not start.' }
        $processStarted = $true
        $stderr = $process.StandardError.ReadToEndAsync()
        $destination = [IO.File]::Open((Join-Path $output 'artifact.zip'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $buffer = [byte[]]::new(65536)
        $total = 0L
        while ($true)
        {
            $remaining = 300000 - [int]$timer.ElapsedMilliseconds
            if ($remaining -le 0) { throw 'Artifact download exceeded five minutes.' }
            $read = $process.StandardOutput.BaseStream.ReadAsync($buffer, 0, $buffer.Length)
            if (-not $read.Wait($remaining)) { throw 'Artifact download exceeded five minutes.' }
            $count = $read.GetAwaiter().GetResult()
            if ($count -eq 0) { break }
            $total += $count
            if ($total -gt 2GB) { throw 'Artifact download exceeded the two-GiB capture limit.' }
            $destination.Write($buffer, 0, $count)
        }
        $remaining = 300000 - [int]$timer.ElapsedMilliseconds
        if ($remaining -le 0 -or -not $process.WaitForExit($remaining)) { throw 'Artifact download exceeded five minutes.' }
        if ($process.ExitCode -ne 0) { throw 'GitHub artifact download failed; partial bytes and stderr are retained.' }
    }
    finally
    {
        if ($null -ne $destination) { $destination.Dispose() }
        if ($processStarted -and -not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(3000) }
        if ($null -ne $stderr)
        {
            $text = $(if ($stderr.Wait(3000)) { $stderr.GetAwaiter().GetResult() } else { 'stderr capture did not complete.' })
            [IO.File]::WriteAllText((Join-Path $output 'artifact-download.stderr.log'), $text, [Text.UTF8Encoding]::new($false))
        }
        $process.Dispose()
    }
}
function Read-Snapshot([string] $Suffix)
{
    $run = Capture-Api "repos/$ExpectedRepository/actions/runs/$RunId/attempts/$RunAttempt" "run-$Suffix"
    $workflow = Capture-Api "repos/$ExpectedRepository/actions/workflows/$WorkflowFile" "workflow-$Suffix"
    $pages = Capture-Api "repos/$ExpectedRepository/actions/runs/$RunId/attempts/$RunAttempt/jobs?per_page=100" "jobs-$Suffix" -Pages
    $record = ConvertTo-CoreGithubRunRecord -Run $run -Workflow $workflow -JobPages $pages `
        -WorkflowFile $WorkflowFile -RunId $RunId -RunAttempt $RunAttempt -ExpectedCommit $ExpectedCommit `
        -CandidateCommitUtc $commitUtc -ExpectedRepository $ExpectedRepository
    $jobs = @($pages | ForEach-Object { $_.jobs } | Sort-Object id)
    $identity = [ordered]@{ record=$record; workflowId=$workflow.id; createdUtc=$run.created_at
        startedUtc=$run.run_started_at; updatedUtc=$run.updated_at
        jobs=@($jobs | Select-Object id,run_id,run_attempt,head_sha,status,conclusion,started_at,completed_at,html_url) }
    return [pscustomobject]@{ Record=$record; Run=$run; JobCount=$jobs.Count
        Fingerprint=($identity | ConvertTo-Json -Depth 16 -Compress) }
}
function Capture-Manifest([string] $Status, [bool] $Validated, [AllowNull()][object] $Record, [string] $Failure)
{
    $files = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
        [ordered]@{path=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    return [ordered]@{schemaVersion=1; kind='GitHubActionsRunMetadata'; status=$Status
        sourceCommit=$ExpectedCommit; toolSourceCommit=$toolCommit; sourceTreeDirty=$false
        repository=$ExpectedRepository; workflowFile=$WorkflowFile; runId=$RunId; runAttempt=$RunAttempt
        startedUtc=$started; completedUtc=[DateTimeOffset]::UtcNow.ToString('O'); record=$Record
        files=$files; failure=$Failure; liveRunMetadataValidated=$Validated
        artifactReceipt=$artifactReceipt; artifactDeliveryIdentityValidated=($Validated -and $artifactValidated)
        checkoutIdentityValidated=$false; artifactIdentityValidated=$false; fixtureIdentityValidated=$false
        executionAuthenticityValidated=$false; allPayloadsValidated=$false; releaseApproved=$false}
}

try
{
    $before = Read-Snapshot 'before'
    if ($captureArtifact)
    {
        $artifactBefore = Capture-Api "repos/$ExpectedRepository/actions/artifacts/$ArtifactId" 'artifact-before'
        $metadataBefore = ConvertTo-CoreGithubArtifactMetadata -Artifact $artifactBefore -Run $before.Run `
            -ArtifactId $ArtifactId -ArtifactName $ArtifactName -RunId $RunId -ExpectedCommit $ExpectedCommit `
            -ExpectedRepository $ExpectedRepository
        Capture-Archive "repos/$ExpectedRepository/actions/artifacts/$ArtifactId/zip"
        $archive = Get-CoreGithubArtifactArchive -Metadata $metadataBefore -ArchivePath (Join-Path $output 'artifact.zip')
    }
    $after = Read-Snapshot 'after'
    if ($before.Fingerprint -cne $after.Fingerprint) { throw 'GitHub attempt or job metadata changed during capture.' }
    if ($captureArtifact)
    {
        $artifactAfter = Capture-Api "repos/$ExpectedRepository/actions/artifacts/$ArtifactId" 'artifact-after'
        $metadataAfter = ConvertTo-CoreGithubArtifactMetadata -Artifact $artifactAfter -Run $after.Run `
            -ArtifactId $ArtifactId -ArtifactName $ArtifactName -RunId $RunId -ExpectedCommit $ExpectedCommit `
            -ExpectedRepository $ExpectedRepository
        if (($metadataBefore | ConvertTo-Json -Compress) -cne ($metadataAfter | ConvertTo-Json -Compress))
        { throw 'GitHub artifact metadata changed during download.' }
        # Recheck actual bytes after the final API snapshot, before retaining a receipt.
        $finalArchive = Get-CoreGithubArtifactArchive -Metadata $metadataAfter -ArchivePath (Join-Path $output 'artifact.zip')
        if (($archive | ConvertTo-Json -Depth 8 -Compress) -cne ($finalArchive | ConvertTo-Json -Depth 8 -Compress))
        { throw 'GitHub artifact archive changed after download.' }
        $artifactReceipt = [ordered]@{ metadata=$metadataAfter; archive=$finalArchive
            runAttempt=$RunAttempt; toolSourceCommit=$toolCommit
            lifecycleCompatibleWithAttempt=$true; checkoutIdentityValidated=$false
            fixtureIdentityValidated=$false; executionAuthenticityValidated=$false
            allPayloadsValidated=$false; releaseApproved=$false }
        $artifactValidated = $true
    }
    if ((& git -C $repositoryRoot rev-parse HEAD).Trim() -cne $toolCommit -or
        @(& git -C $repositoryRoot status --porcelain --untracked-files=normal).Count -ne 0)
    { throw 'Collector tool source changed during capture.' }
    $recordJson = $after.Record | ConvertTo-Json -Depth 8
    $recordBytes = [Text.UTF8Encoding]::new($false).GetBytes($recordJson)
    $binding = [ordered]@{path='record.json'; bytes=$recordBytes.Length
        sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($recordBytes)).ToLowerInvariant()}
    Write-Json 'capture.json' (Capture-Manifest 'validated' $true $binding '')
    # Retain a canonical record only after both actual API snapshots pass. This
    # is a workflow-head metadata record, not evidence of actual checkout/payloads.
    $pendingPath = Join-Path $output '.record.pending'
    $file = [IO.File]::Open($pendingPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $file.Write($recordBytes, 0, $recordBytes.Length) } finally { $file.Dispose() }
    [IO.File]::Move($pendingPath, $recordPath)
    $recordRetained = $true
    Write-Output ([pscustomobject]@{Stage='CoreGithubRunMetadataCapture'; RunId=$RunId; RunAttempt=$RunAttempt
        CandidateCommit=$ExpectedCommit; JobCount=$after.JobCount; RecordPath=$recordPath
        LiveRunMetadataValidated=$true; CheckoutIdentityValidated=$false; ArtifactIdentityValidated=$false
        ArtifactDeliveryIdentityValidated=$artifactValidated
        ExecutionAuthenticityValidated=$false; AllPayloadsValidated=$false; ReleaseApproved=$false})
}
catch
{
    if ($recordRetained) { [IO.File]::Move($recordPath, (Join-Path $output 'rejected-record.json')) }
    Write-Json 'capture-failure.json' (Capture-Manifest 'rejected' $false $null $_.Exception.Message)
    throw
}
