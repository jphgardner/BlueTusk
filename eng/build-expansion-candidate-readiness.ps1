<#
.SYNOPSIS
Bind and re-verify one expansion family's exact-candidate capacity, failover and upgrade runs.
.DESCRIPTION
Runs only inside the manual expansion-candidate-readiness.yml workflow, whose own run ID and
attempt become the readiness identity. For each role workflow named in
eng/expansion-release-policy.json it selects the latest successful workflow_dispatch run whose
head_sha is the candidate and which retains the family's unexpired
expansion-<family>-<role>-<sha> artifact (the rule the release gate also applies), validates the
run attempt, its complete job set and the artifact metadata, downloads the artifact, checks its
byte count and GitHub SHA-256 digest, inventories and extracts it safely, and re-runs the
family's reviewed archived-evidence verifier on it. After all three pass it re-reads the
selection and metadata, then writes readiness.json (the schema-1 record checked by
verify-expansion-readiness-evidence.ps1) and readiness-bindings.json with the receipts.

Any missing, failed, foreign, non-dispatch, expired, changed or digest-mismatched role, or a
role without a reviewed verifier, fails closed before anything is written. The record does not
approve a release; the release gate re-checks it against live GitHub data.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Events', 'Jobs', 'Documents', 'Schema', 'Projections', 'Search', 'Sql', 'Studio',
        'Edge', 'Workflows', IgnoreCase = $false)]
    [string] $Family,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $CandidateCommit,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [string] $Repository = $env:GITHUB_REPOSITORY,

    [string] $Token = $env:GH_TOKEN
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')
Import-Module (Join-Path $PSScriptRoot 'core-github-artifact-capture.psm1')
Import-Module (Join-Path $PSScriptRoot 'expansion-candidate-evidence.psm1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-JsonFile([string] $Path, [object] $Value)
{
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 32) + "`n")
    $file = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $file.Write($bytes, 0, $bytes.Length) } finally { $file.Dispose() }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$governance = Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'v1-github-governance.json')
Require ($governance.repository -is [string] -and $Repository -ceq $governance.repository) (
    'Expansion readiness must query the governed repository.')
Require (-not [string]::IsNullOrWhiteSpace($Token)) 'Expansion readiness requires a GitHub token with actions:read.'

# The readiness identity comes only from the manual readiness workflow run itself.
$readinessWorkflowPattern = '^' + [regex]::Escape($Repository) + '/\.github/workflows/expansion-candidate-readiness\.yml@'
Require ($env:GITHUB_ACTIONS -ceq 'true' -and $env:GITHUB_EVENT_NAME -ceq 'workflow_dispatch' -and
    [string]$env:GITHUB_WORKFLOW_REF -cmatch $readinessWorkflowPattern) (
    'Expansion readiness records are produced only by the manual expansion-candidate-readiness.yml workflow.')
Require ([string]$env:GITHUB_RUN_ID -cmatch '^[1-9][0-9]{0,18}$' -and
    [string]$env:GITHUB_RUN_ATTEMPT -cmatch '^[1-9][0-9]{0,8}$') 'The readiness workflow run identity is invalid.'
$readinessRunId = [long]$env:GITHUB_RUN_ID
$readinessRunAttempt = [int]$env:GITHUB_RUN_ATTEMPT
Require ($env:GITHUB_SHA -ceq $CandidateCommit) (
    'The readiness run head differs from the candidate; the release gate binds readiness by head_sha.')

$separator = [IO.Path]::DirectorySeparatorChar
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd($separator) + $separator
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) }
    else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory)) }
$downloads = Join-Path $artifactsRoot "expansion-readiness-downloads/$readinessRunId-$readinessRunAttempt"
Require ($output.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -and
    -not (Test-Path -LiteralPath $output) -and -not (Test-Path -LiteralPath $downloads)) (
    'Choose fresh readiness directories below the repository artifacts directory.')

$head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
Require ($LASTEXITCODE -eq 0 -and $head -ceq $CandidateCommit) 'The readiness checkout is not the exact candidate.'
Require (@(& git -C $repositoryRoot status --porcelain --untracked-files=normal).Count -eq 0) (
    'Expansion readiness requires a clean candidate checkout.')
