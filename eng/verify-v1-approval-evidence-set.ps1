[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidenceDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $ExpectedWebsiteProductionMetricsSha256,

    [DateTimeOffset] $NotBeforeUtc = [DateTimeOffset]::MinValue,

    [ValidateSet('Legacy', 'Core')]
    [string] $ReleaseTrack = 'Legacy'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$directory = (Resolve-Path -LiteralPath $EvidenceDirectory).Path
if (-not (Test-Path -LiteralPath $directory -PathType Container))
{
    throw "Approval-evidence directory '$directory' does not exist."
}

# The historical 1.0.0 Legacy track binds all ten gates. The 1.1.0 Core track
# binds the eight gates left after the recorded independent-pilot waiver
# (eng/v1.1-release-contract.json waivedReleaseGates); pilot files are rejected.
Import-Module (Join-Path $PSScriptRoot 'approval-release-tracks.psm1') -Force
$gateIds = @(Get-ApprovalTrackGateIds -ReleaseTrack $ReleaseTrack)
$pilotsRequired = 'application-pilot-a' -cin $gateIds -and 'application-pilot-b' -cin $gateIds
if (-not $pilotsRequired -and
    (('application-pilot-a' -cin $gateIds) -or ('application-pilot-b' -cin $gateIds)))
{
    throw 'Independent application pilots must be required or waived together.'
}

$files = @(Get-ChildItem -LiteralPath $directory -Recurse -File)
$subdirectories = @(Get-ChildItem -LiteralPath $directory -Recurse -Directory)
$expectedNames = @($gateIds | ForEach-Object { "$_.json" })
$unexpectedFiles = @($files | Where-Object {
    $_.Name -notin $expectedNames -or $_.DirectoryName -ne $directory
})
$missingNames = @($expectedNames | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $directory $_) -PathType Leaf)
})
if ($files.Count -ne $expectedNames.Count -or
    $subdirectories.Count -ne 0 -or
    $unexpectedFiles.Count -ne 0 -or
    $missingNames.Count -ne 0)
{
    throw (
        "The approval directory must contain exactly the $($gateIds.Count) canonical $ReleaseTrack JSON files. " +
        "Missing: $(if ($missingNames.Count) { $missingNames -join ', ' } else { '<none>' }); " +
        "unexpected: $(if ($unexpectedFiles.Count) { $unexpectedFiles.FullName -join ', ' } else { '<none>' }); " +
        "subdirectories: $(if ($subdirectories.Count) { $subdirectories.FullName -join ', ' } else { '<none>' }).")
}

