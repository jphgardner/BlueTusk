[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][ValidateSet('Regression', 'Compatibility')][string] $Kind
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-test-evidence.psm1') -Force
$root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$name = "core-$($Kind.ToLowerInvariant())-manifest.json"
$path = Join-Path $root $name
if (Test-Path -LiteralPath $path) { throw 'Existing Core test manifests are never overwritten.' }
$ids = if ($Kind -eq 'Regression') { @('linux-x64', 'windows-x64') } else { @(15, 16, 17, 18 | ForEach-Object { "postgresql-$_" }) }
$shards = @($ids | ForEach-Object { [pscustomobject]@{ id = $_; manifest = New-CoreTestArtifact $root "runs/$_/core-test-shard.json" } })
# Validate all payloads before creating the public manifest; failure leaves raw captures intact.
foreach ($id in $ids)
{
    $environment = if ($Kind -eq 'Regression') { $id } else { 'linux-x64' }
    $major = if ($Kind -eq 'Regression') { 0 } else { [int]$id.Substring('postgresql-'.Length) }
    $null = Get-CoreTestShardReport (Join-Path $root "runs/$id/core-test-shard.json") $ExpectedCommit $Kind $environment $major
}
$manifest = [pscustomobject]@{ schemaVersion = 1; scope = 'Core'; releaseVersion = '1.1.0'; sourceCommit = $ExpectedCommit; kind = $Kind; shards = $shards }
$stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try
{
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 10) + [Environment]::NewLine)
    $stream.Write($bytes)
}
finally { $stream.Dispose() }
Get-CoreTestManifestReport $path $ExpectedCommit $Kind
