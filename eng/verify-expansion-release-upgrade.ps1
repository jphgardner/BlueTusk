<#
.SYNOPSIS
Produce or re-verify one expansion family's exact-candidate binary upgrade and rollback rehearsal.
.DESCRIPTION
Preflight checks the clean candidate, the deterministic baseline and the family policy without a
database. Produce archives the baseline source, packs both builds of the family's packaged projects,
publishes the family probe against each immutable source tree, installs the packaged assemblies into
the probe outputs and runs three processes against the same disposable PostgreSQL database: seed
(baseline), upgrade (candidate) and rollback (baseline). Verify re-checks every retained file,
the baseline resolution, the package and assembly bytes, the phase reports, their catalog digests
and the exact effect counts in eng/expansion-upgrade-policy.json. Jobs keeps its dedicated gate.
.EXAMPLE
./eng/verify-expansion-release-upgrade.ps1 -Family Projections -Mode Produce -ExpectedCommit <full-sha>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Events', 'Documents', 'Schema', 'Projections', 'Search', 'Sql',
        'Studio', 'Edge', 'Workflows')][string] $Family,
    [ValidateSet('Preflight', 'Produce', 'Verify')][string] $Mode = 'Verify',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $ExpectedCommit,
    # The old commit is resolved by resolve-expansion-upgrade-baseline.ps1. An explicit value is
    # only an acknowledgement and must equal that baseline.
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string] $OldCommit,
    [string] $EvidenceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'expansion-upgrade-evidence.psm1') -Force
# ValidateSet binding is case-insensitive; the family identity is not.
$Family = Get-ExpansionUpgradeFamily (@('Events', 'Documents', 'Schema', 'Projections', 'Search', 'Sql', 'Studio', 'Edge',
    'Workflows') | Where-Object { $_ -ieq $Family } | Select-Object -First 1)
$slug = $Family.ToLowerInvariant()
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) { $EvidenceRoot = "artifacts/$slug-release-upgrade/local" }
$evidence = if ([IO.Path]::IsPathRooted($EvidenceRoot)) {
    [IO.Path]::GetFullPath($EvidenceRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $repository $EvidenceRoot))
}
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository "artifacts/$slug-release-upgrade")) +
    [IO.Path]::DirectorySeparatorChar
