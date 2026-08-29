[CmdletBinding()]
param(
    [string] $ContractPath = (Join-Path $PSScriptRoot 'v1.2-release-contract.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
$families = @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')
$rcVersion = '1.2.0-rc.1'

if ([int]$contract.schemaVersion -ne 1 -or
    [string]$contract.releaseVersion -ne '1.2.0' -or
    [string]$contract.baselineCommit -notmatch '^[0-9a-f]{40}$')
{
    throw 'The 1.2 release contract has an invalid schema, version, or baseline commit.'
}
if (@($contract.coordinatedFamilies).Count -ne $families.Count -or
    @(Compare-Object $families @($contract.coordinatedFamilies) -SyncWindow 0).Count -ne 0)
{
    throw 'The 1.2 release contract must coordinate all six product families in dependency order.'
}

$productFamiliesPath = Join-Path $PSScriptRoot 'product-families.json'
$productFamilies = Get-Content -LiteralPath $productFamiliesPath -Raw |
    ConvertFrom-Json
foreach ($family in $families)
{
    $definition = $productFamilies.families.PSObject.Properties[$family].Value
    if ($null -eq $definition -or $definition.publication.enabled -ne $false)
    {
        throw "Stable publication for '$family' must remain disabled until every 1.2 gate passes."
    }

    [xml]$versionDocument = Get-Content -LiteralPath (
        Join-Path $repositoryRoot ([string]$definition.versionFile)) -Raw
    $versionPrefix = [string]$versionDocument.Project.PropertyGroup.VersionPrefix
    if ($versionPrefix -ne [string]$contract.releaseVersion)
    {
        throw "Product family '$family' is at '$versionPrefix', not '$($contract.releaseVersion)'."
    }
}

foreach ($manifestName in @('prerelease-train.json', 'package-prerelease-train.json'))
{
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot $manifestName) -Raw |
        ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1 -or
        [string]$manifest.version -ne $rcVersion -or
        $manifest.publicationEnabled -ne $true -or
        @($manifest.families).Count -ne $families.Count -or
        @(Compare-Object $families @($manifest.families) -SyncWindow 0).Count -ne 0)
    {
        throw "Prerelease manifest '$manifestName' is not the exact coordinated $rcVersion train."
    }
}

$nuGetProjects = [ordered]@{
    'BlueTusk.Production.Templates' = 'templates/BlueTusk.Production/BlueTusk.Production.Templates.csproj'
    'BlueTusk.ControlPlane.Kubernetes' = 'src/BlueTusk.ControlPlane.Kubernetes/BlueTusk.ControlPlane.Kubernetes.csproj'
    'BlueTusk.Sync.Kafka' = 'src/BlueTusk.Sync.Kafka/BlueTusk.Sync.Kafka.csproj'
    'BlueTusk.Sync.S3' = 'src/BlueTusk.Sync.S3/BlueTusk.Sync.S3.csproj'
    'BlueTusk.Sync.Webhooks' = 'src/BlueTusk.Sync.Webhooks/BlueTusk.Sync.Webhooks.csproj'
}
$solutionText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'BlueTusk.slnx') -Raw
foreach ($entry in $nuGetProjects.GetEnumerator())
{
    $projectPath = Join-Path $repositoryRoot $entry.Value
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf) -or
        -not $solutionText.Contains($entry.Value, [StringComparison]::Ordinal))
    {
        throw "New 1.2 package '$($entry.Key)' is absent from the source tree or solution."
    }

    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $packageIdNode = $project.SelectSingleNode('//PackageId')
    $packageId = if ($null -eq $packageIdNode)
    {
        [IO.Path]::GetFileNameWithoutExtension($projectPath)
    }
    else
    {
        [string]$packageIdNode.InnerText
    }
    if ($packageId -ne $entry.Key)
    {
        throw "Project '$($entry.Value)' does not produce package '$($entry.Key)'."
    }
    $registered = $false
    foreach ($family in $families)
    {
        $definition = $productFamilies.families.PSObject.Properties[$family].Value
        if ($entry.Value -in @($definition.packages))
        {
            $registered = $true
            break
        }
    }
    if (-not $registered)
    {
        throw "New 1.2 package '$($entry.Key)' is not assigned to a product family."
    }
}
if (@($contract.newNuGetPackages).Count -ne $nuGetProjects.Count -or
    @(Compare-Object @($nuGetProjects.Keys) @($contract.newNuGetPackages)).Count -ne 0)
{
    throw 'The contract newNuGetPackages list does not exactly match the registered 1.2 additions.'
}

