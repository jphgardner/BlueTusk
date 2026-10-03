[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Run', 'Verify')][string] $Mode = 'Verify',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedCommit,
    # The old commit is resolved deterministically by resolve-jobs-upgrade-baseline.ps1. An
    # explicit value is only an acknowledgement and must equal that baseline.
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string] $OldCommit,
    [string] $EvidenceRoot = 'artifacts/jobs-release-upgrade/local'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = if ([IO.Path]::IsPathRooted($EvidenceRoot)) {
    [IO.Path]::GetFullPath($EvidenceRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $repository $EvidenceRoot))
}
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/jobs-release-upgrade')) +
    [IO.Path]::DirectorySeparatorChar
$reportPath = Join-Path $evidence 'report.json'
$manifestPath = Join-Path $evidence 'manifest.json'
$raw = Join-Path $evidence 'raw'
$oldArchive = Join-Path $raw 'old-source.tar'
$oldSource = Join-Path ([IO.Path]::GetTempPath()) (
    'bluetusk-jobs-upgrade-old-' + [Guid]::NewGuid().ToString('N'))
$oldBinary = Join-Path $evidence 'old-binary'
$candidateBinary = Join-Path $evidence 'candidate-binary'
$oldPackage = Join-Path $evidence 'old-package'
$candidatePackage = Join-Path $evidence 'candidate-package'
$probeProject = Join-Path $repository 'eng/JobsUpgradeProbe/JobsUpgradeProbe.csproj'
$probeSource = Join-Path $repository 'eng/JobsUpgradeProbe/Program.cs'

