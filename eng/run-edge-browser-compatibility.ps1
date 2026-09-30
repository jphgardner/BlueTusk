[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory)][ValidateSet('chromium','firefox','webkit','msedge')][string]$Browser,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ReviewHeadCommit,
    [switch]$StorageOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = [IO.Path]::GetFullPath((Join-Path $root $EvidenceDirectory))
$artifacts = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Capture([string]$Name) {
    & python eng/capture-ecosystem-source.py --output (Join-Path $evidence $Name) | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Exact candidate source capture failed.'
}

Push-Location -LiteralPath $root
$oldChannel = $env:BLUETUSK_EDGE_BROWSER_CHANNEL
$oldStoreResult = $env:BLUETUSK_EDGE_BROWSER_RESULT
$oldHttpResult = $env:BLUETUSK_EDGE_HTTP_BROWSER_RESULT
try {
    $head = (& git rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and [string]::Equals($head, $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) 'Checkout is not the exact candidate.'
    Require (@(& git status --porcelain --untracked-files=normal).Count -eq 0) 'A clean exact-source checkout is required.'
    Require ($evidence.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase) -and -not (Test-Path -LiteralPath $evidence)) 'Choose a fresh evidence directory beneath artifacts.'
    if ($StorageOnly) { Require ($IsWindows -and $Browser -ceq 'msedge') 'Storage-only CI mode is reserved for Windows Edge.' }
    else { Require (-not [string]::IsNullOrWhiteSpace($env:BLUETUSK_TEST_CONNECTION_STRING)) 'A disposable PostgreSQL connection is required.' }
    New-Item -ItemType Directory -Path $evidence | Out-Null
    Capture 'source-before.json'
    $source = Get-Content -LiteralPath (Join-Path $evidence 'source-before.json') -Raw | ConvertFrom-Json
    Require ($source.commit -ceq $head -and $source.dirty -eq $false) 'Source capture is not clean or exact.'
    $platform = if ($IsLinux) { 'linux' } elseif ($IsWindows) { 'win32' } else { throw 'Only Linux and Windows are supported.' }
    $runtime = [ordered]@{
        CandidateSha = $head
        SourceTreeSha256 = $source.sourceTreeSha256
        Platform = $platform
        Browser = $Browser
        Scope = $(if ($StorageOnly) { 'storage-only' } else { 'storage-and-http' })
        ReviewHeadSha = $ReviewHeadCommit
        NodeVersion = (& node --version).Trim()
        DotnetVersion = (& dotnet --version).Trim()
        PlaywrightVersion = (& node -p "require('./node_modules/playwright/package.json').version").Trim()
        LockfileSha256 = Hash 'package-lock.json'
        ProductionQualified = $false
    }
    $lockedPlaywright = (Get-Content -LiteralPath package-lock.json -Raw | ConvertFrom-Json -AsHashtable -Depth 20)['packages']['node_modules/playwright']['version']
    Require ($runtime.NodeVersion -match '^v24\.' -and $runtime.DotnetVersion -match '^10\.' -and
        $runtime.PlaywrightVersion -ceq $lockedPlaywright) 'Runtime versions differ from the Edge CI contract or lockfile.'
    $runtime | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'runtime.json') -Encoding utf8
    & npm run build --workspace '@bluetusk/edge' 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'browser-build.log')
    Require ($LASTEXITCODE -eq 0) 'The locked Edge browser client did not build.'
    & npm run test --workspace '@bluetusk/edge' 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'browser-unit.log')
    Require ($LASTEXITCODE -eq 0) 'The Edge browser contract tests failed.'
    if (-not $StorageOnly) {
        & dotnet build tests/BlueTusk.Edge.BrowserHttpSmoke/BlueTusk.Edge.BrowserHttpSmoke.csproj -c Release -nr:false 2>&1 |
            Tee-Object -FilePath (Join-Path $evidence 'host-build.log')
        Require ($LASTEXITCODE -eq 0) 'The Edge HTTP smoke host did not build.'
    }
    $snapshot = Join-Path $evidence 'binary-snapshot'
    New-Item -ItemType Directory -Path $snapshot | Out-Null
    $assets = @('index.js','http.js','ordered.js') | ForEach-Object {
        $path = Join-Path 'clients/edge/dist' $_
        Require (Test-Path -LiteralPath $path -PathType Leaf) "Missing built browser asset: $_"
        Copy-Item -LiteralPath $path -Destination (Join-Path $snapshot $_)
        [ordered]@{ Name = $_; Sha256 = (Hash $path) }
    }
    $hostDll = 'tests/BlueTusk.Edge.BrowserHttpSmoke/bin/Release/net10.0/BlueTusk.Edge.BrowserHttpSmoke.dll'
    $hostHash = $null
    if (-not $StorageOnly) {
        Require (Test-Path -LiteralPath $hostDll -PathType Leaf) 'Missing built HTTP smoke host.'
        $hostHash = Hash $hostDll
        Copy-Item -LiteralPath $hostDll -Destination (Join-Path $snapshot 'BlueTusk.Edge.BrowserHttpSmoke.dll')
    }
    [ordered]@{ BrowserAssets = $assets; HostDllSha256 = $hostHash } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $evidence 'binaries.json') -Encoding utf8
    $env:BLUETUSK_EDGE_BROWSER_CHANNEL = $Browser
    $env:BLUETUSK_EDGE_BROWSER_RESULT = Join-Path $evidence 'storage.json'
    & node clients/edge/test/browser.smoke.mjs 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'storage.log')
    Require ($LASTEXITCODE -eq 0) 'Real browser durable-storage recovery failed.'
    if (-not $StorageOnly) {
        $env:BLUETUSK_EDGE_HTTP_BROWSER_RESULT = Join-Path $evidence 'http.json'
        & dotnet run --project tests/BlueTusk.Edge.BrowserHttpSmoke/BlueTusk.Edge.BrowserHttpSmoke.csproj -c Release --no-build 2>&1 |
            Tee-Object -FilePath (Join-Path $evidence 'http.log')
        Require ($LASTEXITCODE -eq 0) 'Real browser/PostgreSQL HTTP recovery failed.'
    }
    foreach ($asset in $assets) {
        Require ((Hash (Join-Path 'clients/edge/dist' $asset.Name)) -ceq $asset.Sha256) "Browser asset changed during tests: $($asset.Name)"
    }
    if (-not $StorageOnly) { Require ((Hash $hostDll) -ceq $hostHash) 'HTTP smoke host changed during tests.' }
    Capture 'source-after.json'
    $after = Get-Content -LiteralPath (Join-Path $evidence 'source-after.json') -Raw | ConvertFrom-Json
    Require ($after.commit -ceq $head -and $after.dirty -eq $false -and $after.sourceTreeSha256 -ceq $source.sourceTreeSha256) 'Candidate source changed during browser verification.'
    $files = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | ForEach-Object {
        [ordered]@{ Name = [IO.Path]::GetRelativePath($evidence, $_.FullName).Replace('\','/'); Sha256 = (Hash $_.FullName) }
    } | Sort-Object Name)
    $manifestPath = Join-Path $evidence 'manifest.json'
    [ordered]@{ SchemaVersion = 1; CandidateSha = $head; SourceTreeSha256 = $source.sourceTreeSha256;
        Platform = $platform; Browser = $Browser; Scope = $runtime.Scope; ReviewHeadSha = $ReviewHeadCommit;
        BrowserCompatibilityPassed = $false; HttpCompatibilityPassed = $false;
        ProductionQualified = $false; Files = $files } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $manifestPath -Encoding utf8
    & (Join-Path $PSScriptRoot 'verify-edge-browser-compatibility.ps1') -ExpectedCommit $head -Browser $Browser -ExpectedPlatform $platform -EvidenceDirectory $evidence -Candidate -StorageOnly:$StorageOnly
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.BrowserCompatibilityPassed = $true
    $manifest.HttpCompatibilityPassed = -not $StorageOnly
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Output "Edge $Browser $($runtime.Scope) compatibility passed for exact candidate $head. Production qualification remains false."
}
finally {
    $env:BLUETUSK_EDGE_BROWSER_CHANNEL = $oldChannel
    $env:BLUETUSK_EDGE_BROWSER_RESULT = $oldStoreResult
    $env:BLUETUSK_EDGE_HTTP_BROWSER_RESULT = $oldHttpResult
    Pop-Location
}
