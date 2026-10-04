[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-core-approvals-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$examples = (Get-Content (Join-Path $PSScriptRoot 'v1-approval-evidence.examples.json') -Raw | ConvertFrom-Json).examples
$verifier = Join-Path $PSScriptRoot 'verify-v1-approval-evidence-set.ps1'
function Write-CoreFixture([string] $Name, [scriptblock] $Mutate)
{
    $path = Join-Path $root $Name
    [IO.Directory]::CreateDirectory($path) | Out-Null
    # 1.1.0 Core waives both independent pilots (owner delegation 2026-10-04,
    # eng/v1.1-release-contract.json waivedReleaseGates); the other eight gates,
    # including backup/restore and rollback rehearsals, stay required.
    $records = @($examples | ConvertTo-Json -Depth 20 | ConvertFrom-Json |
        Where-Object { $_.gateId -cnotlike 'application-pilot-*' })
    foreach ($record in $records)
    {
        if ($record.gateId -eq 'independent-release-review') { $record.details.packageFamiliesReviewed = 5 }
        if ($record.gateId -eq 'maintainer-signoff')
        {
            $record.details.versions = @('Provider 1.1.0', 'Streams 1.1.0', 'Sync 1.1.0', 'Live 1.1.0', 'Control Plane 1.1.0')
            $record.details.publishedPrereleaseTags = 5
            $record.details.publishedPrereleaseFamilies = @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane')
        }
    }
    if ($null -ne $Mutate)
    {
        $replacement = & $Mutate $records
        if ($null -ne $replacement) { $records = @($replacement) }
    }
    foreach ($record in $records)
    {
        $record | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $path "$($record.gateId).json") -Encoding utf8
    }
    return $path
}
try
{
    $positive = Write-CoreFixture 'positive' $null
    & $verifier -EvidenceDirectory $positive -ExpectedCommit ('0' * 40) -ExpectedWebsiteProductionMetricsSha256 ('0' * 64) -ReleaseTrack Core | Out-Null
    $pilotA = @($examples | Where-Object gateId -ceq 'application-pilot-a')[0]
    $pilotB = @($examples | Where-Object gateId -ceq 'application-pilot-b')[0]
    $mutations = @(
        { param($records) @($records) + @($pilotA, $pilotB) },
        { param($records) @($records) + @($pilotA) },
        { param($records) @($records | Where-Object gateId -cne 'backup-restore-rehearsal') },
        { param($records) @($records | Where-Object gateId -cne 'rollback-rehearsal') },
        { param($records) ($records | Where-Object gateId -eq 'backup-restore-rehearsal').details.restoredRowCount = 999 },
        { param($records) ($records | Where-Object gateId -eq 'rollback-rehearsal').details.dataLossEvents = 1 },
        { param($records) ($records | Where-Object gateId -eq 'independent-release-review').details.reviewerIndependent = $false },
        { param($records) ($records | Where-Object gateId -eq 'maintainer-signoff').details.versions[0] = 'Provider 1.0.0' },
        { param($records) ($records | Where-Object gateId -eq 'maintainer-signoff').details.publishedPrereleaseFamilies[0] = 'ContinuousGraph' },
        { param($records) ($records | Where-Object gateId -eq 'security-review').approvedUtc = '2026-01-02T00:00:00Z' }
    )
    $case = 0
    foreach ($mutation in $mutations)
    {
        $path = Write-CoreFixture "invalid-$case" $mutation
        $failure = $null
        try { & $verifier -EvidenceDirectory $path -ExpectedCommit ('0' * 40) -ExpectedWebsiteProductionMetricsSha256 ('0' * 64) -ReleaseTrack Core | Out-Null }
        catch { $failure = $_.Exception.Message }
        if ($null -eq $failure) { throw "Invalid core approval case $case was accepted." }
        $case++
    }
    foreach ($pilot in @($pilotA, $pilotB))
    {
        $single = Join-Path $root "single-$($pilot.gateId).json"
        $pilot | ConvertTo-Json -Depth 20 | Set-Content $single -Encoding utf8
        $failure = $null
        try { & (Join-Path $PSScriptRoot 'verify-v1-approval-evidence.ps1') -EvidencePath $single -ExpectedGateId $pilot.gateId -ExpectedCommit ('0' * 40) -ReleaseTrack Core | Out-Null }
        catch { $failure = $_.Exception.Message }
        if ($failure -notmatch 'not required by the Core release track') { throw "Waived Core pilot '$($pilot.gateId)' was accepted as a single record: $failure" }
        $case++
    }

    # The approval-track lists and the 1.1.0 release-contract waiver must agree;
    # tampering with either side fails closed.
    Import-Module (Join-Path $PSScriptRoot 'approval-release-tracks.psm1') -Force
    $approvalContractText = Get-Content (Join-Path $PSScriptRoot 'v1-approval-evidence-contract.json') -Raw
    $releaseContractText = Get-Content (Join-Path $PSScriptRoot 'v1.1-release-contract.json') -Raw
    $coreGates = @(Get-ApprovalTrackGateIds -ReleaseTrack Core)
    if ($coreGates.Count -ne 8 -or 'application-pilot-a' -cin $coreGates -or 'application-pilot-b' -cin $coreGates -or
        'backup-restore-rehearsal' -cnotin $coreGates -or 'rollback-rehearsal' -cnotin $coreGates)
    { throw 'The Core approval track does not require exactly the eight non-pilot gates.' }
    if (@(Get-ApprovalTrackGateIds -ReleaseTrack Legacy).Count -ne 10)
    { throw 'The historical Legacy approval track no longer requires all ten gates.' }
    $consistencyCases = @(
        @{ Name = 'waiver-removed-from-release-contract'; Approval = { param($c) }; Release = { param($r) $r.PSObject.Properties.Remove('waivedReleaseGates') } },
        @{ Name = 'pilots-reinstated-without-contract'; Approval = { param($c) $c.releaseTracks.Core.requiredGates = @($c.releaseTracks.Legacy.requiredGates); $c.releaseTracks.Core.waivedGates = @() }; Release = { param($r) } },
        @{ Name = 'rollback-rehearsal-waived'; Approval = { param($c) $c.releaseTracks.Core.requiredGates = @($c.releaseTracks.Core.requiredGates | Where-Object { $_ -cne 'rollback-rehearsal' }); $c.releaseTracks.Core.waivedGates += 'rollback-rehearsal' }; Release = { param($r) $r.waivedReleaseGates[0].approvalGateIds += 'rollback-rehearsal' } },
        @{ Name = 'backup-restore-rehearsal-dropped'; Approval = { param($c) $c.releaseTracks.Core.requiredGates = @($c.releaseTracks.Core.requiredGates | Where-Object { $_ -cne 'backup-restore-rehearsal' }) }; Release = { param($r) } },
        @{ Name = 'legacy-history-rewritten'; Approval = { param($c) $c.releaseTracks.Legacy.requiredGates = @($c.releaseTracks.Core.requiredGates); $c.releaseTracks.Legacy.waivedGates = @('application-pilot-a', 'application-pilot-b') }; Release = { param($r) } }
    )
    foreach ($consistency in $consistencyCases)
    {
        $approvalCopy = $approvalContractText | ConvertFrom-Json
        $releaseCopy = $releaseContractText | ConvertFrom-Json
        & $consistency.Approval $approvalCopy
        & $consistency.Release $releaseCopy
        $approvalPath = Join-Path $root "$($consistency.Name)-approval.json"
        $releasePath = Join-Path $root "$($consistency.Name)-release.json"
        $approvalCopy | ConvertTo-Json -Depth 20 | Set-Content $approvalPath -Encoding utf8
        $releaseCopy | ConvertTo-Json -Depth 20 | Set-Content $releasePath -Encoding utf8
        $failure = $null
        try { Get-ApprovalTrackGateIds -ReleaseTrack Core -ApprovalContractPath $approvalPath -ReleaseContractPath $releasePath | Out-Null }
        catch { $failure = $_.Exception.Message }
        if ($null -eq $failure) { throw "Inconsistent approval-track case '$($consistency.Name)' was accepted." }
        $case++
    }
    Write-Output "Core approval self-test passed: eight synthetic approval records without Graph or the waived independent pilots, and $case rejected reintroduced-pilot, missing-rehearsal, failed-rehearsal, non-independent, wrong-version or stale-review cases."
}
finally
{
    $absolute = [IO.Path]::GetFullPath($root)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($absolute).StartsWith('bluetusk-core-approvals-', [StringComparison]::Ordinal))
    {
        throw 'Refusing to remove an unexpected approval test directory.'
    }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
