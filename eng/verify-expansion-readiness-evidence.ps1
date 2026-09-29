[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Family,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $Commit,
    [Parameter(Mandatory)][string] $Tag,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][object[]] $VerifiedRuns,
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $Token = $env:GITHUB_TOKEN,
    [string] $EvidencePath,
    [string] $ArtifactIndexPath,
    [string] $PolicyPath = (Join-Path $PSScriptRoot 'expansion-release-policy.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($EvidencePath) -ne [string]::IsNullOrWhiteSpace($ArtifactIndexPath))
{
    throw 'Readiness fixtures require both the evidence and artifact index.'
}
$fixtureMode = -not [string]::IsNullOrWhiteSpace($EvidencePath)
if (-not $fixtureMode -and
    ([string]::IsNullOrWhiteSpace($Repository) -or [string]::IsNullOrWhiteSpace($Token)))
{
    throw 'Readiness artifact verification requires GITHUB_REPOSITORY and GITHUB_TOKEN.'
}

$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$qualification = $policy.families.$Family
if ([int]$policy.schemaVersion -ne 1 -or $null -eq $qualification -or
    [string]$policy.readinessWorkflow -cne 'expansion-candidate-readiness.yml')
{
    throw "Missing expansion readiness policy for '$Family'."
}
$expectedWorkflows = [ordered]@{
    capacity = [string]$qualification.capacityWorkflow
    failover = [string]$qualification.failoverWorkflow
    upgrade = [string]$qualification.upgradeWorkflow
}
$readinessRuns = @($VerifiedRuns | Where-Object {
    [string]$_.workflowFile -ceq [string]$policy.readinessWorkflow
})
if ($readinessRuns.Count -ne 1)
{
    throw "Expected one verified candidate-readiness run for '$Family'."
}
$readinessRun = $readinessRuns[0]
$lowerFamily = $Family.ToLowerInvariant()
$lowerCommit = $Commit.ToLowerInvariant()
$readinessName = "expansion-readiness-$lowerFamily-$lowerCommit"
$fixtureArtifacts = @()
if ($fixtureMode)
{
    $index = Get-Content -LiteralPath $ArtifactIndexPath -Raw | ConvertFrom-Json
    if ([int]$index.schemaVersion -ne 1) { throw 'Expected artifact-index fixture schema 1.' }
    $fixtureArtifacts = @($index.artifacts)
}
$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $Token"
    'User-Agent' = 'BlueTusk-release-gate'
    'X-GitHub-Api-Version' = '2022-11-28'
}

function Get-RunArtifacts
{
    param([Parameter(Mandatory)][long] $RunId)
    if ($fixtureMode)
    {
        return @($fixtureArtifacts | Where-Object { [long]$_.workflow_run.id -eq $RunId })
    }
    $all = [Collections.Generic.List[object]]::new()
    $page = 1
    do
    {
        $uri = "https://api.github.com/repos/$Repository/actions/runs/$RunId/artifacts?per_page=100&page=$page"
        $response = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
        foreach ($artifact in @($response.artifacts)) { $all.Add($artifact) }
        $page++
    }
    while ($all.Count -lt [int]$response.total_count -and $page -le 100)
    if ($all.Count -lt [int]$response.total_count)
    {
        throw "Could not enumerate all artifacts for run '$RunId'."
    }
    return @($all.ToArray())
}

function Assert-Artifact
{
    param([object] $Artifact, [object] $Run, [string] $Name)
    if ([string]$Artifact.name -cne $Name -or
        $Artifact.expired -ne $false -or
        [long]$Artifact.size_in_bytes -le 0 -or
        [string]$Artifact.digest -cnotmatch '^sha256:[0-9a-fA-F]{64}$' -or
        [long]$Artifact.workflow_run.id -ne [long]$Run.runId -or
        [string]$Artifact.workflow_run.head_sha -ine $Commit)
    {
        throw "Artifact '$Name' does not match the exact candidate run or has no valid retained digest."
    }
}

$readinessArtifacts = @(Get-RunArtifacts -RunId ([long]$readinessRun.runId) |
    Where-Object { [string]$_.name -ceq $readinessName })