$candidateTimeText = (& git -C $repositoryRoot show --no-patch --format=%cI "$CandidateCommit^{commit}").Trim()
Require ($LASTEXITCODE -eq 0) 'The candidate commit time is unavailable.'
$candidateUtc = [DateTimeOffset]::Parse($candidateTimeText, [Globalization.CultureInfo]::InvariantCulture)

& (Join-Path $PSScriptRoot 'verify-expansion-release-policy.ps1') -Family $Family -RequireArmed | Out-Null
$contract = Get-ExpansionReadinessContract -Family $Family -Commit $CandidateCommit
$identity = Get-ExpansionReleaseIdentity -Family $Family -RepositoryRoot $repositoryRoot
$null = [IO.Directory]::CreateDirectory((Join-Path $downloads 'api'))

$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $Token"
    'User-Agent' = 'BlueTusk-expansion-readiness'
    'X-GitHub-Api-Version' = '2022-11-28'
}
$apiCaptures = 0
function Invoke-GitHubJson([string] $Path)
{
    $uri = "https://api.github.com/repos/$Repository/$Path"
    try { $response = Invoke-WebRequest -Method Get -Uri $uri -Headers $headers -MaximumRedirection 0 }
    catch { throw "GitHub API query '$Path' failed: $($_.Exception.Message)" }
    # Parse through the duplicate-property-safe evidence reader; timestamps stay explicit text.
    $script:apiCaptures++
    $capture = Join-Path $downloads ('api/{0:d4}.json' -f $script:apiCaptures)
    [IO.File]::WriteAllText($capture, [string]$response.Content, [Text.UTF8Encoding]::new($false))
    return Read-CoreEvidenceJson $capture
}
function Get-GitHubPages([string] $Path, [string] $Property)
{
    $pages = [Collections.Generic.List[object]]::new()
    $count = 0L
    $total = $null
    for ($page = 1; $page -le 10; $page++)
    {
        $join = if ($Path.Contains('?')) { '&' } else { '?' }
        $response = Invoke-GitHubJson "$Path${join}per_page=100&page=$page"
        $declared = $response.PSObject.Properties['total_count']
        Require ($null -ne $declared -and ($declared.Value -is [int] -or $declared.Value -is [long]) -and
            $declared.Value -ge 0) "GitHub listing '$Path' has no valid total."
        if ($null -eq $total) { $total = [long]$declared.Value }
        Require ([long]$declared.Value -eq $total) "GitHub listing '$Path' changed during pagination."
        $items = @($response.PSObject.Properties[$Property].Value)
        $pages.Add($response)
        $count += $items.Count
        if ($count -ge $total -or $items.Count -eq 0) { break }
    }
    Require ($count -eq $total) "GitHub listing '$Path' is incomplete."
    return ,$pages.ToArray()
}
function Get-RunArtifacts([long] $RunId, [string] $Name)
{
    $pages = Get-GitHubPages "actions/runs/$RunId/artifacts?name=$([Uri]::EscapeDataString($Name))" 'artifacts'
    return @($pages | ForEach-Object { @($_.artifacts) })
}
function Select-RoleRun([object] $Role)
{
    $pages = Get-GitHubPages (
        "actions/workflows/$([Uri]::EscapeDataString($Role.WorkflowFile))/runs?head_sha=$CandidateCommit&event=workflow_dispatch") 'workflow_runs'
    $runs = @($pages | ForEach-Object { @($_.workflow_runs) } | ForEach-Object {
        ConvertTo-ExpansionRunSummary -Run $_ -WorkflowFile $Role.WorkflowFile })
    return Select-ExpansionQualificationRun -Runs $runs -WorkflowFile $Role.WorkflowFile -Commit $CandidateCommit `
        -ArtifactName $Role.ArtifactName -GetRunArtifacts ${function:Get-RunArtifacts}
}
function Get-ArtifactMetadata([object] $Selection, [object] $Role)
{
    $run = $Selection.Run
    $attempt = Invoke-GitHubJson "actions/runs/$($run.runId)/attempts/$($run.runAttempt)"
    $artifact = Invoke-GitHubJson "actions/artifacts/$([long]$Selection.Artifact.id)"
    $metadata = ConvertTo-CoreGithubArtifactMetadata -Artifact $artifact -Run $attempt `
        -ArtifactId ([long]$Selection.Artifact.id) -ArtifactName $Role.ArtifactName -RunId ([long]$run.runId) `
        -ExpectedCommit $CandidateCommit -ExpectedRepository $Repository
    return [pscustomobject]@{ Attempt = $attempt; Metadata = $metadata }
}
function Assert-ExtractedArchive([string] $Root, [object] $Archive)
{
    $members = @{}
    foreach ($member in $Archive.members) { $members[[string]$member.path] = $member }
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -Force -File)
    Require ($files.Count -eq $members.Count) 'Extracted artifact file count differs from its verified archive inventory.'
    foreach ($file in $files)
    {
        Require (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Extracted artifact contains a link.'
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        Require ($members.ContainsKey($relative) -and [long]$members[$relative].bytes -eq $file.Length -and
            [string]$members[$relative].sha256 -ceq (Hash $file.FullName)) (
            "Extracted artifact member '$relative' differs from its verified archive inventory.")
    }
}

$receipts = [Collections.Generic.List[object]]::new()
Push-Location -LiteralPath $repositoryRoot
try
{
    foreach ($role in $contract.Roles)
    {
        $verifier = Get-ExpansionRoleVerifier -Family $Family -Role $role.Role
        Require (Test-Path -LiteralPath (Join-Path $repositoryRoot ".github/workflows/$($role.WorkflowFile)") -PathType Leaf) (
            "The candidate has no '$($role.WorkflowFile)' $($role.Role) workflow.")
        $selection = Select-RoleRun $role
        $run = $selection.Run
        $attempt = Invoke-GitHubJson "actions/runs/$($run.runId)/attempts/$($run.runAttempt)"
        $workflow = Invoke-GitHubJson "actions/workflows/$([Uri]::EscapeDataString($role.WorkflowFile))"
        $jobPages = Get-GitHubPages "actions/runs/$($run.runId)/attempts/$($run.runAttempt)/jobs" 'jobs'
        $record = ConvertTo-ExpansionGithubRunRecord -Run $attempt -Workflow $workflow -JobPages $jobPages `
            -WorkflowFile $role.WorkflowFile -RunId ([long]$run.runId) -RunAttempt ([int]$run.runAttempt) `
            -ExpectedCommit $CandidateCommit -CandidateCommitUtc $candidateUtc -ExpectedRepository $Repository
        $before = Get-ArtifactMetadata $selection $role

        $archivePath = Join-Path $downloads "$($role.Role).zip"
        $uri = "https://api.github.com/repos/$Repository/actions/artifacts/$($before.Metadata.artifactId)/zip"
        try { Invoke-WebRequest -Method Get -Uri $uri -Headers $headers -MaximumRedirection 5 -OutFile $archivePath | Out-Null }
        catch { throw "The '$($role.Role)' artifact download failed: $($_.Exception.Message)" }
        $archive = Get-CoreGithubArtifactArchive -Metadata $before.Metadata -ArchivePath $archivePath

        # Each verifier constrains its evidence root to the workflow's own artifacts directory.
        $relativeRoot = "artifacts/$([IO.Path]::GetFileNameWithoutExtension($role.WorkflowFile))/readiness-$readinessRunId-$readinessRunAttempt"
        $evidenceRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativeRoot))
        Require ($evidenceRoot.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -and
            -not (Test-Path -LiteralPath $evidenceRoot)) "The '$($role.Role)' verification directory is not fresh."
        [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $evidenceRoot)
        Assert-ExtractedArchive $evidenceRoot $archive

        $arguments = @{}
        foreach ($key in $verifier.Arguments.Keys) { $arguments[$key] = $verifier.Arguments[$key] }
        $arguments.ExpectedCommit = $CandidateCommit
        $arguments[$verifier.EvidenceParameter] = $relativeRoot
        if (-not [string]::IsNullOrEmpty([string]$verifier.ProducerRunParameter))
        {
            $arguments[[string]$verifier.ProducerRunParameter] = [long]$run.runId
        }
        $verifierPath = Join-Path $PSScriptRoot $verifier.Script
        $verifierOutput = @(& $verifierPath @arguments | ForEach-Object { [string]$_ })
        Assert-ExtractedArchive $evidenceRoot $archive

        $receipts.Add([pscustomobject][ordered]@{
            Role = $role.Role; WorkflowFile = $role.WorkflowFile; ArtifactName = $role.ArtifactName
            RunId = [long]$run.runId; RunAttempt = [int]$run.runAttempt; Record = $record
            Metadata = $before.Metadata; Archive = $archive
            ArtifactSha256 = [string]$before.Metadata.sha256
            Verifier = [ordered]@{
                script = "eng/$($verifier.Script)"
                sha256 = Hash $verifierPath
                arguments = @($arguments.Keys | Sort-Object | ForEach-Object { "-$_ $($arguments[$_])" })
                output = @($verifierOutput | Select-Object -Last 5)
            }
        })
    }

    # Nothing may change between selection and record: a newer run, another attempt, or
    # altered artifact metadata would make the release gate bind different evidence.
    foreach ($receipt in $receipts)
    {
        $role = @($contract.Roles | Where-Object { $_.Role -ceq $receipt.Role })[0]
        $again = Select-RoleRun $role
        $after = Get-ArtifactMetadata $again $role
        Require ([long]$again.Run.runId -eq $receipt.RunId -and [int]$again.Run.runAttempt -eq $receipt.RunAttempt -and
            (($after.Metadata | ConvertTo-Json -Compress) -ceq ($receipt.Metadata | ConvertTo-Json -Compress))) (
            "The '$($receipt.Role)' qualification run or artifact changed during readiness; re-run readiness.")
        Require ((Get-CoreGithubArtifactArchive -Metadata $after.Metadata -ArchivePath (
            Join-Path $downloads "$($receipt.Role).zip")).sha256 -ceq $receipt.ArtifactSha256) (
            "The '$($receipt.Role)' downloaded archive changed during readiness.")
    }
}
finally { Pop-Location }

Require ((& git -C $repositoryRoot rev-parse HEAD).Trim() -ceq $CandidateCommit -and
    @(& git -C $repositoryRoot status --porcelain --untracked-files=normal).Count -eq 0) (
    'The readiness checkout changed while qualification evidence was verified.')

$readiness = New-ExpansionReadinessRecord -Contract $contract -Tag $identity.Tag -Version $identity.Version `
    -ReadinessRunId $readinessRunId -ReadinessRunAttempt $readinessRunAttempt -Bindings @($receipts)
$bindings = [ordered]@{
    schemaVersion = 1
    kind = 'ExpansionCandidateReadinessBindings'
    family = $Family
    candidateCommit = $CandidateCommit
    candidateCommitUtc = $candidateTimeText
    tag = $identity.Tag
    version = $identity.Version
    repository = $Repository
    readinessRun = [ordered]@{ id = $readinessRunId; attempt = $readinessRunAttempt }
    policySha256 = Hash (Join-Path $PSScriptRoot 'expansion-release-policy.json')
    roles = @($receipts | ForEach-Object {
        [ordered]@{
            role = $_.Role
            workflowFile = $_.WorkflowFile
            run = $_.Record
            artifact = [ordered]@{
                id = $_.Metadata.artifactId; name = $_.ArtifactName; bytes = $_.Metadata.bytes
                sha256 = $_.ArtifactSha256; createdUtc = $_.Metadata.createdUtc
                updatedUtc = $_.Metadata.updatedUtc; expiresUtc = $_.Metadata.expiresUtc
                memberCount = @($_.Archive.members).Count; uncompressedBytes = $_.Archive.uncompressedBytes
            }
            verifier = $_.Verifier
        }
    })
    productionQualified = $false
    releaseApproved = $false
}
$null = [IO.Directory]::CreateDirectory($output)
Write-JsonFile (Join-Path $output 'readiness.json') $readiness
Write-JsonFile (Join-Path $output 'readiness-bindings.json') $bindings
Write-Output (
    "Bound $($receipts.Count) re-verified '$Family' qualification artifacts for candidate $CandidateCommit " +
    "($($identity.Tag)) in readiness run $readinessRunId attempt $readinessRunAttempt; release approval remains separate.")
