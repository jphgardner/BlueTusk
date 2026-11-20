[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long] $BuildRunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $EnduranceDirectory,
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $Token = $env:GITHUB_TOKEN
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-package-release-github.psm1') -Force
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$evidenceRoot = Join-Path $repositoryRoot 'artifacts/core-endurance-package-evidence'
Initialize-CorePackageEvidence -Repository $Repository -Token $Token -Commit $ExpectedCommit
$build = Get-CorePackageEvidenceRun -WorkflowFile build.yml -RunId $BuildRunId
$name = "v1.2-core-packages-$ExpectedCommit-$BuildRunId-$($build.Record.runAttempt)"
$artifact = Receive-CorePackageEvidenceArtifact -EvidenceRun $build -Name $name -Destination $evidenceRoot
$packageArguments = @{
    EvidenceRoot = $evidenceRoot; ExpectedCommit = $ExpectedCommit
    ReleaseTrack = 'Core'; CoreReleaseVersion = '1.2.0'
}
& (Join-Path $PSScriptRoot 'verify-v1-package-evidence.ps1') @packageArguments

$destination = Join-Path $EnduranceDirectory 'candidate-sbom'
$null = New-Item -ItemType Directory -Path $destination -Force
$provenance = Join-Path $destination 'build-provenance.json'
[IO.File]::Copy((Join-Path $evidenceRoot 'sbom/build-provenance.json'), [IO.Path]::GetFullPath($provenance), $false)
$artifact | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $EnduranceDirectory 'candidate-package-artifact.json') -Encoding utf8NoBOM
