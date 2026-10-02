[CmdletBinding()]
param(
    [ValidateSet('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')]
    [string] $Family,
    [ValidateSet('stable', 'preview')]
    [string] $Channel = 'stable',
    [string] $TrackPath = (Join-Path $PSScriptRoot 'release-tracks.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tracks = Get-Content -LiteralPath $TrackPath -Raw | ConvertFrom-Json
$core = @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane')
if ($tracks.schemaVersion -ne 1 -or $tracks.releaseVersion -cne '1.1.0' -or
    @($tracks.stableFamilies).Count -ne $core.Count -or
    @(Compare-Object $core @($tracks.stableFamilies) -SyncWindow 0).Count -ne 0 -or
    @($tracks.previewFamilies).Count -ne 1 -or $tracks.previewFamilies[0] -cne 'ContinuousGraph' -or
    @($tracks.stablePostgreSqlMajors).Count -ne 4 -or
    @(Compare-Object @(15, 16, 17, 18) @($tracks.stablePostgreSqlMajors) -SyncWindow 0).Count -ne 0 -or
    @($tracks.previewPostgreSqlMajors).Count -ne 1 -or $tracks.previewPostgreSqlMajors[0] -ne 19 -or
    $tracks.postgresql19RequiredForCorePublication -ne $false -or
    $tracks.graphEvidenceRequiredForCorePublication -ne $false)
{
    throw 'Release tracks must retain five core families, isolated Graph preview, and explicit stable/preview PostgreSQL support.'
}
$requirements = @('supported-server-release-and-image-digest', 'sql-pgq-capability-probe',
    'exact-candidate-differential-security-and-recovery-tests', 'exact-candidate-performance-evidence',
    '24-hour-endurance', 'independent-release-approval')
if ($tracks.graph.stablePublicationEligible -ne $false -or
    $tracks.graph.requiredServerCapability -cne 'SQL/PGQ GRAPH_TABLE' -or
    $tracks.graph.versionNumberAloneEstablishesSupport -ne $false -or
    @($tracks.graph.qualificationRequirements).Count -ne $requirements.Count -or
    @(Compare-Object $requirements @($tracks.graph.qualificationRequirements) -SyncWindow 0).Count -ne 0 -or
    $tracks.graph.historicalPreviewMilestone -cne '19beta3')
{
    throw 'Graph must remain preview-only until a supported SQL/PGQ server and its separate release evidence are qualified.'
}
if ($Family -eq 'ContinuousGraph' -and $Channel -eq 'stable')
{
    throw 'ContinuousGraph stable publication is unavailable: qualify a supported SQL/PGQ server and the independent Graph release track first.'
}
$families = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'product-families.json') -Raw | ConvertFrom-Json
foreach ($coreFamily in $core)
{
    foreach ($dependency in @($families.families.PSObject.Properties[$coreFamily].Value.releaseDependencies))
    {
        if ($dependency -notin $core)
        {
            throw "Core family '$coreFamily' must not depend on the Graph preview release track."
        }
    }
}
if ([string]::IsNullOrWhiteSpace($Family))
{
    Write-Output 'Verified five core stable release tracks and the retained, non-blocking ContinuousGraph preview track.'
}
else
{
    Write-Output "Verified release-track policy for $Family ($Channel); publication still requires its exact-candidate gates."
}
