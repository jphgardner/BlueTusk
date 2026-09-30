[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][DateTimeOffset] $CandidateCommitUtc,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $ExpectedRepository = 'jphgardner/BlueTusk'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1') -Force
$path = (Resolve-Path -LiteralPath $EvidencePath).Path
$report = Get-CoreCandidateBindingReport -EvidencePath $path -ExpectedCommit $ExpectedCommit `
    -CandidateCommitUtc $CandidateCommitUtc -ExpectedRepository $ExpectedRepository
Write-Output $report
Write-Information 'Core evidence bindings and approval schemas verified. Payload qualification and remote identity are NOT verified; this is NOT release approval.' -InformationAction Continue
