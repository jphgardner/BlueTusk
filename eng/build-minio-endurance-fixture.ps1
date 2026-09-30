[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts/minio-endurance-fixture',
    [ValidatePattern('^[a-z0-9][a-z0-9./:-]+$')]
    [string] $ImageTag = 'bluetusk/endurance-minio:release-20250907'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$artifacts = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (-not $output.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) { throw 'Fixture capture requires a new directory under artifacts.' }
$null = New-Item -ItemType Directory -Path $output
$metadata = Join-Path $output 'build-metadata.json'
& docker buildx build --platform linux/amd64 --load --provenance=false --metadata-file $metadata `
    --tag $ImageTag (Join-Path $PSScriptRoot 'compose/minio') 2>&1 | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw 'The checksum-verified MinIO fixture build failed.' }
$digest = [string](Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json).'containerimage.digest'
if ($digest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw 'BuildKit did not record an immutable image digest.' }
$reference = "$ImageTag@$digest"
$inspection = & docker image inspect $reference
if ($LASTEXITCODE -ne 0) { throw 'Digest-addressed local images require the Docker containerd image store.' }
$inspection | Set-Content (Join-Path $output 'image-inspection.json') -Encoding utf8NoBOM
$version = & docker run --rm --entrypoint /usr/local/bin/minio $reference --version
if ($LASTEXITCODE -ne 0 -or ($version -join "`n") -notmatch 'RELEASE\.2025-09-07T16-13-09Z')
{ throw 'The built fixture does not identify the expected upstream release.' }
$version | Set-Content (Join-Path $output 'version.txt') -Encoding utf8NoBOM
@{imageReference=$reference;upstreamBinarySha256='7c5bd8512c6e966455b1d198209358b2d191c77a83ab377c4073281065fb855f';
  upstreamBinaryUrl='https://github.com/minio/minio/releases/download/RELEASE.2025-09-07T16-13-09Z/minio.linux-amd64.RELEASE.2025-09-07T16-13-09Z';
  sourceCommit=(& git -C $root rev-parse HEAD).Trim();createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O')} |
    ConvertTo-Json | Set-Content (Join-Path $output 'fixture-provenance.json') -Encoding utf8NoBOM
$reference
