[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedCommit)
{ throw 'The active Core performance contract requires the exact candidate checkout.' }

$paths = @(
    'eng/release-tracks.json',
    'eng/performance-leadership-contract.json',
    'eng/performance-evidence-plan.json',
    'eng/performance-variant-map.json',
    'eng/resolve-core-performance-release.ps1'
)
& git -C $root diff --quiet $ExpectedCommit -- @paths
if ($LASTEXITCODE -ne 0) { throw 'The candidate release or performance contract has local changes.' }

$tracks = Get-Content -LiteralPath (Join-Path $root $paths[0]) -Raw | ConvertFrom-Json
$release = [string]$tracks.releaseVersion
$confirmation = switch ($release)
{
    '1.1.0' { 'CAPTURE-1.1-PERFORMANCE-EVIDENCE' }
    '1.2.0' { 'CAPTURE-1.2-CORE-PERFORMANCE-EVIDENCE' }
    default { throw "Core performance release '$release' is not supported." }
}
$output = Join-Path $root "artifacts/core-performance-contract/$ExpectedCommit"
$null = [IO.Directory]::CreateDirectory($output)

function Publish-ResolvedDocument([string] $Path, [string] $Content)
{
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Content)
    if (-not [IO.File]::Exists($Path))
    {
        $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
        try
        {
            [IO.File]::WriteAllBytes($temporary, $bytes)
            try { [IO.File]::Move($temporary, $Path) }
            catch [IO.IOException] { if (-not [IO.File]::Exists($Path)) { throw } }
        }
        finally { [IO.File]::Delete($temporary) }
    }
    # Another resolver may have published this same candidate while this process was building it.
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) -cne [Convert]::ToBase64String($bytes))
    { throw "The existing resolved performance document differs from the exact candidate: $Path" }
}

$historicalRelease = '"release": "1.1.0"'
$historicalConfirmation = '"confirmationToken": "CAPTURE-1.1-PERFORMANCE-EVIDENCE"'
$contract = Get-Content -LiteralPath (Join-Path $root $paths[1]) -Raw
$plan = Get-Content -LiteralPath (Join-Path $root $paths[2]) -Raw
if ([regex]::Matches($contract, [regex]::Escape($historicalRelease)).Count -ne 1 -or
    [regex]::Matches($plan, [regex]::Escape($historicalRelease)).Count -ne 1 -or
    [regex]::Matches($plan, [regex]::Escape($historicalConfirmation)).Count -ne 1)
{ throw 'Could not identify the historical Core contract and plan release fields.' }

$releaseField = '"release": "' + $release + '"'
$contract = $contract.Replace($historicalRelease, $releaseField)
$plan = $plan.Replace($historicalRelease, $releaseField).Replace(
    $historicalConfirmation, ('"confirmationToken": "' + $confirmation + '"'))
$mapPath = Join-Path $root $paths[3]
if ($release -ceq '1.2.0')
{
    $contractDocument = $contract | ConvertFrom-Json -AsHashtable
    $provider = $contractDocument.workloads.Provider
    # The Windows host measures a native client and a Linux client in Docker.
    $provider.variantsByEnvironment = [ordered]@{
        windows = $provider.variants
        linux = @('linux', 'tls', 'constrained-network')
    }
    $null = $provider.Remove('variants')
    $contractDocument.primaryHotPaths = [ordered]@{
        referenceVersion = '1.0.0'
        referenceSourceCommit = '7380d7b028c72b2aae348b778711d104d022a3f8'
        workloads = [ordered]@{
            Provider = [ordered]@{ feature = 'parameterized-scalar'; concurrency = 64; variant = 'native' }
            Streams = [ordered]@{ scenario = 'commit-to-delivery'; transactionChanges = 1 }
            Sync = [ordered]@{ destination = 'postgresql'; mutations = 1 }
            Live = [ordered]@{ scenario = 'affected-key-update'; results = 1000; subscribers = 64 }
            ControlPlane = [ordered]@{ operation = 'inventory-read'; sources = 100; clients = 32 }
        }
    }
    $contract = ($contractDocument | ConvertTo-Json -Depth 20).Replace("`r`n", "`n")

    $planDocument = $plan | ConvertFrom-Json -AsHashtable
    $planDocument.primaryHotPaths.definitionStatus = 'owner-approved'
    $planDocument.primaryHotPaths.status = 'stub-fails-closed'
    $plan = ($planDocument | ConvertTo-Json -Depth 20).Replace("`r`n", "`n")

    $map = Get-Content -LiteralPath $mapPath -Raw | ConvertFrom-Json -AsHashtable
    $map.crossOsProfile = 'linux-container-client'
    $null = $map.variants.linux.Remove('windows')
    $map.profiles.'linux-container-client'.description = 'Linux .NET client on the Windows host using the pinned runtime image and fixture network.'
    $mapPath = Join-Path $output 'performance-variant-map.json'
    Publish-ResolvedDocument $mapPath ($map | ConvertTo-Json -Depth 20).Replace("`r`n", "`n")
}
$contractPath = Join-Path $output 'performance-leadership-contract.json'
$planPath = Join-Path $output 'performance-evidence-plan.json'
Publish-ResolvedDocument $contractPath $contract
Publish-ResolvedDocument $planPath $plan
[pscustomobject]@{
    Release = $release; Confirmation = $confirmation; ContractPath = $contractPath
    PlanPath = $planPath; VariantMapPath = $mapPath
}
