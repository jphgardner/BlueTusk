[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string] $PackageDirectory,
    [string] $SbomDirectory,
    [string] $ExpectedCommit,
    [switch] $AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$workflowFiles = Get-ChildItem -LiteralPath (
    Join-Path $RepositoryRoot '.github/workflows') -Filter '*.yml' -File
$usesPattern = [regex]'(?m)^\s*(?:-\s*)?uses:\s*(?<action>[^@\s]+)@(?<reference>[^\s#]+)'
$unpinned = [System.Collections.Generic.List[string]]::new()
foreach ($workflow in $workflowFiles)
{
    $content = Get-Content -LiteralPath $workflow.FullName -Raw
    foreach ($match in $usesPattern.Matches($content))
    {
        if ($match.Groups['action'].Value.StartsWith('./'))
        {
            continue
        }
        if ($match.Groups['reference'].Value -notmatch '^[0-9a-f]{40}$')
        {
            $unpinned.Add(
                "$($workflow.Name): $($match.Groups['action'].Value)@$($match.Groups['reference'].Value)")
        }
    }
}
if ($unpinned.Count -gt 0)
{
    throw "Workflow actions must use immutable commits: $($unpinned -join '; ')."
}

& (Join-Path $RepositoryRoot 'eng/verify-api-budgets.ps1') -RepositoryRoot $RepositoryRoot

if ([string]::IsNullOrWhiteSpace($PackageDirectory) -and
    [string]::IsNullOrWhiteSpace($SbomDirectory))
{
    Write-Host "Supply-chain source gates verified across $($workflowFiles.Count) workflows."
    return
}
if ([string]::IsNullOrWhiteSpace($PackageDirectory) -or
    [string]::IsNullOrWhiteSpace($SbomDirectory))
{
    throw 'PackageDirectory and SbomDirectory must be supplied together.'
}

$resolvedPackages = (Resolve-Path -LiteralPath (
    Join-Path $RepositoryRoot $PackageDirectory)).Path
$resolvedSbom = (Resolve-Path -LiteralPath (
    Join-Path $RepositoryRoot $SbomDirectory)).Path
$cyclonePath = Join-Path $resolvedSbom 'bluetusk.cdx.json'
$spdxPath = Join-Path $resolvedSbom 'bluetusk.spdx.json'
$provenancePath = Join-Path $resolvedSbom 'build-provenance.json'
foreach ($path in @($cyclonePath, $spdxPath, $provenancePath))
{
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Required supply-chain artifact '$path' is missing."
    }
}

$cyclone = Get-Content -LiteralPath $cyclonePath -Raw | ConvertFrom-Json
$spdx = Get-Content -LiteralPath $spdxPath -Raw | ConvertFrom-Json
$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
if ($cyclone.bomFormat -ne 'CycloneDX' -or $cyclone.specVersion -ne '1.6')
{
    throw 'CycloneDX SBOM must declare version 1.6.'
}
if ($spdx.spdxVersion -ne 'SPDX-2.3' -or $spdx.dataLicense -ne 'CC0-1.0')
{
    throw 'SPDX SBOM must declare SPDX 2.3 and CC0-1.0.'
}
if ($provenance.schemaVersion -ne 1)
{
    throw 'Build provenance must use schema version 1.'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and
    -not [string]::Equals(
        [string]$provenance.sourceCommit,
        $ExpectedCommit,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw (
        "Provenance commit '$($provenance.sourceCommit)' does not match " +
        "expected commit '$ExpectedCommit'.")
}
if ($provenance.sourceTreeDirty -eq $true -and -not $AllowDirty)
{
    throw 'Candidate provenance reports a dirty tracked source tree.'
}

$artifactFiles = Get-ChildItem -LiteralPath $resolvedPackages -File |
    Where-Object { $_.Extension -in @('.nupkg', '.snupkg', '.tgz') } |
    Sort-Object Name
if ($artifactFiles.Count -eq 0)
{
    throw 'No candidate package artifacts were found.'
}

foreach ($artifact in $artifactFiles)
{
    $record = @($provenance.artifacts | Where-Object path -eq $artifact.Name)
    if ($record.Count -ne 1)
    {
        throw "Candidate artifact '$($artifact.Name)' is not uniquely recorded in provenance."
    }
    $actualHash = (
        Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if (-not [string]::Equals(
        $actualHash,
        [string]$record[0].sha256,
        [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Candidate artifact '$($artifact.Name)' does not match its provenance hash."
    }
    $cycloneComponent = @($cyclone.components | Where-Object {
        @($_.properties | Where-Object {
            $_.name -eq 'bluetusk:artifact-path' -and $_.value -eq $artifact.Name
        }).Count -eq 1
    })
    if ($cycloneComponent.Count -ne 1 -or
        @($cycloneComponent[0].hashes | Where-Object {
            $_.alg -eq 'SHA-256' -and
            $_.content -eq $actualHash.ToUpperInvariant()
        }).Count -ne 1)
    {
        throw "Candidate artifact '$($artifact.Name)' is missing from CycloneDX hashes."
    }
    if (@($spdx.packages | Where-Object {
        @($_.checksums | Where-Object {
            $_.algorithm -eq 'SHA256' -and
            $_.checksumValue -eq $actualHash
        }).Count -eq 1
    }).Count -ne 1)
    {
        throw "Candidate artifact '$($artifact.Name)' is missing from SPDX hashes."
    }
}

foreach ($sbomRecord in @($provenance.sboms))
{
    $path = Join-Path $resolvedSbom $sbomRecord.path
    $actualHash = (
        Get-FileHash -LiteralPath $path -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($actualHash -ne $sbomRecord.sha256)
    {
        throw "SBOM '$($sbomRecord.path)' does not match its provenance hash."
    }
}

Write-Host (
    "Supply-chain gates verified for $($artifactFiles.Count) artifacts, " +
    "$(@($cyclone.components).Count) CycloneDX components and " +
    "$(@($spdx.packages).Count) SPDX packages.")