function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Invoke-GitCommand([string[]] $Arguments)
{
    $result = & git -C $repository @Arguments
    Require ($LASTEXITCODE -eq 0) "Git failed: $($Arguments -join ' ')"
    return [string]($result | Select-Object -First 1)
}
function RequireExit([string] $Action)
{
    Require ($LASTEXITCODE -eq 0) "$Action failed. Retained diagnostics are not qualification evidence."
}
function PackageDllHash([string] $Package)
{
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try
    {
        $entries = @($zip.Entries | Where-Object {
            [string]$_.FullName -ceq 'lib/net10.0/BlueTusk.Jobs.dll'
        })
        Require ($entries.Count -eq 1) 'Jobs package lacks one net10.0 Jobs assembly.'
        $stream = $entries[0].Open()
        try
        {
            $digest = [Security.Cryptography.SHA256]::HashData($stream)
            return [Convert]::ToHexString($digest).ToLowerInvariant()
        }
        finally { $stream.Dispose() }
    }
    finally { $zip.Dispose() }
}
function InstallPackageDll([string] $Package, [string] $Destination)
{
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try
    {
        $entries = @($zip.Entries | Where-Object {
            [string]$_.FullName -ceq 'lib/net10.0/BlueTusk.Jobs.dll'
        })
        Require ($entries.Count -eq 1) 'Jobs package lacks one net10.0 Jobs assembly.'
        $inputStream = $entries[0].Open()
        try
        {
            $outputStream = [IO.File]::Create($Destination)
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose() }
        }
        finally { $inputStream.Dispose() }
    }
    finally { $zip.Dispose() }
}
function WaitForSignal([string] $Path, [Diagnostics.Process] $Process)
{
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    while (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        Require (-not $Process.HasExited) 'Old Jobs process exited before candidate overlap.'
        Require ([DateTime]::UtcNow -lt $deadline) 'Old Jobs process did not signal readiness.'
        Start-Sleep -Milliseconds 200
    }
}
function AssertReport
{
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -Depth 100
    Require ([int]$report.SchemaVersion -eq 1 -and [string]$report.Family -ceq 'Jobs' -and
        [string]$report.CandidateSha -ieq $ExpectedCommit -and
        [string]$report.OldSha -ieq $OldCommit -and
        $report.ProductionQualified -eq $false -and
        [string]$report.SupportedBoundary -ceq 'format-one-unchanged-admission-limits') 'Jobs upgrade report does not identify the exact format-one binary boundary.'
    Require ([string]$report.OldSourceTree -ceq (Invoke-GitCommand -Arguments @('rev-parse', "$OldCommit`^{tree}")) -and
        [string]$report.CandidateSourceTree -ceq (Invoke-GitCommand -Arguments @('rev-parse', 'HEAD^{tree}')) -and
        [string]$report.OldJobsTree -ceq $oldJobsTree -and
        [string]$report.CandidateJobsTree -ceq $candidateJobsTree -and
        [string]$report.ProbeSha256 -ceq (Hash $probeSource) -and
        [string]$report.VerifierSha256 -ceq (Hash (Join-Path $PSScriptRoot 'verify-jobs-release-upgrade.ps1'))) 'Jobs upgrade source provenance changed.'
    $recordedBaseline = $report.PSObject.Properties['Baseline']
    Require ($null -ne $recordedBaseline -and $null -ne $recordedBaseline.Value -and
        [string]$report.BaselineResolverSha256 -ceq (Hash (Join-Path $PSScriptRoot 'resolve-jobs-upgrade-baseline.ps1')) -and
        (($recordedBaseline.Value | ConvertTo-Json -Depth 5 -Compress) -ceq ($baseline | ConvertTo-Json -Depth 5 -Compress))) (
        'Jobs upgrade evidence does not record the deterministic baseline resolution for this candidate.')
    $oldPackagePath = Join-Path $evidence ([string]$report.OldJobsPackagePath)
    $candidatePackagePath = Join-Path $evidence ([string]$report.CandidateJobsPackagePath)
    Require ([string]$report.OldJobsPackagePath -cmatch '^old-package/BlueTusk\.Jobs\.[A-Za-z0-9_.-]+\.nupkg$' -and
        [string]$report.CandidateJobsPackagePath -cmatch '^candidate-package/BlueTusk\.Jobs\.[A-Za-z0-9_.-]+\.nupkg$') 'Jobs package paths must stay inside their artifact directories.'
    Require ([string]$report.OldArchiveSha256 -ceq (Hash $oldArchive) -and
        [string]$report.OldJobsPackageSha256 -ceq (Hash $oldPackagePath) -and
        [string]$report.CandidateJobsPackageSha256 -ceq (Hash $candidatePackagePath)) 'Jobs upgrade source archive or package bytes changed.'
    $oldDll = Join-Path $oldBinary 'BlueTusk.Jobs.dll'
    $newDll = Join-Path $candidateBinary 'BlueTusk.Jobs.dll'
    Require ([string]$report.OldJobsDllSha256 -ceq (Hash $oldDll) -and
        [string]$report.CandidateJobsDllSha256 -ceq (Hash $newDll) -and
        [string]$report.OldJobsDllSha256 -cne [string]$report.CandidateJobsDllSha256 -and
        [string]$report.OldJobsDllSha256 -ceq (PackageDllHash $oldPackagePath) -and
        [string]$report.CandidateJobsDllSha256 -ceq (PackageDllHash $candidatePackagePath)) 'Old and candidate Jobs binaries must be distinct and match their archived packages.'
    foreach ($phase in @('seed', 'advance', 'rollback'))
    {
        $path = Join-Path $raw "$phase.json"
        $entry = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        Require ([string]$entry.Phase -ceq $phase -and $entry.Passed -eq $true -and
            [int]$entry.FormatVersion -eq 1) "Jobs upgrade $phase phase did not pass on format one."
        $expected = switch ($phase) {
            seed { @(1, 0, 0) }
            advance { @(0, 2, 0) }
            rollback { @(0, 0, 1) }
        }
        Require ([int]$entry.OldOverlapEffects -eq $expected[0] -and
            [int]$entry.CandidateEffects -eq $expected[1] -and
            [int]$entry.RollbackEffects -eq $expected[2] -and
            [string]$report.PhaseHashes.$phase -ceq (Hash $path)) "Jobs upgrade $phase effects or raw report hash changed."
    }
    foreach ($signal in @('old-ready', 'candidate-ready', 'old-done'))
    {
        Require (Test-Path -LiteralPath (Join-Path $raw $signal) -PathType Leaf) "Jobs upgrade overlap signal $signal is missing."
    }
    return $report
}

