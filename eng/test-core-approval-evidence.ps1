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
    $records = @($examples | ConvertTo-Json -Depth 20 | ConvertFrom-Json)
    foreach ($record in $records)
    {
        if ($record.gateId -in @('application-pilot-a', 'application-pilot-b'))
        {
            $record.details.enabledProductFamilies = @($record.details.enabledProductFamilies | Where-Object { $_ -ne 'ContinuousGraph' })
        }
        if ($record.gateId -eq 'independent-release-review') { $record.details.packageFamiliesReviewed = 5 }
        if ($record.gateId -eq 'maintainer-signoff')
        {
            $record.details.versions = @('Provider 1.1.0', 'Streams 1.1.0', 'Sync 1.1.0', 'Live 1.1.0', 'Control Plane 1.1.0')
            $record.details.publishedPrereleaseTags = 5
            $record.details.publishedPrereleaseFamilies = @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane')
        }
    }
    if ($null -ne $Mutate) { & $Mutate $records }
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
    $mutations = @(
        { param($records) foreach ($record in $records | Where-Object { $_.gateId -like 'application-pilot-*' }) { $record.details.enabledProductFamilies = @($record.details.enabledProductFamilies | Where-Object { $_ -ne 'ControlPlane' }) } },
        { param($records) ($records | Where-Object gateId -eq 'application-pilot-a').details.enabledProductFamilies += 'ContinuousGraph' },
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
    Write-Output "Core approval self-test passed: ten synthetic approval records without Graph, and $case rejected incomplete, preview, non-independent, wrong-version or stale-review sets."
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
