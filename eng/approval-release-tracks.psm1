Set-StrictMode -Version Latest

# Resolves the approval gates a release track must bind. The approval contract
# keeps every historical gate definition (1.0.0 Legacy records stay verifiable),
# while each track lists exactly which gates it requires. A gate may only be
# missing from the 1.1.0 Core track when eng/v1.1-release-contract.json records
# the matching waiver; anything else fails closed.
function Get-ApprovalTrackGateIds
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Legacy', 'Core')]
        [string] $ReleaseTrack,

        [string] $ApprovalContractPath = (Join-Path $PSScriptRoot 'v1-approval-evidence-contract.json'),

        [string] $ReleaseContractPath = (Join-Path $PSScriptRoot 'v1.1-release-contract.json')
    )

    $approvalContract = Get-Content -LiteralPath $ApprovalContractPath -Raw | ConvertFrom-Json
    $allGateIds = @($approvalContract.gates | ForEach-Object { [string]$_.id })
    if ($allGateIds.Count -ne 10 -or @($allGateIds | Select-Object -Unique).Count -ne 10)
    {
        throw 'The approval contract must define exactly ten unique gate schemas.'
    }

    $tracks = $approvalContract.PSObject.Properties['releaseTracks']
    if ($null -eq $tracks -or $null -eq $tracks.Value -or
        @($tracks.Value.PSObject.Properties.Name).Count -ne 2 -or
        $null -eq $tracks.Value.PSObject.Properties['Legacy'] -or
        $null -eq $tracks.Value.PSObject.Properties['Core'])
    {
        throw 'The approval contract must declare exactly the Legacy and Core release-track gate lists.'
    }

    $legacy = $tracks.Value.Legacy
    $legacyRequired = @($legacy.requiredGates | ForEach-Object { [string]$_ })
    if (@($legacy.waivedGates).Count -ne 0 -or
        $legacyRequired.Count -ne $allGateIds.Count -or
        @(Compare-Object $allGateIds $legacyRequired -SyncWindow 0 -CaseSensitive).Count -ne 0)
    {
        throw 'The historical 1.0.0 Legacy track must require every approval gate, in contract order, with no waiver.'
    }

    $core = $tracks.Value.Core
    $coreRequired = @($core.requiredGates | ForEach-Object { [string]$_ })
    $coreWaived = @($core.waivedGates | ForEach-Object { [string]$_ })
    $combined = @($coreRequired + $coreWaived)
    if (@($combined | Select-Object -Unique).Count -ne $combined.Count -or
        $combined.Count -ne $allGateIds.Count -or
        @($combined | Where-Object { $_ -cnotin $allGateIds }).Count -ne 0 -or
        @($coreRequired | Where-Object { $_ -cnotin $allGateIds }).Count -ne 0)
    {
        throw 'The Core track must partition the ten approval gates into required and waived gates.'
    }
    $expectedOrder = @($allGateIds | Where-Object { $_ -cin $coreRequired })
    if (@(Compare-Object $expectedOrder $coreRequired -SyncWindow 0 -CaseSensitive).Count -ne 0)
    {
        throw 'The Core track must list its required gates in contract order.'
    }
    foreach ($mandatory in @(
            'independent-release-review', 'security-review', 'website-deployment-acceptance',
            'backup-restore-rehearsal', 'rollback-rehearsal', 'incident-response-game-day',
            'slo-owner-approval', 'maintainer-signoff'))
    {
        if ($mandatory -cnotin $coreRequired)
        {
            throw "The Core track must keep approval gate '$mandatory'; only recorded independent-pilot waivers are allowed."
        }
    }

    # Every Core waiver must be exactly the approval gates of a waiver recorded
    # in the 1.1.0 release contract, and vice versa.
    $releaseContract = Get-Content -LiteralPath $ReleaseContractPath -Raw | ConvertFrom-Json
    $waiverProperty = $releaseContract.PSObject.Properties['waivedReleaseGates']
    $recordedWaivedIds = @()
    if ($null -ne $waiverProperty)
    {
        foreach ($waiver in @($waiverProperty.Value))
        {
            $recordedWaivedIds += @($waiver.approvalGateIds | ForEach-Object { [string]$_ })
        }
    }
    if ($recordedWaivedIds.Count -ne $coreWaived.Count -or
        @(Compare-Object $recordedWaivedIds $coreWaived -CaseSensitive).Count -ne 0)
    {
        throw (
            'Core approval waivers must exactly match the waivers recorded in the 1.1.0 release contract. ' +
            "Contract: $(if ($recordedWaivedIds.Count) { $recordedWaivedIds -join ', ' } else { '<none>' }); " +
            "approval track: $(if ($coreWaived.Count) { $coreWaived -join ', ' } else { '<none>' }).")
    }

    if ($ReleaseTrack -eq 'Legacy') { return $legacyRequired }
    return $coreRequired
}

Export-ModuleMember -Function Get-ApprovalTrackGateIds