$reportPath = Join-Path $evidence 'report.json'
$manifestPath = Join-Path $evidence 'manifest.json'
$raw = Join-Path $evidence 'raw'
$oldArchive = Join-Path $raw 'old-source.tar'
$oldBinary = Join-Path $evidence 'old-binary'
$candidateBinary = Join-Path $evidence 'candidate-binary'
$oldPackage = Join-Path $evidence 'old-package'
$candidatePackage = Join-Path $evidence 'candidate-package'
$probeRoot = Join-Path $repository 'eng/ExpansionUpgradeProbe'
$probeProject = Join-Path $probeRoot 'ExpansionUpgradeProbe.csproj'
$probeSources = @('ExpansionUpgradeProbe.csproj', 'Program.cs', 'ProbeSupport.cs', "${Family}Probe.cs")
$phases = [ordered]@{ seed = 'old'; upgrade = 'candidate'; rollback = 'old' }

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
function Get-ProbeSourceHashes
{
    $hashes = [ordered]@{}
    foreach ($name in $probeSources)
    {
        $path = Join-Path $probeRoot $name
        Require (Test-Path -LiteralPath $path -PathType Leaf) "$Family upgrade probe source '$name' is missing."
        $hashes[$name] = Hash $path
    }
    return $hashes
}
function Get-AssemblyName([string] $Project) { [IO.Path]::GetFileNameWithoutExtension($Project) }
function Get-PackageDll([string] $Package, [string] $Assembly, [string] $Destination)
{
    # Returns the SHA-256 of the package's net10.0 assembly and optionally extracts it.
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    try
    {
        $entries = @($zip.Entries | Where-Object { [string]$_.FullName -ceq "lib/net10.0/$Assembly.dll" })
        Require ($entries.Count -eq 1) "$Assembly package lacks one net10.0 assembly."
        $stream = $entries[0].Open()
        try
        {
            if ([string]::IsNullOrEmpty($Destination))
            {
                return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
            }
            $output = [IO.File]::Create($Destination)
            try { $stream.CopyTo($output) } finally { $output.Dispose() }
            return Hash $Destination
        }
        finally { $stream.Dispose() }
    }
    finally { $zip.Dispose() }
}
function Find-Package([string] $Directory, [string] $Assembly)
{
    $pattern = '^' + [regex]::Escape($Assembly) + '\.\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\.nupkg$'
    $found = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Name -cmatch $pattern })
    Require ($found.Count -eq 1) "Each exact source must produce one $Assembly package."
    return $found[0]
}
function Remove-Directory([string] $Path, [string] $Parent)
{
    $resolved = [IO.Path]::GetFullPath($Path)
    Require ($resolved.StartsWith($Parent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) "Refusing to remove '$resolved' outside '$Parent'."
    if (Test-Path -LiteralPath $resolved) { [IO.Directory]::Delete($resolved, $true) }
}
function Invoke-Phase([string] $Phase, [string] $Binary, [string] $Schema, [int] $TimeoutSeconds)
{
    $arguments = @((Join-Path $Binary 'ExpansionUpgradeProbe.dll'), $Phase, $Schema, (Join-Path $raw 'state.json'),
        (Join-Path $raw "$Phase.json")) | ForEach-Object { '"' + $_ + '"' }
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $raw "$Phase.stdout.log") -RedirectStandardError (Join-Path $raw "$Phase.stderr.log")
    try
    {
        $exited = $process.WaitForExit($TimeoutSeconds * 1000)
        if (-not $exited) { $process.Kill($true) }
        Require $exited "$Family $Phase phase exceeded its $TimeoutSeconds-second policy deadline."
        $process.WaitForExit()
        Require ($process.ExitCode -eq 0) "$Family $Phase phase failed; see raw/$Phase.stderr.log."
    }
    finally { $process.Dispose() }
}
function AssertReport
{
    $report = Read-CoreEvidenceJson $reportPath
    Require ([int]$report.SchemaVersion -eq 1 -and [string]$report.Family -ceq $Family -and
        [string]$report.CandidateSha -ceq $ExpectedCommit.ToLowerInvariant() -and
        [string]$report.OldSha -ceq $OldCommit.ToLowerInvariant() -and
        [string]$report.Boundary -ceq [string]$policy.Entry.boundary -and
        [string]$report.SchemaChange -ceq [string]$policy.Entry.schemaChange -and
        [string]$report.PostgreSqlImage -ceq $policy.PostgreSqlImage -and
        $report.ProductionQualified -eq $false) "$Family upgrade report does not identify this exact candidate boundary."
    Require ([string]$report.OldSourceTree -ceq (Invoke-GitCommand @('rev-parse', "$OldCommit`^{tree}")) -and
        [string]$report.CandidateSourceTree -ceq (Invoke-GitCommand @('rev-parse', 'HEAD^{tree}')) -and
        [string]$report.VerifierSha256 -ceq (Hash $PSCommandPath) -and
        [string]$report.EvidenceModuleSha256 -ceq (Hash (Join-Path $PSScriptRoot 'expansion-upgrade-evidence.psm1')) -and
        [string]$report.PolicySha256 -ceq (Hash (Join-Path $PSScriptRoot 'expansion-upgrade-policy.json')) -and
        [string]$report.BaselineResolverSha256 -ceq (Hash (Join-Path $PSScriptRoot 'resolve-expansion-upgrade-baseline.ps1')) -and
        (($report.ProbeSha256 | ConvertTo-Json -Compress) -ceq ((Get-ProbeSourceHashes) | ConvertTo-Json -Compress))) (
        "$Family upgrade source, policy or probe provenance changed.")
    Require ((($report.Baseline | ConvertTo-Json -Depth 5 -Compress) -ceq ($baseline | ConvertTo-Json -Depth 5 -Compress))) (
        "$Family upgrade evidence does not record the deterministic baseline resolution for this candidate.")
    Require ([string]$report.OldArchiveSha256 -ceq (Hash $oldArchive)) "$Family upgrade baseline source archive changed."
    $assemblies = @($report.Assemblies)
    $projects = @($policy.Entry.packagedProjects)
    Require ($assemblies.Count -eq $projects.Count) "$Family upgrade report does not list every packaged project."
    $distinct = 0
    for ($index = 0; $index -lt $projects.Count; $index++)
    {
        $entry = $assemblies[$index]
        $assembly = Get-AssemblyName $projects[$index]
        Require ([string]$entry.Project -ceq $projects[$index] -and [string]$entry.Assembly -ceq $assembly) (
            "$Family upgrade assembly list differs from the policy.")
        foreach ($side in @('Old', 'Candidate'))
        {
            $relative = [string]$entry."${side}PackagePath"
            $directory = if ($side -ceq 'Old') { 'old-package' } else { 'candidate-package' }
            $binary = if ($side -ceq 'Old') { $oldBinary } else { $candidateBinary }
            Require ($relative -cmatch ('^' + $directory + '/' + [regex]::Escape($assembly) + '\.[0-9][0-9A-Za-z.-]*\.nupkg$')) (
                "$assembly $side package path must stay inside its artifact directory.")
            $package = Join-Path $evidence $relative
            Require ([string]$entry."${side}PackageSha256" -ceq (Hash $package) -and
                [string]$entry."${side}DllSha256" -ceq (Hash (Join-Path $binary "$assembly.dll")) -and
                [string]$entry."${side}DllSha256" -ceq (Get-PackageDll $package $assembly $null)) (
                "$assembly $side executed assembly differs from its archived package.")
        }
        if ([string]$entry.OldDllSha256 -cne [string]$entry.CandidateDllSha256) { $distinct++ }
    }
    Require ($distinct -gt 0) "$Family old and candidate assemblies are byte-identical; this is not a cross-binary rehearsal."
    $reports = @{}
    $digests = @{}
    foreach ($phase in $phases.Keys)
    {
        $path = Join-Path $raw "$phase.json"
        Require ([string]$report.PhaseHashes.$phase -ceq (Hash $path)) "$Family upgrade $phase raw report hash changed."
        $reports[$phase] = Read-CoreEvidenceJson $path
        $digests[$phase] = @{}
        foreach ($label in @('before', 'after'))
        {
            $catalog = Join-Path $raw "$phase.catalog-$label.txt"
            if (Test-Path -LiteralPath $catalog -PathType Leaf) { $digests[$phase][$label] = Hash $catalog }
        }
    }
    Assert-ExpansionUpgradePhases -Policy $policy -Reports $reports -CatalogDigests $digests
    return $report
}

