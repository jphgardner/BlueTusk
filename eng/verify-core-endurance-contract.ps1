[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string] $ConfigurationPath = (Join-Path $PSScriptRoot 'v1.1-candidate-readiness.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$contract = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
$core = @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane')
if ($contract.schemaVersion -ne 1 -or $contract.candidateEvidenceSchemaVersion -ne 4 -or
    $contract.releaseVersion -cne '1.1.0' -or $contract.scope -cne 'Core' -or
    $contract.status -cne 'aggregation-migration-in-progress' -or
    $contract.publicationEnabled -ne $false -or @($contract.coreFamilies).Count -ne 5 -or
    (Compare-Object $core @($contract.coreFamilies)))
{ throw 'Core evidence contract identity, family scope or unqualified-publication boundary changed.' }
$minimums = $contract.minimums
if ($minimums.streamsEnduranceHours -ne 72 -or $minimums.streamsMinimumTransactions -ne 100000 -or
    $minimums.syncEnduranceHours -ne 24 -or $minimums.syncMinimumCycles -ne 100 -or
    $minimums.liveAndControlPlaneEnduranceHours -ne 24 -or $minimums.liveAndControlPlaneMinimumCycles -ne 100000)
{ throw 'Core endurance durations or operation minimums changed.' }
$image = [string]$contract.endurancePostgreSqlImage
if ($image -notmatch '^postgres:(?:15|16|17|18)(?:\.\d+)?-alpine@sha256:[0-9a-f]{64}$')
{ throw 'Core endurance requires a stable, digest-pinned PostgreSQL image.' }
$compose = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'eng/compose/postgres.yml') -Raw
if (-not $compose.Contains("image: $image", [StringComparison]::Ordinal))
{ throw 'The core endurance image is not present in the Compose service.' }
foreach ($name in @('streams', 'sync'))
{
    $workflow = Get-Content -LiteralPath (Join-Path $RepositoryRoot ".github/workflows/$name-release-endurance.yml") -Raw
    $runner = Get-Content -LiteralPath (Join-Path $RepositoryRoot "deploy/kubernetes/endurance/run-$name.ps1") -Raw
    foreach ($source in @($workflow, $runner))
    {
        if (-not $source.Contains($image, [StringComparison]::Ordinal) -or
            -not $source.Contains('-ReleaseTrack Core', [StringComparison]::Ordinal) -or
            $source -match 'postgres:19|Port=5419')
        { throw "$name core endurance must bind its stable image and explicit Core verifier scope." }
    }
    if (-not $runner.Contains('Host=postgresql-core;', [StringComparison]::Ordinal))
    { throw "$name Kubernetes runner is not isolated from historical Graph storage." }
}
$database = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'deploy/kubernetes/endurance/postgresql-core.yaml') -Raw
if (-not $database.Contains($image, [StringComparison]::Ordinal) -or
    $database -notmatch '(?m)^  name: postgresql-core\s*$' -or
    $database -notmatch '(?m)^  serviceName: postgresql-core\s*$')
{ throw 'The core Kubernetes database must have its own service, StatefulSet and pinned stable image.' }
$preview = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'deploy/kubernetes/endurance/continuous-graph-preview-job.yaml') -Raw
if (-not $preview.Contains('name: endurance-graph-preview-candidate', [StringComparison]::Ordinal) -or
    $preview -match '(?m)^\s+name: endurance-candidate\s*$')
{ throw 'Graph preview must not consume the core candidate ConfigMap.' }
$launcher = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'eng/deploy-kubernetes-endurance.ps1') -Raw
foreach ($binding in @('postgresql-core.yaml', 'statefulset/postgresql-core', "Set-CandidateConfig -Name 'endurance-graph-preview-candidate'"))
{
    if (-not $launcher.Contains($binding, [StringComparison]::Ordinal))
    { throw "The endurance launcher lacks independent core/preview binding '$binding'." }
}
$live = Get-Content -LiteralPath (Join-Path $RepositoryRoot '.github/workflows/live-control-plane-release-endurance.yml') -Raw
if ($live -notmatch "-Duration '1.00:00:00'" -or $live -notmatch '-MinimumCycles 100000')
{ throw 'Combined Live/Control Plane endurance must retain its 24-hour and 100,000-cycle floor.' }
$build = Get-Content -LiteralPath (Join-Path $RepositoryRoot '.github/workflows/build.yml') -Raw
if ($build -notmatch 'major: \[15, 16, 17, 18\]' -or
    $build -notmatch '(?m)^  core-candidate-packages:' -or
    $build -notmatch '-ReleaseTrack Core' -or
    $build -match 'verify-v1-production-readiness.ps1')
{ throw 'The core build must retain stable compatibility, independent packaging and scoped track validation.' }
Write-Output 'Verified core evidence producer contract and isolated Graph preview. Final remote aggregation is still pending; this is NOT a release approval.'
