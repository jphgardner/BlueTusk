[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')]
    [string] $Family,

    [Parameter(Mandatory)]
    [ValidatePattern('^1\.0\.0$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $Commit,

    [Parameter(Mandatory)]
    [string] $Tag,

    [Parameter(Mandatory)]
    [ValidateSet('Packaging', 'Publication')]
    [string] $Stage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$overridePath = Join-Path $PSScriptRoot 'v1-owner-release-override.json'
$manifestPath = Join-Path $PSScriptRoot 'product-families.json'

if (-not (Test-Path -LiteralPath $overridePath -PathType Leaf)) {
    throw 'The owner release override declaration is missing.'
}

$override = Get-Content -LiteralPath $overridePath -Raw | ConvertFrom-Json -Depth 32
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 32

if ([int]$override.schemaVersion -ne 1 -or $override.enabled -ne $true) {
    throw 'The owner release override is not armed with supported schema 1.'
}
if (-not [string]::Equals([string]$override.version, $Version, [StringComparison]::Ordinal)) {
    throw "The owner release override is for version '$($override.version)', not '$Version'."
}
if (-not [string]::Equals([string]$override.authorizedByGitHubLogin, 'jphgardner', [StringComparison]::Ordinal)) {
    throw 'The owner release override must identify the repository owner.'
}
if ([string]::IsNullOrWhiteSpace([string]$override.authorizationSource) -or
    [string]::IsNullOrWhiteSpace([string]$override.releaseReason)) {
    throw 'The owner release override must record its authorization source and reason.'
}

$authorizedAt = [DateTimeOffset]::Parse(
    [string]$override.authorizedAtUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal)
$expiresAt = [DateTimeOffset]::Parse(
    [string]$override.expiresAtUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal)
if ($authorizedAt -ge $expiresAt -or [DateTimeOffset]::UtcNow -gt $expiresAt) {
    throw "The owner release override expired at '$($expiresAt.ToString('O'))'."
}

$requiredRisks = @(
    'independent-approval-not-complete',
    'coverage-guided-fuzz-evidence-not-produced',
    'reference-performance-gate-not-passed',
    'endurance-evidence-not-complete',
    'postgresql-19-ga-deferred'
)
$acceptedRisks = @($override.acceptedRisks | ForEach-Object { [string]$_ })
$missingRisks = @($requiredRisks | Where-Object { $_ -notin $acceptedRisks })
if ($missingRisks.Count -ne 0) {
    throw "The owner release override omits accepted risks: $($missingRisks -join ', ')."
}

$definition = $manifest.families.PSObject.Properties[$Family].Value
if ($null -eq $definition) {
    throw "Product family '$Family' is not registered."
}
$expectedTag = "$($definition.publication.tagPrefix)-v$Version"
if (-not [string]::Equals($Tag, $expectedTag, [StringComparison]::Ordinal)) {
    throw "Owner-approved release tag '$Tag' does not match '$expectedTag'."
}

Push-Location $repositoryRoot
try {
    $head = (git rev-parse HEAD).Trim()
    $originMain = (git rev-parse refs/remotes/origin/main).Trim()
    $parent = (git rev-parse "$Commit^").Trim()
    $resolvedTag = (git rev-list -n 1 $Tag).Trim()
}
finally {
    Pop-Location
}

foreach ($observed in @($head, $originMain, $resolvedTag)) {
    if (-not [string]::Equals($observed, $Commit, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Owner release override requires HEAD, origin/main and '$Tag' to resolve to '$Commit'."
    }
}
if (-not [string]::Equals(
        $parent,
        [string]$override.baseCommit,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Owner release override commit must be the direct child of '$($override.baseCommit)'."
}
if ([string]$env:GITHUB_REF_TYPE -ne 'tag' -or
    -not [string]::Equals([string]$env:GITHUB_REF_NAME, $Tag, [StringComparison]::Ordinal)) {
    throw 'Owner release override is valid only for the exact GitHub tag event.'
}

Write-Warning (
    "OWNER-APPROVED V1 RELEASE OVERRIDE: $Stage for $Family $Version at $Commit; " +
    "accepted risks: $($acceptedRisks -join ', ').")
