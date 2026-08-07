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
$unpinnedImages = [System.Collections.Generic.List[string]]::new()
$serviceImagePattern = [regex]'(?m)^\s*image:\s*(?<image>[^\r\n#]+?)\s*(?:#.*)?$'
$builtImageDigestPattern = [regex](
    '^\$\{\{\s*steps\.image\.outputs\.name\s*\}\}@' +
    '\$\{\{\s*steps\.build\.outputs\.digest\s*\}\}$')
$knownWorkflowImagePattern = [regex](
    '(?<![A-Za-z0-9._/-])' +
    '(?<image>(?:' +
    'nats|' +
    'opensearchproject/opensearch|' +
    'otel/opentelemetry-collector-contrib|' +
    'postgres|' +
    'prom/prometheus|' +
    'redis' +
    '):[A-Za-z0-9._-]+)' +
    '(?<digest>@sha256:[0-9a-f]{64})?')
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
    foreach ($match in $serviceImagePattern.Matches($content))
    {
        $image = $match.Groups['image'].Value.Trim()
        $isBuiltDigest = $builtImageDigestPattern.IsMatch($image) -and
            $content -match '(?m)^\s*id:\s*image\s*$' -and
            $content -match '(?m)^\s*id:\s*build\s*$' -and
            $content -match 'docker/build-push-action@[0-9a-f]{40}'
        if ($image -notmatch '@sha256:[0-9a-f]{64}$' -and -not $isBuiltDigest)
        {
            $unpinnedImages.Add(
                "$($workflow.Name): $image")
        }
    }
    foreach ($match in $knownWorkflowImagePattern.Matches($content))
    {
        if (-not $match.Groups['digest'].Success)
        {
            $unpinnedImages.Add(
                "$($workflow.Name): $($match.Groups['image'].Value)")
        }
    }
}
if ($unpinned.Count -gt 0)
{
    throw "Workflow actions must use immutable commits: $($unpinned -join '; ')."
}
if ($unpinnedImages.Count -gt 0)
{
    $distinctImages = $unpinnedImages | Sort-Object -Unique
    throw "Workflow container images must use immutable SHA-256 digests: $($distinctImages -join '; ')."
}

& (Join-Path $RepositoryRoot 'eng/verify-api-budgets.ps1') -RepositoryRoot $RepositoryRoot

if ([string]::IsNullOrWhiteSpace($PackageDirectory) -and
    [string]::IsNullOrWhiteSpace($SbomDirectory))
{
    Write-Host (
        "Supply-chain source gates verified across $($workflowFiles.Count) workflows " +
        'with immutable actions and CI container images.')
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
if ([string]$provenance.sourceCommit -notmatch '^[0-9a-fA-F]{40}$')
{
    throw 'Build provenance must identify a full source commit.'
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
if (@($cyclone.metadata.properties | Where-Object {
            [string]$_.name -eq 'bluetusk:source-commit' -and
            [string]::Equals(
                [string]$_.value,
                [string]$provenance.sourceCommit,
                [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 1)
{
    throw 'CycloneDX metadata does not identify the provenance source commit.'
}
if ([string]$spdx.name -ne "BlueTusk-$($provenance.sourceCommit)" -or
    -not ([string]$spdx.documentNamespace).Contains(
        "/$($provenance.sourceCommit)/",
        [StringComparison]::OrdinalIgnoreCase))
{
    throw 'SPDX metadata does not identify the provenance source commit.'
}

$allPackageFiles = @(
    Get-ChildItem -LiteralPath $resolvedPackages -Recurse -File
)
$artifactFiles = @($allPackageFiles |
    Where-Object { $_.Extension -in @('.nupkg', '.snupkg', '.tgz') } |
    Sort-Object Name)
if ($artifactFiles.Count -eq 0)
{
    throw 'No candidate package artifacts were found.'
}
if ($allPackageFiles.Count -ne $artifactFiles.Count -or
    @($artifactFiles | Where-Object {
            $_.Directory.FullName -ne $resolvedPackages
        }).Count -ne 0)
{
    throw 'Candidate package directories may contain only top-level NuGet, symbol and npm archives.'
}

$provenanceArtifacts = @($provenance.artifacts)
$provenanceArtifactPaths = @(
    $provenanceArtifacts |
        ForEach-Object { [string]$_.path }
)
if ($provenanceArtifacts.Count -ne $artifactFiles.Count -or
    @($provenanceArtifactPaths | Sort-Object -Unique).Count -ne
        $provenanceArtifacts.Count)
{
    throw 'Build provenance does not contain exactly one record per candidate package.'
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
        [StringComparison]::OrdinalIgnoreCase) -or
        $artifact.Length -ne [long]$record[0].bytes)
    {
        throw "Candidate artifact '$($artifact.Name)' does not match its provenance hash or bytes."
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

$sbomRecords = @($provenance.sboms)
$expectedSbomNames = @('bluetusk.cdx.json', 'bluetusk.spdx.json')
$actualSbomNames = @($sbomRecords | ForEach-Object { [string]$_.path } | Sort-Object)
if ($sbomRecords.Count -ne $expectedSbomNames.Count -or
    -not [Linq.Enumerable]::SequenceEqual(
        [string[]]$expectedSbomNames,
        [string[]]$actualSbomNames,
        [StringComparer]::Ordinal))
{
    throw 'Build provenance must identify exactly the CycloneDX and SPDX SBOM documents.'
}
foreach ($sbomRecord in $sbomRecords)
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
