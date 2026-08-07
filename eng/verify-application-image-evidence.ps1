[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidencePath,
    [string] $ExpectedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$contract = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'application-image-evidence-contract.json') -Raw |
    ConvertFrom-Json
$evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
if ([int]$evidence.schemaVersion -ne [int]$contract.schemaVersion -or
    -not [string]::Equals([string]$evidence.rcVersion, [string]$contract.rcVersion, [StringComparison]::Ordinal) -or
    -not [string]::Equals([string]$evidence.workflow, [string]$contract.workflow, [StringComparison]::Ordinal))
{
    throw 'Application image evidence does not match schema, RC version, or workflow contract.'
}
if ([string]$evidence.commit -notmatch '^[0-9a-f]{40}$')
{
    throw 'Application image evidence commit must be a lowercase full Git SHA.'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and
    -not [string]::Equals([string]$evidence.commit, $ExpectedCommit, [StringComparison]::Ordinal))
{
    throw "Application image evidence is for '$($evidence.commit)', not '$ExpectedCommit'."
}

$references = @{}
foreach ($application in @($contract.requiredApplications))
{
    $applicationEvidence = $evidence.images.PSObject.Properties[[string]$application]
    if ($null -eq $applicationEvidence)
    {
        throw "Application image evidence is missing '$application'."
    }
    foreach ($component in @($contract.requiredComponents))
    {
        $value = [string]$applicationEvidence.Value.PSObject.Properties[[string]$component].Value
        $expectedPrefix = "ghcr.io/jphgardner/bluetusk-$application@sha256:"
        if (-not $value.StartsWith($expectedPrefix, [StringComparison]::Ordinal) -or
            $value.Substring($expectedPrefix.Length) -notmatch '^[0-9a-f]{64}$')
        {
            throw "Image '$application/$component' is not the expected digest-pinned GHCR reference."
        }
        if ($references.ContainsKey($value))
        {
            throw "Image digest reference '$value' is reused across distinct components."
        }
        $references[$value] = "$application/$component"
    }
}

Write-Output (
    "Verified nine immutable application component images for $($evidence.rcVersion) " +
    "at commit $($evidence.commit).")
