[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory)][ValidateSet('chromium','firefox','webkit','msedge')][string]$Browser,
    [Parameter(Mandatory)][ValidateSet('linux','win32')][string]$ExpectedPlatform,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [switch]$Candidate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = (Resolve-Path -LiteralPath $EvidenceDirectory).Path
$artifacts = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json([string]$Name) {
    $path = Join-Path $evidence $Name
    Require (Test-Path -LiteralPath $path -PathType Leaf) "Missing browser evidence: $Name"
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 30
}

Require ($evidence.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) 'Evidence must be under checkout artifacts.'
$head = (& git -C $root rev-parse HEAD).Trim()
Require ($LASTEXITCODE -eq 0 -and [string]::Equals($head, $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) 'Verifier checkout is not the exact candidate.'
Require (@(& git -C $root status --porcelain --untracked-files=normal).Count -eq 0) 'Verifier checkout must be clean.'
$manifest = Json 'manifest.json'
Require ($manifest.SchemaVersion -eq 1 -and $manifest.CandidateSha -ceq $head -and
    $manifest.Platform -ceq $ExpectedPlatform -and $manifest.Browser -ceq $Browser -and
    $manifest.ProductionQualified -eq $false -and
    ($Candidate -or $manifest.BrowserCompatibilityPassed -eq $true)) 'Browser compatibility manifest is incomplete or mismatched.'
$listed = @($manifest.Files)
$actual = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $evidence 'manifest.json') })
Require ($listed.Count -eq $actual.Count -and $listed.Count -eq 15) 'Browser evidence inventory is incomplete or contains unexpected files.'
$seen = @{}
foreach ($entry in $listed) {
    $name = [string]$entry.Name
    Require ($name -match '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -and -not $seen.ContainsKey($name)) 'Browser evidence path is unsafe or duplicated.'
    $seen[$name] = $true
    $path = Join-Path $evidence $name
    Require (Test-Path -LiteralPath $path -PathType Leaf) "Missing browser evidence artifact: $name"
    Require ((Hash $path) -ceq [string]$entry.Sha256) "Browser evidence artifact changed: $name"
}
$required = @('source-before.json','source-after.json','runtime.json','binaries.json','browser-build.log','browser-unit.log','host-build.log',
    'storage.json','storage.log','http.json','http.log','binary-snapshot/index.js','binary-snapshot/http.js',
    'binary-snapshot/ordered.js','binary-snapshot/BlueTusk.Edge.BrowserHttpSmoke.dll')
foreach ($name in $required) { Require $seen.ContainsKey($name) "Required browser artifact omitted: $name" }
$before = Json 'source-before.json'; $after = Json 'source-after.json'
foreach ($source in @($before,$after)) {
    Require ($source.commit -ceq $head -and $source.dirty -eq $false -and $source.productionQualified -eq $false) 'Browser source capture is not clean or exact.'
}
Require ($before.sourceTreeSha256 -ceq $after.sourceTreeSha256 -and
    $before.sourceTreeSha256 -ceq $manifest.SourceTreeSha256) 'Browser candidate source changed during testing.'
$runtime = Json 'runtime.json'
$lockedPlaywright = (Get-Content -LiteralPath (Join-Path $root 'package-lock.json') -Raw | ConvertFrom-Json -Depth 20).packages.'node_modules/playwright'.version
Require ($runtime.CandidateSha -ceq $head -and $runtime.SourceTreeSha256 -ceq $before.sourceTreeSha256 -and
    $runtime.Platform -ceq $ExpectedPlatform -and $runtime.Browser -ceq $Browser -and
    $runtime.NodeVersion -match '^v24\.' -and $runtime.DotnetVersion -match '^10\.' -and
    $runtime.PlaywrightVersion -ceq $lockedPlaywright -and
    $runtime.LockfileSha256 -ceq (Hash (Join-Path $root 'package-lock.json')) -and
    $runtime.ProductionQualified -eq $false) 'Browser runtime provenance differs.'
$binaries = Json 'binaries.json'
$assets = @($binaries.BrowserAssets)
Require ($assets.Count -eq 3) 'Browser asset snapshot is incomplete.'
foreach ($name in @('index.js','http.js','ordered.js')) {
    $entry = @($assets | Where-Object Name -ceq $name)
    Require ($entry.Count -eq 1 -and (Hash (Join-Path $evidence "binary-snapshot/$name")) -ceq $entry[0].Sha256) "Browser asset differs: $name"
}
Require ((Hash (Join-Path $evidence 'binary-snapshot/BlueTusk.Edge.BrowserHttpSmoke.dll')) -ceq $binaries.HostDllSha256) 'HTTP host snapshot differs.'
$expectedEngine = if ($Browser -eq 'msedge') { 'chromium' } else { $Browser }
$expectedChecks = @{
    storage = @('indexeddb-snapshot-rollback','persistent-profile-restart','lease-fence','atomic-acknowledgement','int64-precision','epoch-isolation')
    http = @('committed-response-loss','offline-read','persistent-profile-restart','idempotent-business-effect',
        'authentication-denial','tenant-isolation','conflict-resolution','deletion-and-feed')
}
foreach ($kind in @('storage','http')) {
    $report = Json "$kind.json"
    $log = Get-Content -LiteralPath (Join-Path $evidence "$kind.log") -Raw
    $success = if ($kind -eq 'storage') { "Real $Browser IndexedDB:" } else { "Real $Browser/IndexedDB/PostgreSQL HTTP:" }
    Require ($log.Contains($success, [StringComparison]::Ordinal) -and $log.Contains('passed.', [StringComparison]::Ordinal)) "$kind browser process did not log completion."
    Require ($report.formatVersion -eq 1 -and $report.channel -ceq $Browser -and
        $report.engine -ceq $expectedEngine -and $report.platform -ceq $ExpectedPlatform -and
        $report.passed -eq $true -and $report.productionQualified -eq $false -and
        -not [string]::IsNullOrWhiteSpace([string]$report.userAgent)) "$kind browser report is incomplete."
    Require ((@($report.checks | Sort-Object) -join '|') -ceq (@($expectedChecks[$kind] | Sort-Object) -join '|')) "$kind browser checks are incomplete."
    $ua = [string]$report.userAgent
    $expectedUa = switch ($Browser) {
        'firefox' { 'Firefox/' }
        'webkit' { 'AppleWebKit/' }
        'msedge' { 'Edg/' }
        default { 'Chrome/' }
    }
    Require ($ua.Contains($expectedUa, [StringComparison]::Ordinal)) "$kind browser user agent does not identify $Browser."
}
Write-Output "Edge $ExpectedPlatform/$Browser real browser storage and HTTP compatibility evidence passed for $head; production qualification remains false."
