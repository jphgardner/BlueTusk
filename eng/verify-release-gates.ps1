[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')]
    [string] $Family,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $Commit,

    [Parameter(Mandatory)]
    [string] $Tag,

    [string] $Repository = $env:GITHUB_REPOSITORY,

    [string] $Token = $env:GITHUB_TOKEN,

    [string] $EvidencePath,

    [string] $NuGetFlatContainerUrl = 'https://api.nuget.org/v3-flatcontainer',

    [string] $NpmRegistryUrl = 'https://registry.npmjs.org'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FamilyVersion
{
    param(
        [Parameter(Mandatory)]
        [object] $Definition,

        [Parameter(Mandatory)]
        [string] $Root
    )

    [xml] $document = Get-Content -LiteralPath (
        Join-Path $Root $Definition.versionFile) -Raw
    $prefix = [string]$document.Project.PropertyGroup.VersionPrefix
    $suffix = [string]$document.Project.PropertyGroup.VersionSuffix
    if ([string]::IsNullOrWhiteSpace($prefix))
    {
        throw "Version file '$($Definition.versionFile)' has no VersionPrefix."
    }

    if ([string]::IsNullOrWhiteSpace($suffix))
    {
        return $prefix
    }

    return "$prefix-$suffix"
}

function Get-ProjectPackageId
{
    param(
        [Parameter(Mandatory)]
        [string] $ProjectPath
    )

    [xml] $document = Get-Content -LiteralPath $ProjectPath -Raw
    $packageId = @(
        $document.SelectNodes('//PackageId') |
            ForEach-Object { $_.InnerText } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -First 1
    )
    if ($packageId.Count -gt 0)
    {
        return [string]$packageId[0]
    }

    return [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
}

function Assert-PublishedResource
{
    param(
        [Parameter(Mandatory)]
        [string] $Uri,

        [Parameter(Mandatory)]
        [string] $Description
    )

    try
    {
        $response = Invoke-WebRequest `
            -Method Head `
            -Uri $Uri `
            -MaximumRedirection 5 `
            -SkipHttpErrorCheck
    }
    catch
    {
        throw "Could not verify published dependency $Description at '$Uri': $($_.Exception.Message)"
    }

    if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300)
    {
        throw (
            "Required dependency $Description is not published at '$Uri' " +
            "(HTTP $([int]$response.StatusCode)).")
    }
}

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$manifestPath = Join-Path $PSScriptRoot 'product-families.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2)
{
    throw "Expected product-family manifest schema 2; found '$($manifest.schemaVersion)'."
}

$definition = $manifest.families.$Family
if ($null -eq $definition)
{
    throw "Product family '$Family' is not registered."
}

$publication = $definition.publication
if ($null -eq $publication -or $publication.enabled -ne $true)
{
    throw "Product family '$Family' is gated and cannot be published."
}

foreach ($dependency in @($definition.releaseDependencies))
{
    if ($manifest.families.$dependency.publication.enabled -ne $true)
    {
        throw "Product family '$Family' cannot publish before '$dependency'."
    }
}

$versionPath = Join-Path $repositoryRoot $definition.versionFile
[xml] $versionDocument = Get-Content -LiteralPath $versionPath -Raw
$versionPrefix = [string]$versionDocument.Project.PropertyGroup.VersionPrefix
$versionSuffix = [string]$versionDocument.Project.PropertyGroup.VersionSuffix
$version = if ([string]::IsNullOrWhiteSpace($versionSuffix))
{
    $versionPrefix
}
else
{
    "$versionPrefix-$versionSuffix"
}

$parsedVersion = $null
if (-not [Version]::TryParse($versionPrefix, [ref]$parsedVersion))
{
    throw "Product family '$Family' has invalid VersionPrefix '$versionPrefix'."
}

$channel = [string]$publication.channel
switch ($channel)
{
    'stable'
    {
        if ($parsedVersion.Major -lt 1 -or -not [string]::IsNullOrWhiteSpace($versionSuffix))
        {
            throw "Stable publication for '$Family' requires a 1.0.0-or-newer version without a suffix."
        }
    }
    'preview'
    {
        if ([string]::IsNullOrWhiteSpace($versionSuffix))
        {
            throw "Preview publication for '$Family' requires a prerelease version suffix."
        }
    }
    default
    {
        throw "Product family '$Family' has unsupported publication channel '$channel'."
    }
}

$expectedTag = "$($publication.tagPrefix)-v$version"
if (-not [string]::Equals($Tag, $expectedTag, [StringComparison]::Ordinal))
{
    throw "Release tag '$Tag' does not match '$Family' version '$expectedTag'."
}

$headCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or
    -not [string]::Equals($headCommit, $Commit, [StringComparison]::OrdinalIgnoreCase))
{
    throw "Checked-out commit '$headCommit' does not match release commit '$Commit'."
}

$trackedStatus = @(& git -C $repositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0)
{
    throw 'Could not inspect the release worktree.'
}
if ($trackedStatus.Count -ne 0)
{
    throw 'Release verification requires a clean tracked worktree.'
}

