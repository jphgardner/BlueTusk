[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidencePath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][ValidateSet('Regression', 'Compatibility')][string] $Kind
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-test-evidence.psm1') -Force
Get-CoreTestManifestReport $EvidencePath $ExpectedCommit $Kind