Require ($evidence.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) (
    "$Family upgrade evidence must use artifacts/$slug-release-upgrade.")
$policy = Get-ExpansionUpgradePolicy -Family $Family -RepositoryRoot $repository
$head = (Invoke-GitCommand @('rev-parse', 'HEAD')).Trim()
Require ($head -ieq $ExpectedCommit -and $OldCommit -ine $ExpectedCommit) (
    "$Family upgrade requires distinct exact old and candidate commits.")
Require (@(& git -C $repository status --porcelain --untracked-files=normal).Count -eq 0) (
    "$Family upgrade requires a clean candidate checkout.")
# No expansion family has been published, so the rehearsal upgrades from the deterministic
# baseline: the state immediately before the most recent change to the family-owned sources.
$baseline = & (Join-Path $PSScriptRoot 'resolve-expansion-upgrade-baseline.ps1') -Family $Family -CandidateCommit $head -RepositoryRoot $repository
Require ($null -ne $baseline -and [string]$baseline.Family -ceq $Family -and
    [string]$baseline.CandidateCommit -ceq $head.ToLowerInvariant() -and
    [string]$baseline.BaselineCommit -cmatch '^[0-9a-f]{40}$') "$Family upgrade baseline resolution did not identify the candidate and one old commit."
if (-not [string]::IsNullOrWhiteSpace($OldCommit))
{
    Require ($OldCommit -ieq [string]$baseline.BaselineCommit) (
        "Requested old commit $OldCommit is not the deterministic $Family upgrade baseline $($baseline.BaselineCommit).")
}
$OldCommit = [string]$baseline.BaselineCommit
& git -C $repository merge-base --is-ancestor $OldCommit $ExpectedCommit
RequireExit "Old $Family commit ancestry check"
foreach ($project in @($policy.Entry.packagedProjects))
{
    & git -C $repository cat-file -e "$OldCommit`:$project"
    RequireExit "Old $Family packaged project '$project' lookup"
}
$probeHashes = Get-ProbeSourceHashes
if ($Mode -eq 'Preflight')
{
    Require (-not (Test-Path -LiteralPath $evidence)) "$Family upgrade requires a fresh evidence directory."
    Write-Output (
        "Clean candidate $head and deterministic $Family baseline $OldCommit (first parent of $Family-owned change " +
        "$($baseline.ChangeCommit)) verified; boundary '$($policy.Entry.boundary)'; no database was used.")
    return
}

