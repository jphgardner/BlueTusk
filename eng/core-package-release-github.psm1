Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-github-run-capture.psm1')
Import-Module (Join-Path $PSScriptRoot 'core-github-artifact-capture.psm1')

function Invoke-CorePackageEvidenceApi([string] $Path)
{
    $response = Invoke-WebRequest -Uri "https://api.github.com/repos/$script:Repository/$Path" -Headers $script:Headers
    $response.Content | ConvertFrom-Json -Depth 64 -DateKind String
}

function Initialize-CorePackageEvidence
{
    param(
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Token,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $Commit
    )

    $root = Split-Path $PSScriptRoot -Parent
    $head = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cne $Commit) { throw 'Evidence collection requires the exact candidate checkout.' }
    $trackedChanges = @(& git -C $root status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or $trackedChanges.Count -ne 0) { throw 'Evidence collection requires a clean tracked checkout.' }
    $timestamp = (& git -C $root show -s --format=%cI HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the candidate commit timestamp.' }
    $script:CandidateCommitUtc = [DateTimeOffset]::Parse($timestamp, [Globalization.CultureInfo]::InvariantCulture)
    $script:Repository = $Repository
    $script:Commit = $Commit
    $script:Headers = @{
        Accept = 'application/vnd.github+json'
        Authorization = "Bearer $Token"
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent' = 'BlueTusk-Core-package-readiness'
    }
}

function Get-CorePackageEvidenceRun
{
    param(
        [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]+\.yml$')][string] $WorkflowFile,
        [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long] $RunId
    )

    $headRun = Invoke-CorePackageEvidenceApi "actions/runs/$RunId"
    $attempt = $headRun.run_attempt
    $run = Invoke-CorePackageEvidenceApi "actions/runs/$RunId/attempts/$attempt"
    $workflow = Invoke-CorePackageEvidenceApi "actions/workflows/$WorkflowFile"
    $pages = [Collections.Generic.List[object]]::new()
    $received = 0
    for ($page = 1; $page -le 100; $page++)
    {
        $result = Invoke-CorePackageEvidenceApi "actions/runs/$RunId/attempts/$attempt/jobs?per_page=100&page=$page"
        $pages.Add($result)
        $received += $result.jobs.Count
        if ($received -ge $result.total_count -or $result.jobs.Count -eq 0) { break }
    }

    $arguments = @{
        Run = $run; Workflow = $workflow; JobPages = $pages.ToArray()
        WorkflowFile = $WorkflowFile; RunId = $RunId; RunAttempt = $attempt
        ExpectedCommit = $script:Commit; CandidateCommitUtc = $script:CandidateCommitUtc
        ExpectedRepository = $script:Repository
    }
    $record = ConvertTo-CoreGithubRunRecord @arguments
    return [pscustomobject]@{ Run = $run; Record = $record }
}

function Receive-CorePackageEvidenceArtifact
{
    param(
        [Parameter(Mandatory)][object] $EvidenceRun,
        [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_.-]+$')][string] $Name,
        [Parameter(Mandatory)][string] $Destination
    )

    $runId = $EvidenceRun.Record.runId
    $listing = Invoke-CorePackageEvidenceApi "actions/runs/$runId/artifacts?name=$([Uri]::EscapeDataString($Name))&per_page=100"
    $matches = @($listing.artifacts | Where-Object { $_.name -ceq $Name })
    if ($listing.total_count -ne 1 -or $matches.Count -ne 1) { throw "Run $runId must have exactly one '$Name' artifact." }

    $id = $matches[0].id
    $artifact = Invoke-CorePackageEvidenceApi "actions/artifacts/$id"
    $arguments = @{
        Artifact = $artifact; Run = $EvidenceRun.Run; ArtifactId = $id; ArtifactName = $Name
        RunId = $runId; ExpectedCommit = $script:Commit; ExpectedRepository = $script:Repository
    }
    $metadata = ConvertTo-CoreGithubArtifactMetadata @arguments

    $path = [IO.Path]::GetFullPath($Destination)
    $archivePath = "$path.zip"
    if ((Test-Path -LiteralPath $path) -or (Test-Path -LiteralPath $archivePath))
    {
        throw "Evidence destination '$path' already exists."
    }
    $null = [IO.Directory]::CreateDirectory((Split-Path $path -Parent))
    Invoke-WebRequest -Uri $metadata.archiveDownloadUrl -Headers $script:Headers -TimeoutSec 300 -OutFile $archivePath
    $null = Get-CoreGithubArtifactArchive -Metadata $metadata -ArchivePath $archivePath
    [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $path)
    Remove-Item -LiteralPath $archivePath
    return $metadata
}

Export-ModuleMember -Function Initialize-CorePackageEvidence, Get-CorePackageEvidenceRun, Receive-CorePackageEvidenceArtifact