if ($readinessArtifacts.Count -ne 1)
{
    throw "Expected one '$readinessName' artifact on the verified readiness run."
}
$readinessArtifact = $readinessArtifacts[0]
Assert-Artifact -Artifact $readinessArtifact -Run $readinessRun -Name $readinessName
if ([long]$readinessArtifact.size_in_bytes -gt 4194304)
{
    throw 'Readiness artifact ZIP exceeds the 4 MiB release-verifier limit.'
}

if ($fixtureMode)
{
    $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
}
else
{
    $temporaryZip = Join-Path ([IO.Path]::GetTempPath()) (
        'bluetusk-readiness-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try
    {
        $uri = "https://api.github.com/repos/$Repository/actions/artifacts/$($readinessArtifact.id)/zip"
        Invoke-WebRequest -Method Get -Uri $uri -Headers $headers -MaximumRedirection 5 -OutFile $temporaryZip | Out-Null
        $actualDigest = 'sha256:' + (Get-FileHash -LiteralPath $temporaryZip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualDigest -cne ([string]$readinessArtifact.digest).ToLowerInvariant())
        {
            throw 'Downloaded readiness artifact digest does not match GitHub metadata.'
        }
        Add-Type -AssemblyName System.IO.Compression
        $archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
        try
        {
            $entries = @($archive.Entries | Where-Object { [string]$_.FullName -ceq 'readiness.json' })
            if ($entries.Count -ne 1 -or $entries[0].Length -gt 1048576)
            {
                throw 'Readiness artifact must contain one readiness.json of at most 1 MiB.'
            }
            $reader = [IO.StreamReader]::new($entries[0].Open())
            try { $evidence = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
        }
        finally { $archive.Dispose() }
    }
    finally
    {
        if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip -Force }
    }
}

if ([int]$evidence.schemaVersion -ne 1 -or
    [string]$evidence.family -cne $Family -or
    [string]$evidence.candidateCommit -ine $Commit -or
    [string]$evidence.tag -cne $Tag -or
    [string]$evidence.version -cne $Version -or
    [long]$evidence.readinessRun.id -ne [long]$readinessRun.runId -or
    [int]$evidence.readinessRun.attempt -ne [int]$readinessRun.runAttempt)
{
    throw "Readiness artifact does not identify the exact '$Family' release candidate."
}

$evidenceEntries = @($evidence.qualificationEvidence)
if ($evidenceEntries.Count -ne $expectedWorkflows.Count)
{
    throw "Readiness artifact must bind all three '$Family' qualification roles."
}
foreach ($role in $expectedWorkflows.Keys)
{
    $workflowFile = $expectedWorkflows[$role]
    $entries = @($evidenceEntries | Where-Object { [string]$_.role -ceq $role })
    $runs = @($VerifiedRuns | Where-Object { [string]$_.workflowFile -ceq $workflowFile })
    if ($entries.Count -ne 1 -or $runs.Count -ne 1)
    {
        throw "Readiness artifact is missing a unique '$role' qualification run."
    }
    $entry = $entries[0]
    $run = $runs[0]
    $expectedName = "expansion-$lowerFamily-$role-$lowerCommit"
    if ([string]$entry.workflowFile -cne $workflowFile -or
        [long]$entry.runId -ne [long]$run.runId -or
        [int]$entry.runAttempt -ne [int]$run.runAttempt -or
        [string]$entry.artifactName -cne $expectedName -or
        [string]$entry.artifactDigest -cnotmatch '^sha256:[0-9a-fA-F]{64}$')
    {
        throw "Readiness '$role' evidence is not bound to the verified '$Family' candidate run."
    }
    $artifacts = @(Get-RunArtifacts -RunId ([long]$run.runId) |
        Where-Object { [string]$_.name -ceq [string]$entry.artifactName })
    if ($artifacts.Count -ne 1)
    {
        throw "Expected one retained '$role' artifact on the verified qualification run."
    }
    Assert-Artifact -Artifact $artifacts[0] -Run $run -Name ([string]$entry.artifactName)
    if ([string]$artifacts[0].digest -ine [string]$entry.artifactDigest)
    {
        throw "Readiness '$role' artifact digest differs from the verified run artifact."
    }
}

Write-Output (
    "Verified '$Family' readiness artifact #$($readinessArtifact.id) for " +
    "candidate $Commit and three exact qualification run artifacts.")
