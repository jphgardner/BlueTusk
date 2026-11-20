[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$websiteRoot = Join-Path $repositoryRoot 'website'
$contract = Get-Content -LiteralPath (
    Join-Path $websiteRoot 'production-contract.json') -Raw | ConvertFrom-Json
if ([int]$contract.schemaVersion -ne 1)
{
    throw "Expected website production contract schema 1; found '$($contract.schemaVersion)'."
}

$limits = $contract.limits
foreach ($limit in @(
        $limits.initialRawBytes,
        $limits.initialBrotliBytes,
        $limits.largestLazyBrotliBytes,
        $limits.totalDistributionBytes))
{
    if ([long]$limit -lt 1)
    {
        throw 'Every website production budget must be positive.'
    }
}

$angular = Get-Content -LiteralPath (
    Join-Path $websiteRoot 'angular.json') -Raw | ConvertFrom-Json
$initialBudget = @(
    $angular.projects.website.architect.build.configurations.production.budgets |
        Where-Object { [string]$_.type -eq 'initial' }
)
if ($initialBudget.Count -ne 1 -or
    [string]$initialBudget[0].maximumError -ne '900kB')
{
    throw 'Angular production builds must fail when the initial bundle exceeds 900 kB.'
}
if ([string]$angular.projects.website.architect.build.configurations.production.outputHashing -ne
    'all')
{
    throw 'Angular production output hashing must remain enabled for every asset.'
}

$package = Get-Content -LiteralPath (
    Join-Path $websiteRoot 'package.json') -Raw | ConvertFrom-Json
if ([string]$package.scripts.postbuild -ne 'node scripts/verify-production-build.mjs' -or
    [string]$package.scripts.'verify:production' -ne
    'node scripts/verify-production-build.mjs')
{
    throw 'The website production verifier must run automatically after every build.'
}

$buildWorkflowSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot '.github/workflows/build.yml') -Raw
foreach ($requiredWorkflowSource in @(
        'run: npm run build',
        'name: bluetusk-website',
        'path: website/dist/website/browser',
        'include-hidden-files: true'))
{
    if (-not $buildWorkflowSource.Contains(
            $requiredWorkflowSource,
            [StringComparison]::Ordinal))
    {
        throw "The build workflow does not retain website evidence through '$requiredWorkflowSource'."
    }
}

$indexSource = Get-Content -LiteralPath (
    Join-Path $websiteRoot 'src/index.html') -Raw
if ($indexSource.Contains('__SITE_ORIGIN__', [StringComparison]::Ordinal))
{
    throw 'The website source contains an unresolved site-origin placeholder.'
}
foreach ($metadata in @($contract.requiredMetadata))
{
    if (-not $indexSource.Contains([string]$metadata, [StringComparison]::Ordinal))
    {
        throw "The website source is missing required metadata '$metadata'."
    }
}

foreach ($requiredAsset in @($contract.requiredAssets))
{
    $asset = Join-Path (Join-Path $websiteRoot 'public') ([string]$requiredAsset)
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf))
    {
        throw "Required website production asset '$requiredAsset' is missing."
    }
}

$securityContact = Get-Content -LiteralPath (
    Join-Path $websiteRoot 'public/.well-known/security.txt') -Raw
$expiryMatch = [regex]::Match(
    $securityContact,
    '(?m)^Expires:\s*(?<value>\S+)\s*$')
$securityExpiry = [DateTimeOffset]::MinValue
if (-not $securityContact.Contains('Contact: https://', [StringComparison]::Ordinal) -or
    -not $securityContact.Contains('Policy: https://', [StringComparison]::Ordinal) -or
    -not $expiryMatch.Success -or
    -not [DateTimeOffset]::TryParse(
        $expiryMatch.Groups['value'].Value,
        [ref]$securityExpiry) -or
    $securityExpiry -le [DateTimeOffset]::UtcNow.AddDays(30))
{
    throw 'The website security contact is incomplete or expires within 30 days.'
}

$applicationTemplate = Get-Content -LiteralPath (
    Join-Path $websiteRoot 'src/app/app.html') -Raw
if (-not $applicationTemplate.Contains(
        '<img src="/bluetusk-mark.png" width="32" height="32" alt="" />',
        [StringComparison]::Ordinal))
{
    throw 'The persistent navigation must use the bounded logo asset with explicit dimensions.'
}

$homeTemplate = Get-Content -LiteralPath (
    Join-Path $websiteRoot 'src/app/home/home.html') -Raw

# Read the dimensions of the lossy WebP hero image from its VP8 key frame.
$heroImage = [System.IO.File]::ReadAllBytes(
    (Join-Path $websiteRoot 'public/platform-layers.webp'))
if ($heroImage.Length -lt 30 -or
    [System.Text.Encoding]::ASCII.GetString($heroImage, 0, 4) -cne 'RIFF' -or
    [System.Text.Encoding]::ASCII.GetString($heroImage, 8, 8) -cne 'WEBPVP8 ' -or
    $heroImage[23] -ne 0x9d -or $heroImage[24] -ne 0x01 -or $heroImage[25] -ne 0x2a)
{
    throw 'The homepage hero must be a lossy WebP image with a VP8 key frame.'
}
$heroWidth = [int]$heroImage[26] -bor (([int]$heroImage[27] -band 0x3f) -shl 8)
$heroHeight = [int]$heroImage[28] -bor (([int]$heroImage[29] -band 0x3f) -shl 8)
$heroTag = [regex]::Match($homeTemplate, '<img\b[^>]*\bsrc="/platform-layers\.webp"[^>]*>').Value
foreach ($requiredImageAttribute in @(
        "width=`"$heroWidth`"",
        "height=`"$heroHeight`"",
        'fetchpriority="high"'))
{
    if (-not $heroTag.Contains($requiredImageAttribute, [StringComparison]::Ordinal))
    {
        throw "The homepage hero image is missing '$requiredImageAttribute'."
    }
}
if ($heroTag.Contains('loading="lazy"', [StringComparison]::Ordinal))
{
    throw 'The homepage hero image must load eagerly.'
}

Write-Output (
    "Verified the website production source contract: a 900000-byte Angular initial build ceiling, " +
    "$([long]$limits.initialBrotliBytes)-byte Brotli transfer ceiling, hashed assets, " +
    'post-build measurement, content hashes, archived evidence, bounded images, ' +
    'discoverability metadata and security contact.')