$npmProjects = [ordered]@{
    '@bluetusk/live-vue' = 'clients/live-vue'
    '@bluetusk/live-svelte' = 'clients/live-svelte'
}
$liveDefinition = $productFamilies.families.Live
foreach ($entry in $npmProjects.GetEnumerator())
{
    $manifestPath = Join-Path $repositoryRoot "$($entry.Value)/package.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]$manifest.name -ne $entry.Key -or
        [string]$manifest.version -ne [string]$contract.releaseVersion -or
        $entry.Value -notin @($liveDefinition.npmPackages))
    {
        throw "New npm client '$($entry.Key)' is not an exact registered 1.2 Live package."
    }
}
if (@($contract.newNpmPackages).Count -ne $npmProjects.Count -or
    @(Compare-Object @($npmProjects.Keys) @($contract.newNpmPackages)).Count -ne 0)
{
    throw 'The contract newNpmPackages list does not exactly match the registered 1.2 additions.'
}

foreach ($flag in @(
        'productionStarter',
        'readOnlyDoctor',
        'kubernetesOperator',
        'controlPlaneFleetOperations',
        'graphVariableLengthPaths',
        'graphUndirectedPatterns',
        'graphMultiLabelExpressions'))
{
    if ($contract.requiredProductWork.PSObject.Properties[$flag].Value -ne $true)
    {
        throw "Required 1.2 product-work flag '$flag' is not complete."
    }
}

$gates = $contract.releaseGates
foreach ($flag in @(
        'build', 'security', 'performance', 'publicApiCompatibility',
        'packageConsumerSmoke', 'nativeAotAndTrimming', 'windowsX64', 'linuxX64',
        'backupRestoreRehearsal', 'rollbackRehearsal',
        'postgresql19GaDigestRequired'))
{
    if ($gates.PSObject.Properties[$flag].Value -ne $true)
    {
        throw "Required 1.2 release gate '$flag' is not enabled."
    }
}
if ([int]$gates.streamsEnduranceHours -ne 72 -or
    [int]$gates.syncEnduranceHours -ne 24 -or
    [int]$gates.liveAndControlPlaneEnduranceHours -ne 24 -or
    [int]$gates.continuousGraphEnduranceHours -ne 24 -or
    [int]$gates.independentPilots -ne 2)
{
    throw 'The 1.2 endurance or independent-pilot minimums were weakened.'
}

$endurance = $contract.enduranceExecution
if ([string]$endurance.namespace -ne 'bluetusk-endurance' -or
    [string]$endurance.launcher -ne 'eng/deploy-kubernetes-endurance.ps1' -or
    [string]$endurance.evidenceStorageClass -ne 'do-block-storage-retain' -or
    $endurance.streamsBeforeSync -ne $true -or
    $endurance.liveAndControlPlaneAfterSync -ne $true -or
    $endurance.exactMainCommitRequired -ne $true -or
    $endurance.continuousGraphPreviewEnabled -ne $true -or
    $endurance.continuousGraphJobEnabledBeforePostgreSql19Ga -ne $false)
{
    throw 'The guarded Kubernetes endurance execution contract was weakened.'
}
$enduranceFiles = @(
    'deploy/kubernetes/endurance/namespace.yaml',
    'deploy/kubernetes/endurance/postgresql.yaml',
    'deploy/kubernetes/endurance/sync-services.yaml',
    'deploy/kubernetes/endurance/streams-job.yaml',
    'deploy/kubernetes/endurance/sync-job.yaml',
    'deploy/kubernetes/endurance/run-streams.ps1',
    'deploy/kubernetes/endurance/run-sync.ps1',
    'deploy/kubernetes/endurance/live-control-plane-job.yaml',
    'deploy/kubernetes/endurance/run-live-control-plane.ps1',
    'deploy/kubernetes/endurance/continuous-graph-preview-job.yaml',
    'deploy/kubernetes/endurance/run-continuous-graph-preview.ps1',
    'eng/run-live-control-plane-endurance.ps1',
    'eng/verify-live-control-plane-endurance-report.ps1',
    '.github/workflows/live-control-plane-release-endurance.yml',
    'eng/deploy-kubernetes-endurance.ps1')
