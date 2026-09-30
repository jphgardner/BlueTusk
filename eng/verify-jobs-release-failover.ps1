[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Run', 'Verify')][string] $Mode = 'Verify',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedCommit,
    [string] $EvidenceRoot = 'artifacts/jobs-release-failover/local'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = if ([IO.Path]::IsPathRooted($EvidenceRoot)) {
    [IO.Path]::GetFullPath($EvidenceRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $repository $EvidenceRoot))
}
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/jobs-release-failover')) +
    [IO.Path]::DirectorySeparatorChar
$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'jobs-release-failover-policy.json') -Raw | ConvertFrom-Json
$reportPath = Join-Path $evidence 'report.json'
$manifestPath = Join-Path $evidence 'manifest.json'
$binaryRoot = Join-Path $evidence 'binary-snapshot'
$binarySource = Join-Path $repository 'tests/BlueTusk.Workflows.PhysicalRecoveryTests/bin/Release/net10.0'

function Require([bool] $Condition, [string] $Message)
{
    if (-not $Condition) { throw $Message }
}
function Hash([string] $Path)
{
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function WithinEvidence([string] $Path)
{
    $resolved = [IO.Path]::GetFullPath((Join-Path $repository $Path))
    Require ($resolved.StartsWith($evidence + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) 'Failover report references evidence outside the candidate artifact.'
    return $resolved
}
function AssertReport
{
    & (Join-Path $PSScriptRoot 'verify-jobs-failover-report.ps1') -ReportPath $reportPath -ExpectedCommit $ExpectedCommit | Out-Null
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -Depth 100
    Require ([string]$report.ScriptSha256 -ieq (Hash (Join-Path $PSScriptRoot 'jobs-physical-recovery.ps1'))) 'Jobs physical runner differs from the report.'
    Require (@($report.Sources).Count -gt 0) 'Jobs failover source inventory is empty.'
    foreach ($source in @($report.Sources))
    {
        Require ([string]$source.Path -match '^(src|tests)/[A-Za-z0-9_./-]+$' -and
            -not ([string]$source.Path).Contains('..')) 'Jobs failover source path is unsafe.'
        $path = [IO.Path]::GetFullPath((Join-Path $repository ([string]$source.Path)))
        Require (Test-Path -LiteralPath $path -PathType Leaf) 'A candidate source file is missing.'
        Require ([string]$source.Sha256 -ieq (Hash $path)) 'A candidate source file changed after failover.'
    }
    $before = Get-Content -LiteralPath (Join-Path $evidence 'raw/source-before.json') -Raw | ConvertFrom-Json
    $after = Get-Content -LiteralPath (Join-Path $evidence 'raw/source-after.json') -Raw | ConvertFrom-Json
    Require (($before | ConvertTo-Json -Depth 100 -Compress) -ceq
        ($report.SourceProvenance.Before | ConvertTo-Json -Depth 100 -Compress) -and
        ($after | ConvertTo-Json -Depth 100 -Compress) -ceq
        ($report.SourceProvenance.After | ConvertTo-Json -Depth 100 -Compress)) 'Retained source captures differ from the failover report.'
    foreach ($run in @($report.Runs))
    {
        $directory = Join-Path $evidence "raw/run$([int]$run.Repetition)"
        Require (Test-Path -LiteralPath (Join-Path $directory 'environment.json') -PathType Leaf) 'A failover fixture environment record is missing.'
        $trxPath = WithinEvidence ([string]$run.TrxPath)
        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $counters = $trx.SelectSingleNode('//*[local-name()="Counters"]')
        Require ($null -ne $counters -and [int]$counters.passed -eq 1 -and
            [int]$counters.total -eq 1 -and [int]$counters.failed -eq 0) 'A failover trial failed or was skipped.'
        $samplePath = WithinEvidence ([string]$run.FixtureSamplePath)
        Require ((Get-Item -LiteralPath $samplePath).Length -gt 0) 'A failover fixture sample stream is empty.'
        Require (@(Get-ChildItem -LiteralPath $directory -Filter '*.log' -File).Count -ge 2) 'A failover trial lacks primary or standby logs.'
        foreach ($file in @('recovery.json', 'binaries-before.json', 'binaries-after.json'))
        {
            Require (Test-Path -LiteralPath (Join-Path $directory $file) -PathType Leaf) "A failover trial lacks raw $file."
        }
        $rawRun = Get-Content -LiteralPath (Join-Path $directory 'recovery.json') -Raw | ConvertFrom-Json
        $rawEnvironment = Get-Content -LiteralPath (Join-Path $directory 'environment.json') -Raw | ConvertFrom-Json -Depth 100
        $rawBinariesBefore = Get-Content -LiteralPath (Join-Path $directory 'binaries-before.json') -Raw | ConvertFrom-Json -Depth 100
        $rawBinariesAfter = Get-Content -LiteralPath (Join-Path $directory 'binaries-after.json') -Raw | ConvertFrom-Json -Depth 100
        Require (($rawRun | ConvertTo-Json -Depth 100 -Compress) -ceq
            ($run.Report | ConvertTo-Json -Depth 100 -Compress) -and
            [string]$rawEnvironment.ImageDigest -ceq [string]$run.Metadata.ImageDigest -and
            [string]$rawEnvironment.ImageInspect.Id -ceq [string]$run.Metadata.ImageInspect.Id -and
            (@($rawEnvironment.BeforePrimarySettings) -join ',') -ceq
            (@($run.Metadata.BeforePrimarySettings) -join ',') -and
            ($rawBinariesBefore | ConvertTo-Json -Depth 100 -Compress) -ceq
            ($run.BinariesBefore | ConvertTo-Json -Depth 100 -Compress) -and
            ($rawBinariesAfter | ConvertTo-Json -Depth 100 -Compress) -ceq
            ($run.BinariesAfter | ConvertTo-Json -Depth 100 -Compress)) 'Retained raw failover records differ from the aggregate.'
    }
    return $report
}
function AssertBinaries($Report)
{
    $files = @($Report.Runs[0].BinariesBefore.Files)
    $required = @('BlueTusk.Workflows.PhysicalRecoveryTests.dll', 'BlueTusk.Jobs.dll',
        'BlueTusk.Workflows.dll', 'BlueTusk.Data.dll')
    foreach ($name in $required)
    {
        Require (@($files | Where-Object { [string]$_.Name -ceq $name }).Count -eq 1) "Missing failover executable dependency '$name'."
    }
    Require (@($files).Count -eq @(Get-ChildItem -LiteralPath $binaryRoot -File).Count) 'Archived failover binary count differs.'
    foreach ($file in $files)
    {
        Require ([string]$file.Name -match '^[A-Za-z0-9_.-]+$' -and
            [string]$file.Name -notin @('.', '..')) 'Unsafe failover executable name.'
        $archived = Join-Path $binaryRoot ([string]$file.Name)
        Require (Test-Path -LiteralPath $archived -PathType Leaf) 'An archived failover executable is missing.'
        Require ((Hash $archived) -eq ([string]$file.Sha256).ToLowerInvariant()) 'An archived failover executable hash changed.'
        foreach ($run in @($Report.Runs))
        {
            $entry = @($run.BinariesBefore.Files | Where-Object { [string]$_.Name -ceq [string]$file.Name })
            Require ($entry.Count -eq 1 -and
                [string]$entry[0].Sha256 -ieq [string]$file.Sha256) 'Failover repetitions used different executable bytes.'
        }
    }
}

Require ($evidence.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) 'Jobs failover evidence must use its product directory.'
Require ($policy.schemaVersion -eq 1 -and $policy.repetitions -eq 3) 'Jobs failover requires three fresh promotions.'
$head = (& git -C $repository rev-parse HEAD).Trim()
Require ($LASTEXITCODE -eq 0 -and [string]$head -ieq $ExpectedCommit) 'Jobs failover checkout is not the requested exact commit.'
Require (@(& git -C $repository status --porcelain --untracked-files=normal).Count -eq 0) 'Jobs failover requires a clean candidate checkout.'
if ($Mode -eq 'Preflight')
{
    Require (-not (Test-Path -LiteralPath $evidence)) 'Jobs failover requires a fresh evidence directory.'
    Write-Output "Clean exact Jobs failover candidate $head verified; no disturbance was run."
    return
}
if ($Mode -eq 'Run')
{
    Require (-not (Test-Path -LiteralPath $evidence)) 'Jobs failover requires a fresh evidence directory.'
    [IO.Directory]::CreateDirectory($evidence) | Out-Null
    & (Join-Path $PSScriptRoot 'jobs-physical-recovery.ps1') -Repetitions $policy.repetitions -ArtifactDirectory (Join-Path $evidence 'raw') -OutputReport $reportPath
    $report = AssertReport
    [IO.Directory]::CreateDirectory($binaryRoot) | Out-Null
    foreach ($file in @($report.Runs[0].BinariesBefore.Files))
    {
        $source = Join-Path $binarySource ([string]$file.Name)
        Require (Test-Path -LiteralPath $source -PathType Leaf) 'A measured failover binary is missing after the run.'
        Require ((Hash $source) -eq ([string]$file.Sha256).ToLowerInvariant()) 'A measured failover binary changed after the run.'
        Copy-Item -LiteralPath $source -Destination (Join-Path $binaryRoot ([string]$file.Name))
    }
    AssertBinaries $report
    $files = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | ForEach-Object {
        [ordered]@{
            Path = [IO.Path]::GetRelativePath($evidence, $_.FullName).Replace('\', '/')
            Sha256 = Hash $_.FullName
        }
    } | Sort-Object Path)
    [ordered]@{
        SchemaVersion = 1
        Family = 'Jobs'
        CandidateSha = $ExpectedCommit.ToLowerInvariant()
        Qualification = $policy.qualification
        Repetitions = $policy.repetitions
        SourceTreeSha256 = [string]$report.SourceProvenance.Before.sourceTreeSha256
        ProductionQualified = $false
        Files = $files
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Require ($manifest.SchemaVersion -eq 1 -and $manifest.Family -ceq 'Jobs' -and
    [string]$manifest.CandidateSha -ieq $ExpectedCommit -and
    $manifest.Qualification -ceq $policy.qualification -and
    $manifest.Repetitions -eq $policy.repetitions -and
    $manifest.ProductionQualified -eq $false) 'Jobs failover manifest is not bound to this candidate.'
$listed = @($manifest.Files)
$actual = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | Where-Object { $_.FullName -ne $manifestPath })
Require ($listed.Count -gt 0 -and $listed.Count -eq $actual.Count) 'Jobs failover artifact file count changed.'
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $listed)
{
    Require ([string]$entry.Path -match '^[^/\\]+(?:/[^/\\]+)*$' -and
        -not ([string]$entry.Path).Contains('..') -and
        $seen.Add([string]$entry.Path)) 'Jobs failover manifest contains an unsafe or duplicate path.'
    $path = Join-Path $evidence ([string]$entry.Path)
    Require (Test-Path -LiteralPath $path -PathType Leaf) 'Jobs failover artifact file is missing.'
    Require ([string]$entry.Sha256 -ieq (Hash $path)) 'Jobs failover artifact file hash changed.'
}
$report = AssertReport
Require ([string]$manifest.SourceTreeSha256 -ceq [string]$report.SourceProvenance.Before.sourceTreeSha256) 'Jobs failover source tree differs from the manifest.'
AssertBinaries $report
Write-Output "Archived Jobs failover evidence verified for exact candidate $ExpectedCommit; production qualification remains false."
