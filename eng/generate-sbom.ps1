[CmdletBinding()]
param(
    [string] $Solution = 'BlueTusk.slnx',
    [string] $PackageDirectory = 'artifacts/packages',
    [string] $OutputDirectory = 'artifacts/sbom',
    [string] $Commit,
    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Split-Path $PSScriptRoot -Parent)
$solutionPath = (Resolve-Path -LiteralPath (Join-Path $repositoryRoot $Solution)).Path
$resolvedPackageDirectory = Join-Path $repositoryRoot $PackageDirectory
$resolvedOutputDirectory = Join-Path $repositoryRoot $OutputDirectory
[IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

if ([string]::IsNullOrWhiteSpace($Commit))
{
    $Commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Could not resolve the source commit.'
    }
}
if ($Commit -notmatch '^[0-9a-fA-F]{40}$')
{
    throw "A full source commit is required; found '$Commit'."
}
$Commit = $Commit.ToLowerInvariant()

$commitTimestamp = (& git -C $repositoryRoot show -s --format=%cI $Commit).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commitTimestamp))
{
    throw "Could not resolve timestamp for commit '$Commit'."
}
$created = [DateTimeOffset]::Parse(
    $commitTimestamp,
    [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime().ToString('o')

$listArguments = @(
    'list',
    $solutionPath,
    'package',
    '--include-transitive',
    '--format',
    'json'
)
if ($NoRestore)
{
    $listArguments += '--no-restore'
}
$packageOutput = @(& dotnet @listArguments)
if ($LASTEXITCODE -ne 0)
{
    throw 'Could not enumerate NuGet dependencies for the SBOM.'
}
$packageReport = ($packageOutput -join [Environment]::NewLine) | ConvertFrom-Json

$components = [ordered]@{}
function Add-Component
{
    param(
        [Parameter(Mandatory)]
        [string] $Id,

        [Parameter(Mandatory)]
        [string] $Version,

        [string] $ArtifactPath,

        [string] $Sha256,

        [string] $PurlType = 'nuget',

        [string] $ArtifactKind
    )

    $key = "$($Id.ToLowerInvariant())@$Version"
    if (-not [string]::IsNullOrWhiteSpace($ArtifactPath))
    {
        $key += "|$ArtifactPath"
    }
    $purl = "pkg:$PurlType/$([Uri]::EscapeDataString($Id))@$([Uri]::EscapeDataString($Version))"
    if (-not [string]::IsNullOrWhiteSpace($ArtifactKind))
    {
        $purl += "?type=$([Uri]::EscapeDataString($ArtifactKind))"
    }
    if (-not $components.Contains($key))
    {
        $component = [ordered]@{
            type = 'library'
            'bom-ref' = $purl
            name = $Id
            version = $Version
            purl = $purl
            hashes = @()
            properties = @()
        }
        $components[$key] = $component
    }

    if (-not [string]::IsNullOrWhiteSpace($ArtifactPath))
    {
        $components[$key].hashes = @(
            [ordered]@{
                alg = 'SHA-256'
                content = $Sha256.ToUpperInvariant()
            })
        $components[$key].properties = @(
            [ordered]@{
                name = 'bluetusk:artifact-path'
                value = $ArtifactPath
            })
    }
}

foreach ($project in @($packageReport.projects))
{
    foreach ($framework in @($project.frameworks))
    {
        $frameworkPackages = @()
        if ($null -ne $framework.PSObject.Properties['topLevelPackages'])
        {
            $frameworkPackages += @($framework.topLevelPackages)
        }
        if ($null -ne $framework.PSObject.Properties['transitivePackages'])
        {
            $frameworkPackages += @($framework.transitivePackages)
        }
        foreach ($package in $frameworkPackages)
        {
            Add-Component -Id ([string]$package.id) -Version ([string]$package.resolvedVersion)
        }
    }
}

$artifacts = [System.Collections.Generic.List[object]]::new()
if (Test-Path -LiteralPath $resolvedPackageDirectory -PathType Container)
{
    $artifactFiles = Get-ChildItem -LiteralPath $resolvedPackageDirectory -File |
        Where-Object { $_.Extension -in @('.nupkg', '.snupkg', '.tgz') } |
        Sort-Object Name
    foreach ($artifact in $artifactFiles)
    {
        $sha256 = (
            Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        $artifactRecord = [ordered]@{
            path = $artifact.Name
            sha256 = $sha256
            bytes = $artifact.Length
        }
        $artifacts.Add($artifactRecord)

        if ($artifact.Extension -in @('.nupkg', '.snupkg'))
        {
            $archive = [IO.Compression.ZipFile]::OpenRead($artifact.FullName)
            try
            {
                $nuspec = $archive.Entries |
                    Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) } |
                    Select-Object -First 1
                if ($null -eq $nuspec)
                {
                    throw "Package '$($artifact.Name)' has no nuspec."
                }

                $stream = $nuspec.Open()
                try
                {
                    [xml]$metadata = [IO.StreamReader]::new($stream).ReadToEnd()
                }
                finally
                {
                    $stream.Dispose()
                }
                $id = [string]$metadata.SelectSingleNode(
                    "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']").InnerText
                $version = [string]$metadata.SelectSingleNode(
                    "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']").InnerText
                Add-Component -Id $id -Version $version -ArtifactPath $artifact.Name `
                    -Sha256 $sha256 -ArtifactKind $artifact.Extension.TrimStart('.')
            }
            finally
            {
                $archive.Dispose()
            }
        }
        else
        {
            $id = [IO.Path]::GetFileNameWithoutExtension($artifact.Name)
            Add-Component -Id $id -Version $Commit.Substring(0, 12) `
                -ArtifactPath $artifact.Name -Sha256 $sha256 `
                -PurlType 'generic' -ArtifactKind 'tgz'
        }
    }
}

$seed = [Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes("BlueTusk:$Commit"))
$guidBytes = [byte[]]::new(16)
[Array]::Copy($seed, $guidBytes, 16)
$serial = [Guid]::new($guidBytes).ToString()
$componentValues = @($components.Values | Sort-Object name, version)
$cycloneDx = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    serialNumber = "urn:uuid:$serial"
    version = 1
    metadata = [ordered]@{
        timestamp = $created
        component = [ordered]@{
            type = 'application'
            'bom-ref' = "pkg:generic/BlueTusk@$Commit"
            name = 'BlueTusk'
            version = $Commit
        }
        properties = @(
            [ordered]@{
                name = 'bluetusk:source-commit'
                value = $Commit
            })
    }
    components = $componentValues
}

$spdxPackages = [System.Collections.Generic.List[object]]::new()
$relationships = [System.Collections.Generic.List[object]]::new()
$index = 0
foreach ($component in $componentValues)
{
    $index++
    $spdxId = "SPDXRef-Package-$index"
    $package = [ordered]@{
        name = $component.name
        SPDXID = $spdxId
        versionInfo = $component.version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        supplier = 'NOASSERTION'
        checksums = @()
        externalRefs = @(
            [ordered]@{
                referenceCategory = 'PACKAGE-MANAGER'
                referenceType = 'purl'
                referenceLocator = $component.purl
            })
    }
    if ($component.hashes.Count -gt 0)
    {
        $package.checksums = @(
            [ordered]@{
                algorithm = 'SHA256'
                checksumValue = $component.hashes[0].content.ToLowerInvariant()
            })
    }
    $spdxPackages.Add($package)
    $relationships.Add(
        [ordered]@{
            spdxElementId = 'SPDXRef-DOCUMENT'
            relationshipType = 'DESCRIBES'
            relatedSpdxElement = $spdxId
        })
}

$spdx = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "BlueTusk-$Commit"
    documentNamespace = "https://github.com/jphgardner/BlueTusk/sbom/$Commit/$serial"
    creationInfo = [ordered]@{
        created = $created
        creators = @('Tool: BlueTusk-eng-generate-sbom')
    }
    packages = @($spdxPackages)
    relationships = @($relationships)
}

$cyclonePath = Join-Path $resolvedOutputDirectory 'bluetusk.cdx.json'
$spdxPath = Join-Path $resolvedOutputDirectory 'bluetusk.spdx.json'
$cycloneDx | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $cyclonePath -Encoding utf8NoBOM
$spdx | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $spdxPath -Encoding utf8NoBOM

$trackedStatus = @(& git -C $repositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0)
{
    throw 'Could not inspect source-tree state for provenance.'
}
$provenance = [ordered]@{
    schemaVersion = 1
    sourceCommit = $Commit
    sourceCommitTimestamp = $created
    sourceTreeDirty = $trackedStatus.Count -gt 0
    generatedBy = [ordered]@{
        dotnet = (& dotnet --version).Trim()
        powershell = $PSVersionTable.PSVersion.ToString()
        os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    }
    artifacts = @($artifacts)
    sboms = @(
        [ordered]@{
            path = [IO.Path]::GetFileName($cyclonePath)
            sha256 = (Get-FileHash $cyclonePath -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            path = [IO.Path]::GetFileName($spdxPath)
            sha256 = (Get-FileHash $spdxPath -Algorithm SHA256).Hash.ToLowerInvariant()
        })
}
$provenancePath = Join-Path $resolvedOutputDirectory 'build-provenance.json'
$provenance | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $provenancePath -Encoding utf8NoBOM

Write-Host (
    "Generated CycloneDX 1.6 and SPDX 2.3 SBOMs for $($componentValues.Count) " +
    "components and $($artifacts.Count) candidate artifacts.")
