[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$temporaryBase = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
$temporaryRoot = Join-Path $temporaryBase (
    "bluetusk-disturbance-verifier-$([Guid]::NewGuid().ToString('N'))")
$null = New-Item -ItemType Directory -Path $temporaryRoot

try
{
    $example = Get-Content -LiteralPath (
        Join-Path $PSScriptRoot 'v1-operational-disturbance-evidence.example.json'
    ) -Raw | ConvertFrom-Json
    $streamsReportPath = Join-Path $temporaryRoot 'streams/report.json'
    $syncReportPath = Join-Path $temporaryRoot 'sync/report.json'
    $evidencePath = Join-Path (
        $temporaryRoot
    ) 'disturbances/operational-disturbance-evidence.json'
    $null = New-Item -ItemType Directory -Force -Path (
        Split-Path -Parent $streamsReportPath)
    $null = New-Item -ItemType Directory -Force -Path (
        Split-Path -Parent $syncReportPath)
    $null = New-Item -ItemType Directory -Force -Path (
        Split-Path -Parent $evidencePath)

    @{
        completed = $true
        startedAt = '2026-01-01T00:00:00Z'
        completedAt = '2026-01-02T02:00:00Z'
    } | ConvertTo-Json | Set-Content -LiteralPath $streamsReportPath -Encoding utf8NoBOM
    @{
        completed = $true
        startedAt = '2026-01-03T00:00:00Z'
        completedAt = '2026-01-03T21:00:00Z'
    } | ConvertTo-Json | Set-Content -LiteralPath $syncReportPath -Encoding utf8NoBOM

    $streamsHash = (
        Get-FileHash -LiteralPath $streamsReportPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $syncHash = (
        Get-FileHash -LiteralPath $syncReportPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    foreach ($run in @($example.runs))
    {
        $run.enduranceReportSha256 = if ([string]$run.id -eq 'streams')
        {
            $streamsHash
        }
        else
        {
            $syncHash
        }
        foreach ($scenario in @($run.scenarios))
        {
            foreach ($artifact in @($scenario.artifacts))
            {
                $artifactPath = Join-Path $temporaryRoot ([string]$artifact.path)
                $null = New-Item -ItemType Directory -Force -Path (
                    Split-Path -Parent $artifactPath)
                [ordered]@{
                    run = [string]$run.id
                    scenario = [string]$scenario.id
                    role = [string]$artifact.role
                    observedAt = [string]$scenario.completedAt
                    result = 'passed'
                } | ConvertTo-Json | Set-Content `
                    -LiteralPath $artifactPath `
                    -Encoding utf8NoBOM
                $artifact.sha256 = (
                    Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256
                ).Hash.ToLowerInvariant()
            }
        }
    }
    $example | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM

    $arguments = @{
        EvidencePath = $evidencePath
        EvidenceRoot = $temporaryRoot
        ExpectedCommit = [string]$example.candidateCommit
        ExpectedPackageManifestSha256 = [string]$example.packageManifestSha256
        ExpectedPackageProvenanceSha256 = [string]$example.packageProvenanceSha256
        StreamsReportPath = $streamsReportPath
        ExpectedStreamsReportSha256 = $streamsHash
        SyncReportPath = $syncReportPath
        ExpectedSyncReportSha256 = $syncHash
    }
    & (Join-Path $PSScriptRoot 'verify-endurance-disturbance-evidence.ps1') `
        @arguments | Out-Null

    $example.runs[0].scenarios[0].continuityVerified = $false
    $example | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM
    $rejected = $false
    try
    {
        & (Join-Path $PSScriptRoot 'verify-endurance-disturbance-evidence.ps1') `
            @arguments | Out-Null
    }
    catch
    {
        if ($_.Exception.Message -notmatch "continuityVerified")
        {
            throw
        }
        $rejected = $true
    }
    if (-not $rejected)
    {
        throw 'The disturbance verifier accepted a failed continuity assertion.'
    }

    $example.runs[0].scenarios[0].continuityVerified = $true
    $example | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM
    $tamperedPath = Join-Path $temporaryRoot (
        [string]$example.runs[0].scenarios[0].artifacts[0].path)
    'tampered' | Set-Content -LiteralPath $tamperedPath -Encoding utf8NoBOM
    $rejected = $false
    try
    {
        & (Join-Path $PSScriptRoot 'verify-endurance-disturbance-evidence.ps1') `
            @arguments | Out-Null
    }
    catch
    {
        if ($_.Exception.Message -notmatch 'does not match its SHA-256')
        {
            throw
        }
        $rejected = $true
    }
    if (-not $rejected)
    {
        throw 'The disturbance verifier accepted a modified observation artifact.'
    }

    Write-Output (
        'Endurance-disturbance verifier self-test passed: valid evidence accepted; ' +
        'failed continuity and modified artifact rejected.')
}
finally
{
    $resolvedRoot = (Resolve-Path -LiteralPath $temporaryRoot).Path
    $temporaryPrefix = $temporaryBase.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith(
            $temporaryPrefix,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $resolvedRoot).StartsWith(
            'bluetusk-disturbance-verifier-',
            [StringComparison]::Ordinal))
    {
        throw "Refusing to remove unexpected verifier directory '$resolvedRoot'."
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