$verifier = Join-Path $PSScriptRoot 'verify-v1-approval-evidence.ps1'
$approvals = @{}
foreach ($gateId in $gateIds)
{
    $path = Join-Path $directory "$gateId.json"
    & $verifier `
        -EvidencePath $path `
        -ExpectedGateId $gateId `
        -ExpectedCommit $ExpectedCommit `
        -ReleaseTrack $ReleaseTrack `
        -NotBeforeUtc $NotBeforeUtc | Out-Null
    $approvals[$gateId] = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

if ($pilotsRequired)
{
    $pilotA = $approvals['application-pilot-a']
    $pilotB = $approvals['application-pilot-b']
    foreach ($identityField in @('applicationName', 'operatorOrganisation'))
    {
        if ([string]::Equals(
                [string]$pilotA.details.$identityField,
                [string]$pilotB.details.$identityField,
                [StringComparison]::OrdinalIgnoreCase))
        {
            throw (
                "Application pilots A and B must have distinct '$identityField' values.")
        }
    }
    if ([string]::Equals(
            [string]$pilotA.approvedBy,
            [string]$pilotB.approvedBy,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw 'Application pilots A and B must have distinct accountable approvers.'
    }
}

$requiredFamilies = @(
    'Provider',
    'Streams',
    'Sync',
    'Live',
    'ControlPlane',
    'ContinuousGraph'
)
if ($ReleaseTrack -eq 'Core')
{
    & (Join-Path $PSScriptRoot 'verify-release-track.ps1') | Out-Null
    $tracks = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-tracks.json') -Raw | ConvertFrom-Json
    $requiredFamilies = @($tracks.stableFamilies)
    $signoff = $approvals['maintainer-signoff'].details
    $versions = @('Provider 1.1.0', 'Streams 1.1.0', 'Sync 1.1.0', 'Live 1.1.0', 'Control Plane 1.1.0')
    if (@($signoff.versions).Count -ne 5 -or
        @(Compare-Object $versions @($signoff.versions) -CaseSensitive).Count -ne 0 -or
        @($signoff.publishedPrereleaseFamilies).Count -ne 5 -or
        @(Compare-Object $requiredFamilies @($signoff.publishedPrereleaseFamilies) -CaseSensitive).Count -ne 0)
    {
        throw 'Core maintainer sign-off must identify exactly the five 1.1.0 stable versions and five core prerelease families.'
    }
}
if ($pilotsRequired)
{
    $pilotFamilies = @(
        @($pilotA.details.enabledProductFamilies) +
        @($pilotB.details.enabledProductFamilies) |
            ForEach-Object { [string]$_ } |
            Sort-Object -Unique
    )
    $unknownPilotFamilies = @(
        $pilotFamilies | Where-Object { $_ -notin $requiredFamilies }
    )
    $missingPilotFamilies = @(
        $requiredFamilies | Where-Object { $_ -notin $pilotFamilies }
    )
    if ($unknownPilotFamilies.Count -ne 0 -or $missingPilotFamilies.Count -ne 0)
    {
        throw (
            "Application pilots must collectively cover exactly the $ReleaseTrack product " +
            "families. Missing: " +
            "$(if ($missingPilotFamilies.Count) { $missingPilotFamilies -join ', ' } else { '<none>' }); " +
            "unknown: " +
            "$(if ($unknownPilotFamilies.Count) { $unknownPilotFamilies -join ', ' } else { '<none>' }).")
    }
    if ($ReleaseTrack -eq 'Legacy' -and
        'ContinuousGraph' -notin @($pilotA.details.enabledProductFamilies) -and
        'ContinuousGraph' -notin @($pilotB.details.enabledProductFamilies))
    {
        throw 'At least one independent application pilot must exercise ContinuousGraph.'
    }
}

$websiteApproval = $approvals['website-deployment-acceptance']
if (-not [string]::Equals(
        [string]$websiteApproval.details.productionMetricsSha256,
        $ExpectedWebsiteProductionMetricsSha256,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw (
        'Website deployment acceptance does not identify the exact archived ' +
        'production-metrics record.')
}

$operationalGateIds = @(
    $gateIds |
        Where-Object { $_ -notin @('independent-release-review', 'maintainer-signoff') }
)
$latestOperationalApprovalUtc = [DateTimeOffset]::MinValue
foreach ($gateId in $operationalGateIds)
{
    $approvedUtc = [DateTimeOffset]$approvals[$gateId].approvedUtc
    if ($approvedUtc -gt $latestOperationalApprovalUtc)
    {
        $latestOperationalApprovalUtc = $approvedUtc
    }
}
$independentReviewUtc = [DateTimeOffset]$approvals[
    'independent-release-review'
].approvedUtc
if ($independentReviewUtc -lt $latestOperationalApprovalUtc)
{
    throw (
        'Independent release review must not predate any operational, security, ' +
        'pilot, recovery, game-day, or SLO approval required by the release track.')
}
$maintainerSignoffUtc = [DateTimeOffset]$approvals['maintainer-signoff'].approvedUtc
$latestPreSignoffApprovalUtc = $independentReviewUtc
foreach ($gateId in $operationalGateIds)
{
    $approvedUtc = [DateTimeOffset]$approvals[$gateId].approvedUtc
    if ($approvedUtc -gt $latestPreSignoffApprovalUtc)
    {
        $latestPreSignoffApprovalUtc = $approvedUtc
    }
}
if ($maintainerSignoffUtc -lt $latestPreSignoffApprovalUtc)
{
    throw 'Maintainer sign-off must be the final V1 approval decision.'
}

$pilotSummary = if ($pilotsRequired)
{
    "two independent pilots covering the $ReleaseTrack families"
}
else
{
    'independent pilots waived for 1.1.0 by the recorded owner delegation, both recovery rehearsals bound'
}
Write-Output (
    "$ReleaseTrack approval-evidence set passed: $($gateIds.Count) gate-specific records, " +
    "$pilotSummary, exact website " +
    'production-metrics binding, and ' +
    'ordered independent review and maintainer sign-off.')
