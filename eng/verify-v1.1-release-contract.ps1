[CmdletBinding()]
param(
    [string] $ContractPath = (Join-Path $PSScriptRoot 'v1.1-release-contract.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
$families = @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')
$rcVersion = '1.1.0-rc.2'
if ($contract.releaseTracksFile -cne 'eng/release-tracks.json')
{
    throw 'The 1.1 release contract must use the canonical family release tracks.'
}
& (Join-Path $PSScriptRoot 'verify-release-track.ps1')

if ([int]$contract.schemaVersion -ne 1 -or
    [string]$contract.releaseVersion -ne '1.1.0' -or
    [string]$contract.baselineCommit -notmatch '^[0-9a-f]{40}$')
{
    throw 'The 1.1 release contract has an invalid schema, version, or baseline commit.'
}
$performanceContract = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'performance-leadership-contract.json') -Raw | ConvertFrom-Json
if ($performanceContract.release -cne $contract.releaseVersion)
{
    throw 'The performance-leadership contract must identify the same 1.1 release.'
}
if (@($contract.coordinatedFamilies).Count -ne $families.Count -or
    @(Compare-Object $families @($contract.coordinatedFamilies) -SyncWindow 0).Count -ne 0)
{
    throw 'The 1.1 release contract must coordinate all six product families in dependency order.'
}

$productFamiliesPath = Join-Path $PSScriptRoot 'product-families.json'
$productFamilies = Get-Content -LiteralPath $productFamiliesPath -Raw |
    ConvertFrom-Json
foreach ($family in $families)
{
    $definition = $productFamilies.families.PSObject.Properties[$family].Value
    if ($null -eq $definition -or $definition.publication.enabled -ne $false)
    {
        throw "Stable publication for '$family' must remain disabled until every 1.1 gate passes."
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
        throw "New 1.1 package '$($entry.Key)' is absent from the source tree or solution."
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
        throw "New 1.1 package '$($entry.Key)' is not assigned to a product family."
    }
}
if (@($contract.newNuGetPackages).Count -ne $nuGetProjects.Count -or
    @(Compare-Object @($nuGetProjects.Keys) @($contract.newNuGetPackages)).Count -ne 0)
{
    throw 'The contract newNuGetPackages list does not exactly match the registered 1.1 additions.'
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
        throw "New npm client '$($entry.Key)' is not an exact registered 1.1 Live package."
    }
}
if (@($contract.newNpmPackages).Count -ne $npmProjects.Count -or
    @(Compare-Object @($npmProjects.Keys) @($contract.newNpmPackages)).Count -ne 0)
{
    throw 'The contract newNpmPackages list does not exactly match the registered 1.1 additions.'
}

foreach ($flag in @(
        'productionStarter',
        'readOnlyDoctor',
        'kubernetesOperator',
        'controlPlaneFleetOperations',
        'postgresql19NativeRepack',
        'graphVariableLengthPaths',
        'graphUndirectedPatterns',
        'graphMultiLabelExpressions'))
{
    if ($contract.requiredProductWork.PSObject.Properties[$flag].Value -ne $true)
    {
        throw "Required 1.1 product-work flag '$flag' is not complete."
    }
}

foreach ($path in @(
        'src/BlueTusk.Data/Maintenance/BlueTuskRepack.cs',
        'tests/BlueTusk.Data.Tests/BlueTuskRepackTests.cs',
        'tests/BlueTusk.IntegrationTests/BlueTuskRepackIntegrationTests.cs',
        'docs/ado-net/repack.md'))
{
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $path) -PathType Leaf))
    {
        throw "Required PostgreSQL 19 native REPACK asset '$path' is missing."
    }
}

$gates = $contract.releaseGates
foreach ($flag in @(
        'build', 'security', 'performance', 'publicApiCompatibility',
        'packageConsumerSmoke', 'nativeAotAndTrimming', 'windowsX64', 'linuxX64',
        'backupRestoreRehearsal', 'rollbackRehearsal'))
{
    if ($gates.PSObject.Properties[$flag].Value -ne $true)
    {
        throw "Required 1.1 release gate '$flag' is not enabled."
    }
}
if ([int]$gates.streamsEnduranceHours -ne 72 -or
    [int]$gates.syncEnduranceHours -ne 24 -or
    [int]$gates.liveAndControlPlaneEnduranceHours -ne 24 -or
    [int]$gates.continuousGraphEnduranceHours -ne 24)
{
    throw 'The 1.1 endurance minimums were weakened.'
}

