[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$verifier = Join-Path $PSScriptRoot 'verify-release-track.ps1'
$tracks = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-tracks.json') -Raw
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-track-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try
{
    foreach ($family in @('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane'))
    {
        & $verifier -Family $family | Out-Null
    }
    & $verifier -Family ContinuousGraph -Channel preview | Out-Null
    $graphFailure = $null
    try { & $verifier -Family ContinuousGraph | Out-Null }
    catch { $graphFailure = $_.Exception.Message }
    if ($null -eq $graphFailure -or -not $graphFailure.Contains('stable publication is unavailable'))
    {
        throw 'An unsupported Graph stable release was not rejected.'
    }
    $mutations = @(
        { param($value) $value.stableFamilies += 'ContinuousGraph' },
        { param($value) $value.previewFamilies = @() },
        { param($value) $value.stablePostgreSqlMajors += 19 },
        { param($value) $value.graphEvidenceRequiredForCorePublication = $true },
        { param($value) $value.postgresql19RequiredForCorePublication = $true },
        { param($value) $value.graph.stablePublicationEligible = $true },
        { param($value) $value.graph.versionNumberAloneEstablishesSupport = $true },
        { param($value) $value.graph.qualificationRequirements = @('sql-pgq-capability-probe') }
    )
    $case = 0
    foreach ($mutation in $mutations)
    {
        $changed = $tracks | ConvertFrom-Json -AsHashtable
        & $mutation $changed
        $path = Join-Path $temporaryRoot "case-$case.json"
        $changed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
        $failure = $null
        try { & $verifier -TrackPath $path | Out-Null }
        catch { $failure = $_.Exception.Message }
        if ($null -eq $failure) { throw "Invalid release track case $case was accepted." }
        $case++
    }
    Write-Output "Release-track self-test passed: five core stable families, Graph preview, rejected Graph stable, and $case rejected policy mutations."
}
finally
{
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedRoot).StartsWith('bluetusk-track-tests-', [StringComparison]::Ordinal)))
    {
        throw 'Refusing to remove a test directory outside the explicit temporary test root.'
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
