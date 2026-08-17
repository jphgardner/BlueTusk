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
    [string]$initialBudget[0].maximumError -ne '650kB')
{
    throw 'Angular production builds must fail when the initial bundle exceeds 650 kB.'
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
        '<img src="/favicon.ico" width="32" height="32" alt="" />',
        [StringComparison]::Ordinal))
{
    throw 'The persistent navigation must use the bounded logo asset with explicit dimensions.'
}

$homeTemplate = Get-Content -LiteralPath (
    Join-Path $websiteRoot 'src/app/home/home.html') -Raw
foreach ($requiredImageAttribute in @(
        'width="1376"',
        'height="768"',
        'loading="lazy"',
        'decoding="async"'))
{
    if (-not $homeTemplate.Contains(
            $requiredImageAttribute,
            [StringComparison]::Ordinal))
    {
        throw "The architecture image is missing '$requiredImageAttribute'."
    }
}

Write-Output (
    "Verified the website production source contract: a 650000-byte initial build ceiling, " +
    "$([long]$limits.initialBrotliBytes)-byte Brotli transfer ceiling, hashed assets, " +
    'post-build measurement, content hashes, archived evidence, bounded images, ' +
    'discoverability metadata and security contact.')
