[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][DateTimeOffset] $CandidateCommitUtc,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk',
    [switch] $UseLocalExecution,
    [switch] $VerifyPayloads
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1')
$root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$artifactsPrefix = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $root.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase))
{ throw 'Generated core envelopes must remain below the repository artifacts directory.' }
$output = Join-Path $root 'candidate.json'
if (Test-Path -LiteralPath $output) { throw 'Candidate envelope already exists; choose a new capture directory.' }
$contract = Get-CoreCandidateContract
$runsFile = Resolve-CoreEvidenceFile $root $(if ($UseLocalExecution) { 'producer-runs.json' } else { 'workflow-runs.json' })
$runs = Read-CoreEvidenceJson $runsFile -Array
if ($UseLocalExecution)
{
    Import-Module (Join-Path $PSScriptRoot 'core-execution-evidence.psm1')
    $executionReport = Get-CoreExecutionBindingReport -Records $runs -EvidenceRoot $root -ExpectedCommit $ExpectedCommit `
        -CandidateCommitUtc $CandidateCommitUtc -ExpectedRepository $ExpectedRepository
}
$artifacts = @(foreach ($role in $contract.requiredArtifactRoles)
{
    $binding = $contract.artifactBindings.PSObject.Properties[$role].Value
    $path = Resolve-CoreEvidenceFile $root $binding.path
    $runId = $null
    if ($null -ne $binding.workflowFile)
    {
        if ($UseLocalExecution) { $runId = $executionReport.Bindings[$binding.workflowFile].Identifier }
        else
        {
            $matches = @($runs | Where-Object workflowFile -ceq $binding.workflowFile)
            if ($matches.Count -ne 1) { throw "Missing or duplicated producer workflow for '$role'." }
            $runId = $matches[0].runId
        }
    }
    $record = [ordered]@{ role = $role; path = $binding.path; sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $path).Length }
    if ($UseLocalExecution) { $record.producerFile = $binding.workflowFile; $record.executionId = $runId }
    else { $record.workflowFile = $binding.workflowFile; $record.runId = $runId }
    $record
})
$approvalContract = Read-CoreEvidenceJson (Join-Path $PSScriptRoot 'v1-approval-evidence-contract.json')
$approvals = @(foreach ($id in $approvalContract.gates.id)
{
    $relativePath = "approvals/$id.json"
    $path = Resolve-CoreEvidenceFile $root $relativePath
    [ordered]@{ id = $id; path = $relativePath; sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $path).Length }
})
$evidence = [ordered]@{ schemaVersion = $(if ($UseLocalExecution) { 5 } else { 4 })
    candidateCommit = $ExpectedCommit; scope = 'Core'; releaseVersion = '1.2.0' }
if ($UseLocalExecution) { $evidence.producerRuns = @($runs) } else { $evidence.workflowRuns = @($runs) }
$evidence.artifacts = $artifacts
$evidence.approvals = $approvals
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
    if ($VerifyPayloads)
    {
        Import-Module (Join-Path $PSScriptRoot 'core-candidate-payloads.psm1')
        $report = Get-CoreCandidatePayloadReport -EvidencePath $temporaryPath `
            -ExpectedCommit $ExpectedCommit -CandidateCommitUtc $CandidateCommitUtc -ExpectedRepository $ExpectedRepository
    }
    $stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    Write-Output $report
    Write-Information "Retained immutable envelope: $output. NOT release approval; inspect the report's verification flags." -InformationAction Continue
}
finally
{
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
}
