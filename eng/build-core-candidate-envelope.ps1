[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][DateTimeOffset] $CandidateCommitUtc,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1') -Force
$root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$artifactsPrefix = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $root.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase))
{ throw 'Generated core envelopes must remain below the repository artifacts directory.' }
$output = Join-Path $root 'candidate.json'
if (Test-Path -LiteralPath $output) { throw 'Candidate envelope already exists; choose a new capture directory.' }
$contract = Get-CoreCandidateContract
$runsFile = Resolve-CoreEvidenceFile $root 'workflow-runs.json'
$runs = Read-CoreEvidenceJson $runsFile -Array
$artifacts = @(foreach ($role in $contract.requiredArtifactRoles)
{
    $binding = $contract.artifactBindings.PSObject.Properties[$role].Value
    $path = Resolve-CoreEvidenceFile $root $binding.path
    $runId = $null
    if ($null -ne $binding.workflowFile)
    {
        $matches = @($runs | Where-Object workflowFile -ceq $binding.workflowFile)
        if ($matches.Count -ne 1) { throw "Missing or duplicated producer workflow for '$role'." }
        $runId = $matches[0].runId
    }
    [ordered]@{ role = $role; path = $binding.path; sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $path).Length; workflowFile = $binding.workflowFile; runId = $runId }
})
$approvalContract = Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'v1-approval-evidence-contract.json')
$approvals = @(foreach ($id in $approvalContract.gates.id)
{
    $relativePath = "approvals/$id.json"
    $path = Resolve-CoreEvidenceFile $root $relativePath
    [ordered]@{ id = $id; path = $relativePath; sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $path).Length }
})
$evidence = [ordered]@{ schemaVersion = 4; candidateCommit = $ExpectedCommit; scope = 'Core'; releaseVersion = '1.2.0'
    workflowRuns = @($runs); artifacts = $artifacts; approvals = $approvals }
# Validate before retaining anything that could be confused with a qualified bundle.
# A private temporary envelope lets the existing workflow reader consume the same
# records as the in-memory join. It is removed after validation, never published.
$temporaryPath = Join-Path $root ('.core-envelope-' + [Guid]::NewGuid().ToString('N') + '.json')
try
{
    $json = $evidence | ConvertTo-Json -Depth 16
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $temporary = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $temporary.Write($bytes, 0, $bytes.Length) } finally { $temporary.Dispose() }
    $report = Get-CoreCandidateBindingReport -EvidencePath $temporaryPath `
        -ExpectedCommit $ExpectedCommit -CandidateCommitUtc $CandidateCommitUtc -ExpectedRepository $ExpectedRepository
    $stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    Write-Output $report
    Write-Information "Retained immutable envelope: $output. Bindings only, NOT release approval." -InformationAction Continue
}
finally
{
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath }
}
