[CmdletBinding()]
param(
    [string] $OutputRoot = 'artifacts/v1-candidate-packages',
    [string] $Configuration = 'Release',

    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $Commit,

    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$resolvedOutputRoot = if ([IO.Path]::IsPathRooted($OutputRoot))
{
    [IO.Path]::GetFullPath($OutputRoot)
}
else
{
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}
$artifactsPrefix = $artifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutputRoot.StartsWith(
        $artifactsPrefix,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "Candidate package output '$resolvedOutputRoot' must be below '$artifactsRoot'."
}

if ([string]::IsNullOrWhiteSpace($Commit))
{
    $Commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Could not resolve the candidate source commit.'
    }
}
$Commit = $Commit.ToLowerInvariant()
$headCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $headCommit -ne $Commit)
{
    throw "Checked-out commit '$headCommit' does not match candidate commit '$Commit'."
}

$trackedStatus = @(& git -C $repositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $trackedStatus.Count -ne 0)
{
    throw 'Canonical candidate packages require a clean tracked worktree.'
}

if (Test-Path -LiteralPath $resolvedOutputRoot)
{
    Remove-Item -LiteralPath $resolvedOutputRoot -Recurse -Force
}
[IO.Directory]::CreateDirectory($resolvedOutputRoot) | Out-Null

$workRoot = Join-Path $resolvedOutputRoot '_work'
$packageRoot = Join-Path $resolvedOutputRoot 'packages'
$sbomRoot = Join-Path $resolvedOutputRoot 'sbom'
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
[IO.Directory]::CreateDirectory($packageRoot) | Out-Null
[IO.Directory]::CreateDirectory($sbomRoot) | Out-Null

if (-not $NoRestore)
{
    & dotnet restore (Join-Path $repositoryRoot 'BlueTusk.slnx')
    if ($LASTEXITCODE -ne 0)
    {
        throw "Candidate package restore failed with exit code $LASTEXITCODE."
    }
}

$manifest = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'product-families.json') -Raw | ConvertFrom-Json
$familyNames = @(
    $manifest.families.PSObject.Properties |
        ForEach-Object { $_.Name }
)
$artifactRecords = [Collections.Generic.List[object]]::new()
$familyRecords = [Collections.Generic.List[object]]::new()
$totalPackageBytes = 0L
$artifactNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)

try
{
    foreach ($family in $familyNames)
    {
        $familyRoot = Join-Path $workRoot $family
        & (Join-Path $PSScriptRoot 'pack-product-family.ps1') `
            -Family $family `
            -Configuration $Configuration `
            -Output $familyRoot `
            -Candidate `
            -NoRestore
        if ($LASTEXITCODE -ne 0)
        {
            throw "Packing the '$family' candidate failed with exit code $LASTEXITCODE."
        }

        & (Join-Path $PSScriptRoot 'verify-product-family-packages.ps1') `
            -Family $family `
            -PackageDirectory $familyRoot `
            -ExpectedCommit $Commit

        $familyArtifacts = @(
            Get-ChildItem -LiteralPath $familyRoot -File |
                Where-Object { $_.Extension -in @('.nupkg', '.snupkg', '.tgz') } |
                Sort-Object Name
        )
        if ($familyArtifacts.Count -eq 0)
        {
            throw "The '$family' candidate produced no package artifacts."
        }

        $familyBytes = 0L
        foreach ($artifact in $familyArtifacts)
        {
            if (-not $artifactNames.Add($artifact.Name))
            {
                throw "Candidate package file '$($artifact.Name)' is emitted by more than one family."
            }

            $destination = Join-Path $packageRoot $artifact.Name
            [IO.File]::Copy($artifact.FullName, $destination, $false)
            $copied = Get-Item -LiteralPath $destination
            $sha256 = (
                Get-FileHash -LiteralPath $copied.FullName -Algorithm SHA256
            ).Hash.ToLowerInvariant()
            $artifactRecords.Add([ordered]@{
                path = "packages/$($copied.Name)"
                family = $family
                sha256 = $sha256
                bytes = $copied.Length
            })
            $familyBytes += $copied.Length
            $totalPackageBytes += $copied.Length
        }

        $familyRecords.Add([ordered]@{
            id = $family
            artifactCount = $familyArtifacts.Count
            totalBytes = $familyBytes
        })
    }

    & (Join-Path $PSScriptRoot 'generate-sbom.ps1') `
        -PackageDirectory ([IO.Path]::GetRelativePath($repositoryRoot, $packageRoot)) `
        -OutputDirectory ([IO.Path]::GetRelativePath($repositoryRoot, $sbomRoot)) `
        -Commit $Commit `
        -NoRestore
    & (Join-Path $PSScriptRoot 'verify-supply-chain.ps1') `
        -RepositoryRoot $repositoryRoot `
        -PackageDirectory ([IO.Path]::GetRelativePath($repositoryRoot, $packageRoot)) `
        -SbomDirectory ([IO.Path]::GetRelativePath($repositoryRoot, $sbomRoot)) `
        -ExpectedCommit $Commit

    $supplyFiles = [ordered]@{}
    foreach ($entry in @(
            @{ Key = 'cycloneDx'; Name = 'bluetusk.cdx.json' },
            @{ Key = 'spdx'; Name = 'bluetusk.spdx.json' },
            @{ Key = 'provenance'; Name = 'build-provenance.json' }))
    {
        $file = Get-Item -LiteralPath (Join-Path $sbomRoot $entry.Name)
        $supplyFiles[$entry.Key] = [ordered]@{
            path = "sbom/$($file.Name)"
            sha256 = (
                Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
            ).Hash.ToLowerInvariant()
            bytes = $file.Length
        }
    }

    $sortedArtifacts = @($artifactRecords | Sort-Object path)
    $candidateManifest = [ordered]@{
        schemaVersion = 1
        sourceCommit = $Commit
        familyCount = $familyRecords.Count
        artifactCount = $sortedArtifacts.Count
        totalBytes = $totalPackageBytes
        families = @($familyRecords | Sort-Object id)
        artifacts = $sortedArtifacts
        supplyChain = $supplyFiles
    }
    $candidateManifest | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (
            Join-Path $resolvedOutputRoot 'package-manifest.json') `
            -Encoding utf8NoBOM
}
finally
{
    if (Test-Path -LiteralPath $workRoot)
    {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}

Write-Output (
    "Built immutable V1 package evidence for commit ${Commit}: " +
    "$($familyRecords.Count) families, $($artifactRecords.Count) package artifacts, " +
    "$totalPackageBytes bytes.")