foreach ($path in $enduranceFiles)
{
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $path) -PathType Leaf))
    {
        throw "Required Kubernetes endurance file '$path' is missing."
    }
}
if (Test-Path -LiteralPath (
        Join-Path $repositoryRoot 'deploy/kubernetes/endurance/continuous-graph-job.yaml'))
{
    throw 'Continuous Graph endurance must not be enabled before PostgreSQL 19 GA is digest pinned.'
}
$previewManifestText = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'deploy/kubernetes/endurance/continuous-graph-preview-job.yaml') -Raw
foreach ($previewBoundary in @(
        'bluetusk.io/release-gate: "false"',
        'non-gating-postgresql-19-beta3-preview',
        'NON_GATING_PREVIEW'))
{
    if (-not $previewManifestText.Contains($previewBoundary, [StringComparison]::Ordinal))
    {
        throw "Continuous Graph preview is missing boundary '$previewBoundary'."
    }
}
$launcherText = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'eng/deploy-kubernetes-endurance.ps1') -Raw
foreach ($snippet in @(
        'merge-base --is-ancestor',
        'origin/main',
        'Sync cannot start until the exact Streams 72-hour Job has completed successfully.',
        'Live/Control Plane cannot start until the exact Sync 24-hour Job has completed successfully.'))
{
    if (-not $launcherText.Contains($snippet, [StringComparison]::Ordinal))
    {
        throw "Kubernetes endurance launcher is missing fail-closed contract '$snippet'."
    }
}
foreach ($manifestPath in @(
        'postgresql.yaml', 'sync-services.yaml', 'streams-job.yaml', 'sync-job.yaml',
        'live-control-plane-job.yaml', 'continuous-graph-preview-job.yaml'))
{
    $manifestText = Get-Content -LiteralPath (
        Join-Path $repositoryRoot "deploy/kubernetes/endurance/$manifestPath") -Raw
    foreach ($imageLine in @($manifestText -split "`r?`n" | Where-Object {
                $_ -match '^\s+image:\s+'
            }))
    {
        if ($imageLine -notmatch '@sha256:[0-9a-f]{64}\s*$')
        {
            throw "Endurance manifest '$manifestPath' contains an unpinned image: '$($imageLine.Trim())'."
        }
    }
}

if ($contract.compatibility.continuousGraphRequiresPostgreSql19Ga -ne $true -or
    $contract.publication.stableEnabledBeforeAllGatesPass -ne $false -or
    $contract.publication.nugetTrustedPublishingRequired -ne $true -or
    $contract.publication.npmProvenanceRequired -ne $true -or
    $contract.publication.sbomRequired -ne $true -or
    $contract.publication.dependencyOrderRequired -ne $true)
{
    throw 'The 1.2 compatibility or stable-publication boundary was weakened.'
}

Write-Output (
    "Verified the coordinated BlueTusk 1.2 contract: six families, " +
    "$($nuGetProjects.Count) new NuGet packages, $($npmProjects.Count) new npm packages, " +
    'guarded Kubernetes endurance, and disabled stable publication.')