Require ($evidence.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) 'Jobs upgrade evidence must use its product directory.'
$head = (Invoke-GitCommand -Arguments @('rev-parse', 'HEAD')).Trim()
Require ($head -ieq $ExpectedCommit -and $OldCommit -ine $ExpectedCommit) 'Jobs upgrade requires distinct exact old and candidate commits.'
Require (@(& git -C $repository status --porcelain --untracked-files=normal).Count -eq 0) 'Jobs upgrade requires a clean candidate checkout.'
# Jobs has never been published, so the rehearsal upgrades from the deterministic baseline:
# the state immediately before the most recent change to the Jobs-owned sources.
$baseline = & (Join-Path $PSScriptRoot 'resolve-jobs-upgrade-baseline.ps1') -CandidateCommit $head -RepositoryRoot $repository
Require ($null -ne $baseline -and [string]$baseline.CandidateCommit -ceq $head.ToLowerInvariant() -and
    [string]$baseline.BaselineCommit -cmatch '^[0-9a-f]{40}$') 'Jobs upgrade baseline resolution did not identify the candidate and one old commit.'
if (-not [string]::IsNullOrWhiteSpace($OldCommit))
{
    Require ($OldCommit -ieq [string]$baseline.BaselineCommit) (
        "Requested old commit $OldCommit is not the deterministic Jobs upgrade baseline $($baseline.BaselineCommit).")
}
$OldCommit = [string]$baseline.BaselineCommit
& git -C $repository cat-file -e "$OldCommit`^{commit}"
RequireExit 'Old Jobs commit lookup'
& git -C $repository merge-base --is-ancestor $OldCommit $ExpectedCommit
RequireExit 'Old Jobs commit ancestry check'
& git -C $repository cat-file -e "$OldCommit`:src/BlueTusk.Jobs/BlueTusk.Jobs.csproj"
RequireExit 'Old Jobs preview project lookup'
$oldJobsTree = (Invoke-GitCommand -Arguments @('rev-parse', "$OldCommit`:src/BlueTusk.Jobs")).Trim()
$candidateJobsTree = (Invoke-GitCommand -Arguments @('rev-parse', 'HEAD:src/BlueTusk.Jobs')).Trim()
Require ($oldJobsTree -cne $candidateJobsTree) 'Old and candidate Jobs source trees are identical; a cross-binary rehearsal needs a changed candidate Jobs implementation.'
Require (Test-Path -LiteralPath $probeSource -PathType Leaf) 'Jobs upgrade probe source is missing.'
if ($Mode -eq 'Preflight')
{
    Require (-not (Test-Path -LiteralPath $evidence)) 'Jobs upgrade requires a fresh evidence directory.'
    Write-Output (
        "Clean candidate $head and deterministic old commit $OldCommit (first parent of Jobs-owned " +
        "change $($baseline.ChangeCommit); Jobs trees $($baseline.BaselineJobsTree) -> " +
        "$($baseline.CandidateJobsTree)) verified; no database was used.")
    return
}

