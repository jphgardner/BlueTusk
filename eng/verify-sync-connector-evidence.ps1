[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'core-test-evidence.psm1') -Force
$payload = Read-CoreEvidenceJson $EvidencePath
Get-CoreTestShardReport $EvidencePath $ExpectedCommit 'SyncConnectors' $payload.environmentId
