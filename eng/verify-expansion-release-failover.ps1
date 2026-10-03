[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Documents', 'Projections', 'Workflows', IgnoreCase = $false)]
    [string] $Family,
    [ValidateSet('Preflight', 'Run', 'Verify')][string] $Mode = 'Verify',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedCommit,
    [string] $EvidenceRoot,
    # Local diagnostic runs only: the Docker owner label for the owned fixture. Workflow runs use the default.
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{2,62}$')][string] $Owner = 'bluetusk.expansion.failover'
)

# Preflight checks the clean exact candidate. Run produces the evidence: three fresh synchronous
# pairs through eng/run-expansion-failover.ps1, then the report, raw records, binary snapshot and
# file-hash manifest. Verify re-checks an archived artifact offline, as readiness does.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$slug = $Family.ToLowerInvariant()
$workflowDirectory = "$slug-release-failover"
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) { $EvidenceRoot = "artifacts/$workflowDirectory/local" }
$evidence = if ([IO.Path]::IsPathRooted($EvidenceRoot)) { [IO.Path]::GetFullPath($EvidenceRoot) }
else { [IO.Path]::GetFullPath((Join-Path $repository $EvidenceRoot)) }
# Readiness extracts each artifact under artifacts/<workflow file name>/; nothing else is accepted.
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository "artifacts/$workflowDirectory")) + [IO.Path]::DirectorySeparatorChar
$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'expansion-failover-policy.json') -Raw | ConvertFrom-Json -Depth 40
$familyPolicy = $policy.families.PSObject.Properties[$Family].Value
$reportPath = Join-Path $evidence 'report.json'
$manifestPath = Join-Path $evidence 'manifest.json'
$binaryRoot = Join-Path $evidence 'binary-snapshot'
$runner = Join-Path $PSScriptRoot 'run-expansion-failover.ps1'

