[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Run', 'Verify')]
    [string] $Mode = 'Verify',
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit,
    [string] $EvidenceRoot = 'artifacts/jobs-release-capacity'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'verify-ecosystem-performance.ps1') `
    -Mode $Mode `
    -ExpectedCommit $ExpectedCommit `
    -EvidenceRoot $EvidenceRoot `
    -Product Jobs