if ($Mode -eq 'Produce')
{
    Require (-not (Test-Path -LiteralPath $evidence)) "$Family upgrade requires a fresh evidence directory."
    Require (-not [string]::IsNullOrWhiteSpace($env:BLUETUSK_TEST_CONNECTION_STRING)) (
        "$Family upgrade needs BLUETUSK_TEST_CONNECTION_STRING for its owned disposable PostgreSQL fixture.")
    foreach ($directory in @($raw, $oldBinary, $candidateBinary, $oldPackage, $candidatePackage))
    {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $oldTree = (Invoke-GitCommand @('rev-parse', "$OldCommit`^{tree}")).Trim()
    $candidateTree = (Invoke-GitCommand @('rev-parse', 'HEAD^{tree}')).Trim()
    & git -C $repository archive --format=tar $OldCommit -o $oldArchive
    RequireExit "Old $Family source archive"
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $oldSource = Join-Path $tempRoot ("bluetusk-$slug-upgrade-old-" + [Guid]::NewGuid().ToString('N'))
    # The baseline tree contains paths longer than the legacy Windows limit under a temporary root.
    & git -C $repository -c core.longpaths=true worktree add --detach $oldSource $OldCommit
    RequireExit "Old $Family isolated worktree creation"
    try
    {
        Require ((& git -C $oldSource rev-parse HEAD).Trim() -ieq $OldCommit -and
            @(& git -C $oldSource status --porcelain).Count -eq 0) "Old $Family worktree is not clean at the baseline."
        foreach ($project in @($policy.Entry.packagedProjects))
        {
            $assembly = Get-AssemblyName $project
            & dotnet pack (Join-Path $oldSource $project) -c Release -o $oldPackage --artifacts-path (Join-Path $raw "old-pack-$assembly")
            RequireExit "Old $assembly package build"
            & dotnet pack (Join-Path $repository $project) -c Release -o $candidatePackage --artifacts-path (Join-Path $raw "candidate-pack-$assembly")
            RequireExit "Candidate $assembly package build"
        }
        & dotnet publish $probeProject -c Release "-p:UpgradeFamily=$Family" "-p:FamilySourceRoot=$oldSource/" -o $oldBinary `
            --artifacts-path (Join-Path $raw 'old-probe-build')
        RequireExit "Old $Family probe build"
        & dotnet publish $probeProject -c Release "-p:UpgradeFamily=$Family" "-p:FamilySourceRoot=$repository/" -o $candidateBinary `
            --artifacts-path (Join-Path $raw 'candidate-probe-build')
        RequireExit "Candidate $Family probe build"
    }
    finally
    {
        Require ([IO.Path]::GetFileName($oldSource).StartsWith("bluetusk-$slug-upgrade-old-", [StringComparison]::Ordinal) -and
            $oldSource.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) (
            "Refusing to remove a worktree outside the explicit $Family upgrade temporary root.")
        & git -C $repository -c core.longpaths=true worktree remove --force $oldSource
        RequireExit "Old $Family isolated worktree cleanup"
    }
    $assemblies = @(foreach ($project in @($policy.Entry.packagedProjects))
    {
        $assembly = Get-AssemblyName $project
        $old = Find-Package $oldPackage $assembly
        $candidate = Find-Package $candidatePackage $assembly
        [ordered]@{
            Project = $project
            Assembly = $assembly
            OldPackagePath = 'old-package/' + $old.Name
            CandidatePackagePath = 'candidate-package/' + $candidate.Name
            OldPackageSha256 = Hash $old.FullName
            CandidatePackageSha256 = Hash $candidate.FullName
            OldDllSha256 = Get-PackageDll $old.FullName $assembly (Join-Path $oldBinary "$assembly.dll")
            CandidateDllSha256 = Get-PackageDll $candidate.FullName $assembly (Join-Path $candidateBinary "$assembly.dll")
        }
    })
    Require (@($assemblies | Where-Object { $_.OldDllSha256 -cne $_.CandidateDllSha256 }).Count -gt 0) (
        "$Family old and candidate assemblies are byte-identical; this is not a cross-binary rehearsal.")
    foreach ($directory in @(Get-ChildItem -LiteralPath $raw -Directory))
    {
        Require ($directory.Name -cmatch '^(?:old|candidate)-(?:pack-[A-Za-z0-9.]+|probe-build)$') (
            "Unexpected $Family upgrade build directory '$($directory.Name)'.")
        Remove-Directory $directory.FullName $raw
    }
    $schema = "u_${slug}_" + [Guid]::NewGuid().ToString('N').Substring(0, 12)
    foreach ($phase in $phases.Keys)
    {
        $binary = if ($phases[$phase] -ceq 'old') { $oldBinary } else { $candidateBinary }
        Invoke-Phase $phase $binary $schema ([int]$policy.Entry.phaseTimeoutSeconds.$phase)
    }
    $phaseHashes = [ordered]@{}
    foreach ($phase in $phases.Keys) { $phaseHashes[$phase] = Hash (Join-Path $raw "$phase.json") }
    [ordered]@{
        SchemaVersion = 1
        Family = $Family
        CandidateSha = $ExpectedCommit.ToLowerInvariant()
        OldSha = $OldCommit.ToLowerInvariant()
        OldSourceTree = $oldTree
        CandidateSourceTree = $candidateTree
        Baseline = $baseline
        BaselineResolverSha256 = Hash (Join-Path $PSScriptRoot 'resolve-expansion-upgrade-baseline.ps1')
        PolicySha256 = Hash (Join-Path $PSScriptRoot 'expansion-upgrade-policy.json')
        EvidenceModuleSha256 = Hash (Join-Path $PSScriptRoot 'expansion-upgrade-evidence.psm1')
        VerifierSha256 = Hash $PSCommandPath
        ProbeSha256 = $probeHashes
        OldArchiveSha256 = Hash $oldArchive
        Assemblies = $assemblies
        Boundary = [string]$policy.Entry.boundary
        SchemaChange = [string]$policy.Entry.schemaChange
        PostgreSqlImage = $policy.PostgreSqlImage
        ProbeSchema = $schema
        PhaseHashes = $phaseHashes
        ProductionQualified = $false
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
    AssertReport | Out-Null
    $files = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | ForEach-Object {
        [ordered]@{
            Path = [IO.Path]::GetRelativePath($evidence, $_.FullName).Replace('\', '/')
            Sha256 = Hash $_.FullName
        }
    } | Sort-Object { $_.Path })
    [ordered]@{
        SchemaVersion = 1
        Family = $Family
        CandidateSha = $ExpectedCommit.ToLowerInvariant()
        OldSha = $OldCommit.ToLowerInvariant()
        ProductionQualified = $false
        Files = $files
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}

Require (Test-Path -LiteralPath $manifestPath -PathType Leaf) "$Family upgrade evidence has no manifest."
$manifest = Read-CoreEvidenceJson $manifestPath
Require ([int]$manifest.SchemaVersion -eq 1 -and [string]$manifest.Family -ceq $Family -and
    [string]$manifest.CandidateSha -ceq $ExpectedCommit.ToLowerInvariant() -and
    [string]$manifest.OldSha -ceq $OldCommit.ToLowerInvariant() -and
    $manifest.ProductionQualified -eq $false) "$Family upgrade manifest is not bound to this family and both exact commits."
$listed = @($manifest.Files)
$actual = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | Where-Object { $_.FullName -ne $manifestPath })
Require ($listed.Count -gt 0 -and $listed.Count -eq $actual.Count) "$Family upgrade artifact file count changed."
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $listed)
{
    Require ([string]$entry.Path -match '^[^/\\]+(?:/[^/\\]+)*$' -and
        -not ([string]$entry.Path).Contains('..') -and
        $seen.Add([string]$entry.Path)) "Unsafe or duplicate $Family upgrade artifact path."
    $path = Join-Path $evidence ([string]$entry.Path)
    Require (Test-Path -LiteralPath $path -PathType Leaf) "A $Family upgrade artifact file is missing."
    Require ([string]$entry.Sha256 -ceq (Hash $path)) "A $Family upgrade artifact file hash changed."
}
AssertReport | Out-Null
Write-Output (
    "Archived $Family seed/upgrade/rollback evidence verified for $ExpectedCommit against deterministic " +
    "baseline $OldCommit; publication remains disabled.")
