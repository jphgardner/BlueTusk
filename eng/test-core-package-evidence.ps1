[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$source = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$verifier = Join-Path $PSScriptRoot 'verify-v1-package-evidence.ps1'
$scratch = Join-Path $repositoryRoot "artifacts/core-package-self-test-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch
try
{
    # This positive case uses real packages and must not arm a prerelease train.
    & $verifier -ReleaseTrack Core -EvidenceRoot $source -ExpectedCommit $ExpectedCommit | Out-Null
    $mutations = @(
        @{ Name = 'graph-family'; Error = 'exactly every product family'; Change = {param($m) $m.families[0].id = 'ContinuousGraph'} },
        @{ Name = 'wrong-version'; Error = 'Core 1.2.0'; Change = {param($m) $m.releaseVersion = '1.1.0'} },
        @{ Name = 'wrong-commit'; Error = 'Package evidence commit'; Change = {param($m) $m.sourceCommit = ('0' * 40)} },
        @{ Name = 'wrong-artifact-count'; Error = 'artifact count mismatch'; Change = {param($m) $m.artifactCount++} },
        @{ Name = 'wrong-package-hash'; Error = 'does not match its hash'; Change = {param($m) $m.artifacts[0].sha256 = ('0' * 64)} },
        @{ Name = 'wrong-package-bytes'; Error = 'does not match its hash'; Change = {param($m) $m.artifacts[0].bytes++} },
        @{ Name = 'wrong-provenance-hash'; Error = 'does not match its integrity record'; Change = {param($m) $m.supplyChain.provenance.sha256 = ('0' * 64)} },
        @{ Name = 'wrong-artifact-family'; Error = 'unknown family'; Change = {param($m) $m.artifacts[0].family = 'ContinuousGraph'} }
    )
    foreach ($mutation in $mutations)
    {
        $caseRoot = Join-Path $scratch $mutation.Name
        $null = New-Item -ItemType Directory -Path $caseRoot
        Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $caseRoot -Recurse
        }
        $path = Join-Path $caseRoot 'package-manifest.json'
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        & $mutation.Change $manifest
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
        $failure = $null
        try { & $verifier -ReleaseTrack Core -EvidenceRoot $caseRoot -ExpectedCommit $ExpectedCommit | Out-Null }
        catch { $failure = $_.Exception.Message }
        if ($null -eq $failure -or -not $failure.Contains($mutation.Error, [StringComparison]::OrdinalIgnoreCase))
        { throw "Invalid package fixture '$($mutation.Name)' did not fail at the expected guard: $failure" }
    }
    Write-Output 'Core package-reader self-test passed against retained real packages: stable 1.2.0 accepted without prerelease arming; eight identity, scope or integrity mutations rejected.'
}
finally
{
    $resolved = [IO.Path]::GetFullPath($scratch)
    $prefix = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('core-package-self-test-', [StringComparison]::Ordinal))
    { throw 'Refusing unexpected package self-test cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