function Require([bool] $Condition, [string] $Message)
{
    if (-not $Condition) { throw $Message }
}
function Hash([string] $Path)
{
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Json([string] $Path)
{
    Require (Test-Path -LiteralPath $Path -PathType Leaf) "Failover evidence lacks raw '$([IO.Path]::GetFileName($Path))'."
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100
}
function Same($Left, $Right)
{
    return ($Left | ConvertTo-Json -Depth 100 -Compress) -ceq ($Right | ConvertTo-Json -Depth 100 -Compress)
}
function Assert-Report
{
    & (Join-Path $PSScriptRoot 'verify-expansion-failover-report.ps1') -Family $Family -ReportPath $reportPath -ExpectedCommit $ExpectedCommit | Out-Null
    $report = Json $reportPath
    Require ([string]$report.ScriptSha256 -ceq (Hash $runner)) "The $Family failover runner differs from the report."
    Require (@($report.Sources).Count -gt 0) "The $Family failover source inventory is empty."
    foreach ($source in @($report.Sources))
    {
        Require ([string]$source.Path -match '^(src|tests|benchmarks)/[A-Za-z0-9_./-]+$' -and
            -not ([string]$source.Path).Contains('..')) "A $Family failover source path is unsafe."
        $path = [IO.Path]::GetFullPath((Join-Path $repository ([string]$source.Path)))
        Require (Test-Path -LiteralPath $path -PathType Leaf) 'A candidate source file is missing.'
        Require ([string]$source.Sha256 -ceq (Hash $path)) 'A candidate source file changed after failover.'
    }
    Require ((Same (Json (Join-Path $evidence 'raw/source-before.json')) $report.SourceProvenance.Before) -and
        (Same (Json (Join-Path $evidence 'raw/source-after.json')) $report.SourceProvenance.After)) 'Retained source captures differ from the failover report.'
    foreach ($run in @($report.Runs))
    {
        $repetition = [int]$run.Repetition
        $directory = Join-Path $evidence "raw/run$repetition"
        $environment = Json (Join-Path $directory 'environment.json')
        Require ([string]$environment.ImageDigest -ceq [string]$run.Metadata.ImageDigest -and
            [string]$environment.ImageInspect.Id -ceq [string]$run.Metadata.ImageInspect.Id -and
            [string]$environment.Fixture -ceq [string]$run.Metadata.Fixture -and
            (@($environment.BeforePrimarySettings) -join ',') -ceq (@($run.Metadata.BeforePrimarySettings) -join ',')) 'Retained fixture environment differs from the aggregate.'
        Require ((Get-Item -LiteralPath (Join-Path $directory 'fixture.jsonl')).Length -gt 0) 'A failover fixture sample stream is empty.'
        Require (@(Get-ChildItem -LiteralPath $directory -Filter '*-primary.log' -File).Count -eq 1 -and
            @(Get-ChildItem -LiteralPath $directory -Filter '*-standby.log' -File).Count -eq 1) 'A failover repetition lacks primary or standby logs.'
        foreach ($step in @($run.Steps))
        {
            $name = [string]$step.Name
            Require ($name -cmatch '^[a-z][a-z-]*$') 'Unsafe failover step name.'
            # Recorded paths name the producing checkout; the archived copy is found by its fixed layout.
            Require ([string]$step.ReportPath -clike "*/raw/run$repetition/$name.json" -and
                [string]$step.LogPath -clike "*/raw/run$repetition/$name.log") 'Failover step paths differ from the archive layout.'
            Require ((Same (Json (Join-Path $directory "$name.json")) $step.Report) -and
                (Same (Json (Join-Path $directory "$name.binaries-before.json")) $step.BinariesBefore) -and
                (Same (Json (Join-Path $directory "$name.binaries-after.json")) $step.BinariesAfter)) 'Retained raw failover records differ from the aggregate.'
            Require (Test-Path -LiteralPath (Join-Path $directory "$name.log") -PathType Leaf) 'A failover step log is missing.'
            if ([string]$step.Kind -ceq 'xunit')
            {
                Require ([string]$step.TrxPath -clike "*/raw/run$repetition/$name.trx") 'Failover test result path differs from the archive layout.'
                [xml] $trx = Get-Content -LiteralPath (Join-Path $directory "$name.trx") -Raw
                $counters = $trx.SelectSingleNode('//*[local-name()="Counters"]')
                $tests = [int](@($familyPolicy.steps | Where-Object { [string]$_.name -ceq $name })[0].tests)
                Require ($null -ne $counters -and $tests -gt 0 -and [int]$counters.passed -eq $tests -and
                    [int]$counters.total -eq $tests -and [int]$counters.failed -eq 0) 'A failover rehearsal failed or was skipped.'
            }
        }
    }
    return $report
}
function Assert-Binaries($Report)
{
    foreach ($stepPolicy in @($familyPolicy.steps))
    {
        $name = [string]$stepPolicy.name
        $files = @(@($Report.Runs[0].Steps | Where-Object { [string]$_.Name -ceq $name })[0].BinariesBefore.Files)
        $archive = Join-Path $binaryRoot $name
        Require ($files.Count -gt 0 -and $files.Count -eq @(Get-ChildItem -LiteralPath $archive -File).Count) "Archived '$name' binary count differs."
        foreach ($file in $files)
        {
            Require ([string]$file.Name -match '^[A-Za-z0-9_.-]+$' -and [string]$file.Name -notin @('.', '..')) 'Unsafe failover executable name.'
            $archived = Join-Path $archive ([string]$file.Name)
            Require (Test-Path -LiteralPath $archived -PathType Leaf) 'An archived failover executable is missing.'
            Require ((Hash $archived) -ceq [string]$file.Sha256) 'An archived failover executable hash changed.'
            foreach ($run in @($Report.Runs))
            {
                $entry = @(@($run.Steps | Where-Object { [string]$_.Name -ceq $name })[0].BinariesBefore.Files |
                    Where-Object { [string]$_.Name -ceq [string]$file.Name })
                Require ($entry.Count -eq 1 -and [string]$entry[0].Sha256 -ceq [string]$file.Sha256) 'Failover repetitions used different executable bytes.'
            }
        }
    }
}

Require ($null -ne $familyPolicy) "No failover policy exists for '$Family'."
Require ($evidence.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) "$Family failover evidence must use its workflow directory artifacts/$workflowDirectory/."
Require ([int]$policy.repetitions -ge 3) "$Family failover requires at least three fresh pairs."
$head = (& git -C $repository rev-parse HEAD).Trim()
Require ($LASTEXITCODE -eq 0 -and [string]$head -ieq $ExpectedCommit) "$Family failover checkout is not the requested exact commit."
Require (@(& git -C $repository status --porcelain --untracked-files=normal).Count -eq 0) "$Family failover requires a clean candidate checkout."
if ($Mode -eq 'Preflight')
{
    Require (-not (Test-Path -LiteralPath $evidence)) "$Family failover requires a fresh evidence directory."
    Write-Output "Clean exact $Family failover candidate $head verified; no disturbance was run."
    return
}
if ($Mode -eq 'Run')
{
    Require (-not (Test-Path -LiteralPath $evidence)) "$Family failover requires a fresh evidence directory."
    [IO.Directory]::CreateDirectory($evidence) | Out-Null
    & $runner -Family $Family -Repetitions ([int]$policy.repetitions) -ArtifactDirectory (Join-Path $evidence 'raw') -OutputReport $reportPath -Owner $Owner
    $report = Assert-Report
    foreach ($stepPolicy in @($familyPolicy.steps))
    {
        $name = [string]$stepPolicy.name
        $source = Join-Path $repository "$(Split-Path -Parent ([string]$stepPolicy.project))/bin/Release/net10.0"
        $destination = Join-Path $binaryRoot $name
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        foreach ($file in @(@($report.Runs[0].Steps | Where-Object { [string]$_.Name -ceq $name })[0].BinariesBefore.Files))
        {
            $path = Join-Path $source ([string]$file.Name)
            Require (Test-Path -LiteralPath $path -PathType Leaf) 'A measured failover binary is missing after the run.'
            Require ((Hash $path) -ceq [string]$file.Sha256) 'A measured failover binary changed after the run.'
            Copy-Item -LiteralPath $path -Destination (Join-Path $destination ([string]$file.Name))
        }
    }
    Assert-Binaries $report
    $files = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | ForEach-Object {
            [ordered]@{ Path = [IO.Path]::GetRelativePath($evidence, $_.FullName).Replace('\', '/'); Sha256 = Hash $_.FullName }
        } | Sort-Object { $_.Path })
    [ordered]@{
        SchemaVersion = 1
        Family = $Family
        CandidateSha = $ExpectedCommit.ToLowerInvariant()
        Qualification = [string]$familyPolicy.qualification
        Repetitions = [int]$policy.repetitions
        SourceTreeSha256 = [string]$report.SourceProvenance.Before.sourceTreeSha256
        ProductionQualified = $false
        Files = $files
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}

$manifest = Json $manifestPath
Require ($manifest.SchemaVersion -eq 1 -and [string]$manifest.Family -ceq $Family -and
    [string]$manifest.CandidateSha -ieq $ExpectedCommit -and
    [string]$manifest.Qualification -ceq [string]$familyPolicy.qualification -and
    [int]$manifest.Repetitions -eq [int]$policy.repetitions -and
    $manifest.ProductionQualified -eq $false) "$Family failover manifest is not bound to this candidate."
$listed = @($manifest.Files)
$actual = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | Where-Object { $_.FullName -ne $manifestPath })
Require ($listed.Count -gt 0 -and $listed.Count -eq $actual.Count) "$Family failover artifact file count changed."
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $listed)
{
    Require ([string]$entry.Path -match '^[^/\\]+(?:/[^/\\]+)*$' -and -not ([string]$entry.Path).Contains('..') -and
        $seen.Add([string]$entry.Path)) "$Family failover manifest contains an unsafe or duplicate path."
    $path = Join-Path $evidence ([string]$entry.Path)
    Require (Test-Path -LiteralPath $path -PathType Leaf) "$Family failover artifact file is missing."
    Require ([string]$entry.Sha256 -ceq (Hash $path)) "$Family failover artifact file hash changed."
}
$report = Assert-Report
Require ([string]$manifest.SourceTreeSha256 -ceq [string]$report.SourceProvenance.Before.sourceTreeSha256) "$Family failover source tree differs from the manifest."
Assert-Binaries $report
Write-Output "Archived $Family failover evidence verified for exact candidate $ExpectedCommit; production qualification remains false."