# Independent pilots are not a 1.1.0 gate: the owner delegated decision 3 to
# option (b) on 2026-10-04 ("do what needs to be done"). The waiver must be
# recorded exactly once, must name the two approval gates it removes, and must
# keep both recovery rehearsals. Reintroducing pilots is allowed only as the
# complete original rule (independentPilots = 2, no waiver, and the Core
# approval track requiring both pilot approvals); any mixture fails closed.
$pilotGateIds = @('application-pilot-a', 'application-pilot-b')
$pilotGateProperty = $gates.PSObject.Properties['independentPilots']
$waiverProperty = $contract.PSObject.Properties['waivedReleaseGates']
$waivers = @(if ($null -ne $waiverProperty) { $waiverProperty.Value })
if ($null -ne $pilotGateProperty)
{
    if (($pilotGateProperty.Value -isnot [long] -and $pilotGateProperty.Value -isnot [int]) -or
        [int]$pilotGateProperty.Value -ne 2 -or $waivers.Count -ne 0)
    {
        throw 'Independent pilots may only be reintroduced as the full two-pilot gate with no recorded waiver.'
    }
}
else
{
    if ($waivers.Count -ne 1)
    {
        throw 'The 1.1 release contract must record exactly one waiver: the owner-delegated independent-pilot waiver.'
    }
    $waiver = $waivers[0]
    $expectedWaiverProperties = @(
        'gate', 'previousRequirement', 'approvalGateIds', 'scope', 'decision', 'delegatedBy',
        'delegatedAt', 'delegationQuote', 'reason', 'retainedGates')
    if (@(Compare-Object $expectedWaiverProperties @($waiver.PSObject.Properties.Name)).Count -ne 0 -or
        [string]$waiver.gate -cne 'independentPilots' -or
        [int]$waiver.previousRequirement -ne 2 -or
        @(Compare-Object $pilotGateIds @($waiver.approvalGateIds) -SyncWindow 0 -CaseSensitive).Count -ne 0 -or
        [string]$waiver.scope -cne [string]$contract.releaseVersion -or
        [string]$waiver.decision -cne 'owner-decisions-20261002 decision 3, option (b)' -or
        [string]$waiver.delegatedBy -cne 'repository owner' -or
        # ConvertFrom-Json parses timestamps, so the exact recorded text is checked on the raw contract.
        (Get-Content -LiteralPath $ContractPath -Raw) -cnotmatch '"delegatedAt":\s*"2026-10-04T00:43:03\+01:00"' -or
        [string]$waiver.delegationQuote -cne 'do what needs to be done' -or
        [string]::IsNullOrWhiteSpace([string]$waiver.reason) -or
        @(Compare-Object @('backupRestoreRehearsal', 'rollbackRehearsal') @($waiver.retainedGates) -SyncWindow 0 -CaseSensitive).Count -ne 0)
    {
        throw 'The independent-pilot waiver does not exactly record the 2026-10-04 owner delegation and the retained rehearsals.'
    }
    foreach ($retained in @($waiver.retainedGates))
    {
        if ($gates.PSObject.Properties[[string]$retained].Value -ne $true)
        {
            throw "The independent-pilot waiver requires retained gate '$retained' to stay enabled."
        }
    }
}
# The retained rehearsals must stay executable for real against the exact candidate.
foreach ($path in @(
        'eng/run-core-recovery-rehearsal.ps1',
        'eng/verify-core-recovery-rehearsal.ps1',
        'eng/test-core-recovery-rehearsal.ps1',
        'eng/CoreRecoveryProbe/CoreRecoveryProbe.csproj',
        'eng/CoreRecoveryProbe/Program.cs',
        'docs/operations/core-recovery-rehearsals.md'))
{
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $path) -PathType Leaf))
    {
        throw "Required 1.1 recovery-rehearsal asset '$path' is missing."
    }
}
Import-Module (Join-Path $PSScriptRoot 'approval-release-tracks.psm1') -Force
$coreApprovalGates = @(Get-ApprovalTrackGateIds -ReleaseTrack Core -ReleaseContractPath $ContractPath)
$corePilotApprovals = @($pilotGateIds | Where-Object { $_ -cin $coreApprovalGates })
if (($null -eq $pilotGateProperty -and $corePilotApprovals.Count -ne 0) -or
    ($null -ne $pilotGateProperty -and $corePilotApprovals.Count -ne $pilotGateIds.Count) -or
    'backup-restore-rehearsal' -cnotin $coreApprovalGates -or
    'rollback-rehearsal' -cnotin $coreApprovalGates)
{
    throw 'The Core approval track does not match the 1.1 independent-pilot rule or dropped a recovery rehearsal.'
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
        'postgresql.yaml', 'postgresql-core.yaml', 'sync-services.yaml', 'streams-job.yaml', 'sync-job.yaml',
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

if ($contract.compatibility.continuousGraphRequiresQualifiedSqlPgqServer -ne $true -or
    @($contract.compatibility.generalPostgreSqlMajors).Count -ne 4 -or
    @(Compare-Object @(15, 16, 17, 18) @($contract.compatibility.generalPostgreSqlMajors) -SyncWindow 0).Count -ne 0 -or
    @($contract.compatibility.previewPostgreSqlMajors).Count -ne 1 -or
    $contract.compatibility.previewPostgreSqlMajors[0] -ne 19 -or
    $gates.postgresql19GaDigestRequiredForCore -ne $false -or
    $gates.graphEvidenceRequiredForCore -ne $false -or
    $contract.publication.stableEnabledBeforeAllGatesPass -ne $false -or
    $contract.publication.nugetTrustedPublishingRequired -ne $true -or
    $contract.publication.npmProvenanceRequired -ne $true -or
    $contract.publication.sbomRequired -ne $true -or
    $contract.publication.dependencyOrderRequired -ne $true)
{
    throw 'The 1.1 compatibility or stable-publication boundary was weakened.'
}

Write-Output (
    "Verified the BlueTusk 1.1 source-version contract: five core release tracks and Graph preview, " +
    "$($nuGetProjects.Count) new NuGet packages, $($npmProjects.Count) new npm packages, " +
    'guarded Kubernetes endurance, required backup/restore and rollback rehearsals, ' +
    $(if ($null -eq $pilotGateProperty) { 'independent pilots waived for 1.1.0 by the 2026-10-04 owner delegation, ' } else { 'two independent pilots, ' }) +
    'and disabled stable publication.')
