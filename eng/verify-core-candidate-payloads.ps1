[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][DateTimeOffset] $CandidateCommitUtc,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-payloads.psm1')
Get-CoreCandidatePayloadReport -EvidencePath $EvidencePath -ExpectedCommit $ExpectedCommit `
    -CandidateCommitUtc $CandidateCommitUtc -ExpectedRepository $ExpectedRepository
Write-Information 'All fourteen Core payload roles and approval schemas verified. Fixture/execution authenticity and live GitHub identity are NOT verified; this is NOT release approval.' -InformationAction Continue