if ($Mode -eq 'Run')
{
    Require (-not (Test-Path -LiteralPath $evidence)) 'Jobs upgrade requires a fresh evidence directory.'
    foreach ($directory in @($raw, $oldBinary, $candidateBinary, $oldPackage, $candidatePackage))
    {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $oldTree = (Invoke-GitCommand -Arguments @('rev-parse', "$OldCommit`^{tree}")).Trim()
    $candidateTree = (Invoke-GitCommand -Arguments @('rev-parse', 'HEAD^{tree}')).Trim()
    & git -C $repository archive --format=tar $OldCommit -o $oldArchive
    RequireExit 'Old Jobs source archive'
    $candidateProject = Join-Path $repository 'src/BlueTusk.Jobs/BlueTusk.Jobs.csproj'
    & git -C $repository worktree add --detach $oldSource $OldCommit
    RequireExit 'Old Jobs isolated worktree creation'
    try
    {
        Require ((& git -C $oldSource rev-parse HEAD).Trim() -ieq $OldCommit -and
            @(& git -C $oldSource status --porcelain).Count -eq 0) 'Old Jobs worktree is not clean at the requested commit.'
        $oldProject = Join-Path $oldSource 'src/BlueTusk.Jobs/BlueTusk.Jobs.csproj'
        Require (Test-Path -LiteralPath $oldProject -PathType Leaf) 'Old commit predates Jobs preview.'
        & dotnet pack $oldProject -c Release -o $oldPackage --artifacts-path (Join-Path $raw 'old-pack-build')
        RequireExit 'Old Jobs package build'
        & dotnet pack $candidateProject -c Release -o $candidatePackage --artifacts-path (Join-Path $raw 'candidate-pack-build')
        RequireExit 'Candidate Jobs package build'
        & dotnet publish $probeProject -c Release -p:JobsProject=$oldProject -o $oldBinary --artifacts-path (Join-Path $raw 'old-probe-build')
        RequireExit 'Old Jobs executable build'
        & dotnet publish $probeProject -c Release -p:JobsProject=$candidateProject -o $candidateBinary --artifacts-path (Join-Path $raw 'candidate-probe-build')
        RequireExit 'Candidate Jobs executable build'
    }
    finally
    {
        $resolvedOldSource = [IO.Path]::GetFullPath($oldSource)
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
            [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        Require ($resolvedOldSource.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedOldSource).StartsWith('bluetusk-jobs-upgrade-old-', [StringComparison]::Ordinal)) 'Refusing to remove a worktree outside the explicit Jobs upgrade temporary root.'
        & git -C $repository worktree remove --force $oldSource
        RequireExit 'Old Jobs isolated worktree cleanup'
    }
    $oldNupkg = @(Get-ChildItem -LiteralPath $oldPackage -Filter 'BlueTusk.Jobs.*.nupkg' -File)
    $candidateNupkg = @(Get-ChildItem -LiteralPath $candidatePackage -Filter 'BlueTusk.Jobs.*.nupkg' -File)
    Require ($oldNupkg.Count -eq 1 -and $candidateNupkg.Count -eq 1) 'Each exact source must produce one Jobs package.'
    InstallPackageDll $oldNupkg[0].FullName (Join-Path $oldBinary 'BlueTusk.Jobs.dll')
    InstallPackageDll $candidateNupkg[0].FullName (Join-Path $candidateBinary 'BlueTusk.Jobs.dll')
    $oldDllHash = Hash (Join-Path $oldBinary 'BlueTusk.Jobs.dll')
    $candidateDllHash = Hash (Join-Path $candidateBinary 'BlueTusk.Jobs.dll')
    Require ($oldDllHash -cne $candidateDllHash) 'Old and candidate Jobs DLL bytes are identical; this is not a cross-binary rehearsal.'
    Require ($oldDllHash -ceq (PackageDllHash $oldNupkg[0].FullName) -and
        $candidateDllHash -ceq (PackageDllHash $candidateNupkg[0].FullName)) 'Executed Jobs DLLs differ from their packaged DLLs.'
    foreach ($name in @('old-pack-build', 'candidate-pack-build', 'old-probe-build', 'candidate-probe-build'))
    {
        $buildPath = [IO.Path]::GetFullPath((Join-Path $raw $name))
        Require ($buildPath.StartsWith($raw + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($buildPath) -ceq $name) 'Refusing to clean an unexpected Jobs upgrade build directory.'
        if (Test-Path -LiteralPath $buildPath) { Remove-Item -LiteralPath $buildPath -Recurse -Force }
    }
    $schema = 'jobs_upgrade_' + ([Guid]::NewGuid().ToString('N'))
    $statePath = Join-Path $raw 'state.json'
    $oldExe = Join-Path $oldBinary 'JobsUpgradeProbe.dll'
    $newExe = Join-Path $candidateBinary 'JobsUpgradeProbe.dll'
    $oldArguments = @($oldExe, 'seed', $schema, $statePath, $raw, (Join-Path $raw 'seed.json')) |
        ForEach-Object { '"' + $_ + '"' }
    $oldProcess = Start-Process -FilePath 'dotnet' -ArgumentList $oldArguments -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $raw 'old.stdout.log') -RedirectStandardError (Join-Path $raw 'old.stderr.log')
    try
    {
        WaitForSignal (Join-Path $raw 'old-ready') $oldProcess
        & dotnet $newExe advance $schema $statePath $raw (Join-Path $raw 'advance.json') `
            *> (Join-Path $raw 'candidate.log')
        RequireExit 'Candidate Jobs overlapping process'
        Require ($oldProcess.WaitForExit(120000) -and $oldProcess.ExitCode -eq 0) 'Old Jobs overlapping process did not complete successfully.'
        & dotnet $oldExe rollback $schema $statePath $raw (Join-Path $raw 'rollback.json') `
            *> (Join-Path $raw 'rollback.log')
        RequireExit 'Old Jobs rollback process'
    }
    finally
    {
        if (-not $oldProcess.HasExited) { $oldProcess.Kill($true) }
        $oldProcess.Dispose()
    }
    $phaseHashes = [ordered]@{}
    foreach ($phase in @('seed', 'advance', 'rollback'))
    {
        $phaseHashes[$phase] = Hash (Join-Path $raw "$phase.json")
    }
    [ordered]@{
        SchemaVersion = 1
        Family = 'Jobs'
        CandidateSha = $ExpectedCommit.ToLowerInvariant()
        OldSha = $OldCommit.ToLowerInvariant()
        OldSourceTree = $oldTree
        CandidateSourceTree = $candidateTree
        OldJobsTree = $oldJobsTree
        CandidateJobsTree = $candidateJobsTree
        Baseline = $baseline
        BaselineResolverSha256 = Hash (Join-Path $PSScriptRoot 'resolve-jobs-upgrade-baseline.ps1')
        OldArchiveSha256 = Hash $oldArchive
        ProbeSha256 = Hash $probeSource
        VerifierSha256 = Hash (Join-Path $PSScriptRoot 'verify-jobs-release-upgrade.ps1')
        OldJobsPackagePath = 'old-package/' + $oldNupkg[0].Name
        CandidateJobsPackagePath = 'candidate-package/' + $candidateNupkg[0].Name
        OldJobsPackageSha256 = Hash $oldNupkg[0].FullName
        CandidateJobsPackageSha256 = Hash $candidateNupkg[0].FullName
        OldJobsDllSha256 = $oldDllHash
        CandidateJobsDllSha256 = $candidateDllHash
        SupportedBoundary = 'format-one-unchanged-admission-limits'
        PhaseHashes = $phaseHashes
        ProductionQualified = $false
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
    AssertReport | Out-Null
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
        OldSha = $OldCommit.ToLowerInvariant()
        ProductionQualified = $false
        Files = $files
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Require ([int]$manifest.SchemaVersion -eq 1 -and [string]$manifest.Family -ceq 'Jobs' -and
    [string]$manifest.CandidateSha -ieq $ExpectedCommit -and [string]$manifest.OldSha -ieq $OldCommit -and
    $manifest.ProductionQualified -eq $false) 'Jobs upgrade manifest is not bound to both exact commits.'
$listed = @($manifest.Files)
$actual = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | Where-Object { $_.FullName -ne $manifestPath })
Require ($listed.Count -gt 0 -and $listed.Count -eq $actual.Count) 'Jobs upgrade artifact file count changed.'
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $listed)
{
    Require ([string]$entry.Path -match '^[^/\\]+(?:/[^/\\]+)*$' -and
        -not ([string]$entry.Path).Contains('..') -and
        $seen.Add([string]$entry.Path)) 'Unsafe or duplicate Jobs upgrade artifact path.'
    $path = Join-Path $evidence ([string]$entry.Path)
    Require (Test-Path -LiteralPath $path -PathType Leaf) 'A Jobs upgrade artifact file is missing.'
    Require ([string]$entry.Sha256 -ieq (Hash $path)) 'A Jobs upgrade artifact file hash changed.'
}
AssertReport | Out-Null
Write-Output (
    "Archived Jobs old/candidate/rollback evidence verified for $ExpectedCommit against deterministic " +
    "baseline $OldCommit; publication remains disabled.")