$verifiedDependencyPackages = 0
foreach ($dependency in @($definition.releaseDependencies))
{
    $dependencyDefinition = $manifest.families.$dependency
    $dependencyVersion = Get-FamilyVersion `
        -Definition $dependencyDefinition `
        -Root $repositoryRoot
    foreach ($project in @($dependencyDefinition.packages))
    {
        $packageId = Get-ProjectPackageId -ProjectPath (
            Join-Path $repositoryRoot $project)
        $normalizedPackageId = $packageId.ToLowerInvariant()
        $normalizedVersion = $dependencyVersion.ToLowerInvariant()
        $packageUri =
            "$($NuGetFlatContainerUrl.TrimEnd('/'))/" +
            "$normalizedPackageId/$normalizedVersion/" +
            "$normalizedPackageId.$normalizedVersion.nupkg"
        Assert-PublishedResource `
            -Uri $packageUri `
            -Description "NuGet package '$packageId' version '$dependencyVersion'"
        $verifiedDependencyPackages++
    }

    if ($null -ne $dependencyDefinition.PSObject.Properties['npmPackages'])
    {
        foreach ($npmPath in @($dependencyDefinition.npmPackages))
        {
            $npmManifest = Get-Content -LiteralPath (
                Join-Path (Join-Path $repositoryRoot $npmPath) 'package.json') `
                -Raw |
                ConvertFrom-Json
            $encodedPackageName = [Uri]::EscapeDataString([string]$npmManifest.name)
            $encodedVersion = [Uri]::EscapeDataString($dependencyVersion)
            $packageUri =
                "$($NpmRegistryUrl.TrimEnd('/'))/" +
                "$encodedPackageName/$encodedVersion"
            Assert-PublishedResource `
                -Uri $packageUri `
                -Description "npm package '$($npmManifest.name)' version '$dependencyVersion'"
            $verifiedDependencyPackages++
        }
    }
}

$fixtureRuns = $null
if (-not [string]::IsNullOrWhiteSpace($EvidencePath))
{
    $resolvedEvidencePath = (Resolve-Path -LiteralPath $EvidencePath).Path
    $fixture = Get-Content -LiteralPath $resolvedEvidencePath -Raw | ConvertFrom-Json
    if ($fixture.schemaVersion -ne 1)
    {
        throw "Expected workflow-evidence fixture schema 1; found '$($fixture.schemaVersion)'."
    }

    $fixtureRuns = @($fixture.runs)
}
elseif ([string]::IsNullOrWhiteSpace($Repository) -or
        [string]::IsNullOrWhiteSpace($Token))
{
    throw 'Exact-commit workflow verification requires GITHUB_REPOSITORY and GITHUB_TOKEN.'
}

$verifiedRuns = [Collections.Generic.List[object]]::new()
foreach ($requirement in @($publication.requiredWorkflowEvidence))
{
    $workflowFile = [string]$requirement.workflowFile
    $allowedEvents = @($requirement.allowedEvents | ForEach-Object { [string]$_ })
    if ($allowedEvents.Count -ne 1 -or $allowedEvents[0] -ne 'workflow_dispatch')
    {
        throw "Workflow '$workflowFile' must require workflow_dispatch evidence only."
    }

    $runs = if ($null -ne $fixtureRuns)
    {
        @($fixtureRuns | Where-Object {
            [string]::Equals(
                [string]$_.workflowFile,
                $workflowFile,
                [StringComparison]::Ordinal)
        })
    }
    else
    {
        $encodedWorkflow = [Uri]::EscapeDataString($workflowFile)
        $uri =
            "https://api.github.com/repos/$Repository/actions/workflows/" +
            "$encodedWorkflow/runs?head_sha=$Commit&status=completed&per_page=100"
        $headers = @{
            Accept = 'application/vnd.github+json'
            Authorization = "Bearer $Token"
            'User-Agent' = 'BlueTusk-release-gate'
            'X-GitHub-Api-Version' = '2022-11-28'
        }
        $response = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
        @($response.workflow_runs | ForEach-Object {
            [pscustomobject]@{
                workflowFile = $workflowFile
                headSha = [string]$_.head_sha
                event = [string]$_.event
                conclusion = [string]$_.conclusion
                runId = [long]$_.id
                url = [string]$_.html_url
            }
        })
    }

    $successfulRun = $runs |
        Where-Object {
            [string]::Equals(
                [string]$_.headSha,
                $Commit,
                [StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals(
                [string]$_.conclusion,
                'success',
                [StringComparison]::Ordinal) -and
            $allowedEvents -contains [string]$_.event
        } |
        Sort-Object runId -Descending |
        Select-Object -First 1

    if ($null -eq $successfulRun)
    {
        throw (
            "No successful '$workflowFile' run for exact commit '$Commit' " +
            "from allowed event(s): $($allowedEvents -join ', ').")
    }

    $verifiedRuns.Add($successfulRun)
}

Write-Output (
    "Verified $Family $channel release '$version' at exact commit $Commit " +
    "using $verifiedDependencyPackages published dependency package(s) and " +
    "$($verifiedRuns.Count) required workflow run(s): " +
    "$(@($verifiedRuns | ForEach-Object { "$($_.workflowFile)#$($_.runId)" }) -join ', ').")
