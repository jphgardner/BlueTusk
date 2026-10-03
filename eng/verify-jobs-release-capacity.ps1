[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Run', 'Verify')]
    [string] $Mode = 'Verify',
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit,
    [string] $EvidenceRoot = 'artifacts/jobs-release-capacity',
    # Archived re-verification only: the GitHub run that produced the evidence.
    [ValidateRange(1, [long]::MaxValue)]
    [long] $ProducerRunId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$arguments = @{
    Mode = $Mode
    ExpectedCommit = $ExpectedCommit
    EvidenceRoot = $EvidenceRoot
}
if ($PSBoundParameters.ContainsKey('ProducerRunId'))
{
    $arguments.ProducerRunId = $ProducerRunId
}
& (Join-Path $PSScriptRoot 'verify-ecosystem-performance.ps1') @arguments -Product Jobs
