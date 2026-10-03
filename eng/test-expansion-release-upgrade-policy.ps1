<#
.SYNOPSIS
Offline self-test for the nine expansion upgrade gates (every family except Jobs, which keeps
test-jobs-release-upgrade-policy.ps1).
.DESCRIPTION
Checks the workflow contracts, the readiness verifier mapping, the policy and its time-budget rule,
the phase-evidence rules against synthetic substitutions, the deterministic baseline resolver on
synthetic Git histories, and that every family probe compiles against the candidate. No PostgreSQL,
container or release run is used, and nothing here certifies a rehearsal.
#>
[CmdletBinding()]
param([switch] $SkipProbeBuild)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'expansion-upgrade-evidence.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'expansion-candidate-evidence.psm1') -Force
$families = @('Projections', 'Documents', 'Workflows', 'Search', 'Edge', 'Events', 'Schema', 'Sql', 'Studio')
$release = Get-Content (Join-Path $PSScriptRoot 'expansion-release-policy.json') -Raw | ConvertFrom-Json
$verifierText = Get-Content (Join-Path $PSScriptRoot 'verify-expansion-release-upgrade.ps1') -Raw
$probeSupport = Get-Content (Join-Path $PSScriptRoot 'ExpansionUpgradeProbe/ProbeSupport.cs') -Raw
$rejected = 0

function Assert-Rejected([string] $Name, [scriptblock] $Action, [string] $Guard)
{
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($Guard))
    {
        throw "Substitution '$Name' was not rejected at '$Guard': '$failure'."
    }
    $script:rejected++
}

foreach ($script in @('verify-expansion-release-upgrade.ps1', 'resolve-expansion-upgrade-baseline.ps1',
    'test-expansion-release-upgrade-policy.ps1'))
{
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot $script), [ref]$null, [ref]$errors) | Out-Null
    if ($null -ne $errors -and $errors.Count -gt 0) { throw "$script has a PowerShell parse error: $($errors[0])" }
}
foreach ($boundary in @('resolve-expansion-upgrade-baseline.ps1', 'merge-base --is-ancestor', 'old-source.tar',
    'dotnet pack', 'dotnet publish', 'Get-PackageDll', 'not a cross-binary rehearsal', 'Assert-ExpansionUpgradePhases',
    'PhaseHashes', 'BaselineResolverSha256', 'PolicySha256', 'ProbeSha256', 'deterministic $Family upgrade baseline',
    "'Preflight', 'Produce', 'Verify'"))
{
    if (-not $verifierText.Contains($boundary)) { throw "Expansion upgrade verifier lost '$boundary'." }
}

# Workflow contracts: the policy names each file; each is a manual, exact-candidate, digest-pinned run
# on the reference host that uploads exactly one qualifying artifact.
foreach ($family in $families)
{
    $slug = $family.ToLowerInvariant()
    $file = [string]$release.families.$family.upgradeWorkflow
    if ($file -cne "$slug-release-upgrade.yml") { throw "$family upgrade workflow name differs from the release policy." }
    $workflow = Get-Content (Join-Path $root ".github/workflows/$file") -Raw
    $required = @(
        '(?m)^  workflow_dispatch:\s*$',
        [regex]::Escape('runs-on: [self-hosted, windows, x64, bluetusk-benchmark]'),
        [regex]::Escape('ref: ${{ inputs.candidate_sha }}'),
        'fetch-depth: 0',
        [regex]::Escape('$env:GITHUB_SHA -cne $env:CANDIDATE_SHA'),
        [regex]::Escape("RUN-$($family.ToUpperInvariant())-RELEASE-UPGRADE"),
        [regex]::Escape('$policy.postgreSqlImage'),
        [regex]::Escape("@sha256:[0-9a-f]{64}"),
        [regex]::Escape("--label bluetusk.owner=$slug-release-upgrade"),
        [regex]::Escape('::add-mask::'),
        'SSL Mode=Disable',
        [regex]::Escape("EVIDENCE_ROOT: artifacts/$slug-release-upgrade/"),
        [regex]::Escape("name: $slug-upgrade-partial-"),
        'group: bluetusk-reference-host')
    foreach ($pattern in $required)
    {
        if ($workflow -notmatch $pattern) { throw "$file lost its exact-candidate contract element '$pattern'." }
    }
    if ($workflow -match '(?m)^  (?:push|pull_request|schedule|workflow_run):\s*$') { throw "$file must be workflow_dispatch only." }
    if ($workflow -match '(?i)old_sha|-OldCommit') { throw "$file must not accept an operator-chosen old commit." }
    $qualifying = [regex]::Matches($workflow, '(?m)^\s+name: expansion-[a-z]+-upgrade-\$\{\{ inputs\.candidate_sha \}\}\s*$')
    if ($qualifying.Count -ne 1 -or $qualifying[0].Value.Trim() -cne "name: expansion-$slug-upgrade-`${{ inputs.candidate_sha }}")
    {
        throw "$file must upload exactly one artifact named expansion-$slug-upgrade-<sha>."
    }
    foreach ($mode in @('Preflight', 'Produce', 'Verify'))
    {
        if ($workflow -notmatch [regex]::Escape("./eng/verify-expansion-release-upgrade.ps1 -Family $family -Mode $mode ")) { throw "$file omits $mode for $family." }
    }
    if ([regex]::Matches($workflow, '-Family ([A-Za-z]+)') | Where-Object { $_.Groups[1].Value -cne $family }) { throw "$file runs another family's gate." }

    # Readiness re-verifies archived upgrade evidence with the same verifier in Verify mode.
    $verifier = Get-ExpansionRoleVerifier -Family $family -Role 'upgrade'
    if ($verifier.Script -cne 'verify-expansion-release-upgrade.ps1' -or $verifier.EvidenceParameter -cne 'EvidenceRoot' -or
        $verifier.Arguments.Mode -cne 'Verify' -or $verifier.Arguments.Family -cne $family -or
        $null -ne $verifier.ProducerRunParameter)
    {
        throw "Readiness maps the $family upgrade role to the wrong verifier invocation."
    }
}
Assert-Rejected 'jobs-upgrade-family' { Get-ExpansionUpgradeFamily 'Jobs' } 'Jobs keeps its dedicated gate'
Assert-Rejected 'case-substituted-family' { Get-ExpansionUpgradeFamily 'projections' } 'Unknown expansion upgrade family'

# Policy: valid as committed, and its time budgets follow the single documented rule.
$lease = [regex]::Match($probeSupport, 'public const int InFlightLeaseSeconds = (\d+);')
if (-not $lease.Success) { throw 'The probe no longer declares its in-flight lease duration.' }
foreach ($family in $families)
{
    $policy = Get-ExpansionUpgradePolicy -Family $family
    $probe = Get-Content (Join-Path $PSScriptRoot "ExpansionUpgradeProbe/${family}Probe.cs") -Raw
    $waits = $policy.Entry.phaseWaitSeconds
    if ($waits.seed -ne 0 -or $waits.rollback -ne 0 -or
        ($waits.upgrade -ne 0 -and $waits.upgrade -ne [int]$lease.Groups[1].Value) -or
        (($waits.upgrade -ne 0) -ne $probe.Contains('InFlightLease')))
    {
        throw "$family phase waits are not derived from the probe's in-flight lease."
    }
    if (-not $probe.Contains("public const string Family = `"$family`";")) { throw "$family probe reports another family." }
}
$policyText = Get-Content (Join-Path $PSScriptRoot 'expansion-upgrade-policy.json') -Raw
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-upgrade-policy-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try
{
    $mutations = [ordered]@{
        'unpinned-image' = @{ Change = { param($p) $p.postgreSqlImage = 'postgres:18-alpine' }; Guard = 'pinned by digest' }
        'other-major' = @{ Change = { param($p) $p.postgreSqlMajor = 17 }; Guard = 'major version differs' }
        'tuned-timeout' = @{ Change = { param($p) $p.families.Projections.phaseTimeoutSeconds.upgrade = 95 }; Guard = 'ten command timeouts' }
        'foreign-project' = @{ Change = { param($p) $p.families.Documents.packagedProjects += 'src/BlueTusk.Jobs/BlueTusk.Jobs.csproj' }; Guard = 'not one of its family projects' }
        'no-primary' = @{ Change = { param($p) $p.families.Edge.packagedProjects = @('src/BlueTusk.Edge.Server/BlueTusk.Edge.Server.csproj') }; Guard = 'primary project' }
        'missing-doc' = @{ Change = { param($p) $p.families.Sql.documentation = @('docs/sql/UPGRADE.md') }; Guard = 'missing documentation' }
        'empty-phase' = @{ Change = { param($p) $p.families.Studio.observations.rollback = [pscustomobject]@{} }; Guard = 'must expect observable effects' }
        'fractional-effect' = @{ Change = { param($p) $p.families.Search.observations.seed.CursorsOpened = 1.5 }; Guard = 'is invalid' }
        'schema-change' = @{ Change = { param($p) $p.families.Schema.schemaChange = 'downgrade' }; Guard = "'none' or 'upgrade'" }
        'missing-family' = @{ Change = { param($p) $p.families.PSObject.Properties.Remove('Workflows') }; Guard = 'Workflows' }
    }
    foreach ($case in $mutations.GetEnumerator())
    {
        $copy = $policyText | ConvertFrom-Json
        & $case.Value.Change $copy
        $path = Join-Path $temporary "$($case.Key).json"
        [IO.File]::WriteAllText($path, ($copy | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        Assert-Rejected $case.Key { Get-ExpansionUpgradePolicy -Family Projections -PolicyPath $path } $case.Value.Guard
    }
}
finally
{
    [IO.Directory]::Delete($temporary, $true)
}

# Phase evidence rules against synthetic reports; they prove the rules, never a rehearsal.
$empty = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'
function New-Evidence([string] $Family, [string] $Change)
{
    $policy = Get-ExpansionUpgradePolicy -Family $Family
    $digest = { param($Text) [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant() }
    $created = & $digest 'created'
    $migrated = if ($Change -ceq 'upgrade') { & $digest 'migrated' } else { $created }
    $catalogs = @{ seed = @($empty, $created); upgrade = @($created, $migrated); rollback = @($migrated, $migrated) }
    $reports = @{}
    $digests = @{}
    foreach ($phase in @('seed', 'upgrade', 'rollback'))
    {
        $observations = [ordered]@{}
        foreach ($property in $policy.Entry.observations.$phase.PSObject.Properties) { $observations[$property.Name] = [long]$property.Value }
        $reports[$phase] = [pscustomobject][ordered]@{
            Family = $Family; Phase = $phase; Passed = $true; ServerVersionNum = [long]180003
            Fingerprints = [pscustomobject][ordered]@{ before = $catalogs[$phase][0]; after = $catalogs[$phase][1] }
            Observations = [pscustomobject]$observations
        }
        $digests[$phase] = @{ before = $catalogs[$phase][0]; after = $catalogs[$phase][1] }
    }
    return [pscustomobject]@{ Policy = $policy; Reports = $reports; Digests = $digests }
}
function Test-Evidence([object] $Evidence)
{
    Assert-ExpansionUpgradePhases -Policy $Evidence.Policy -Reports $Evidence.Reports -CatalogDigests $Evidence.Digests
}
foreach ($family in $families)
{
    Test-Evidence (New-Evidence $family ([string](Get-ExpansionUpgradePolicy -Family $family).Entry.schemaChange))
}
$substitutions = [ordered]@{
    'other-family-report' = @{ Family = 'Projections'; Change = { param($e) $e.Reports.upgrade.Family = 'Documents' }; Guard = 'belongs to another family or phase' }
    'swapped-phase' = @{ Family = 'Documents'; Change = { param($e) $e.Reports.rollback.Phase = 'upgrade' }; Guard = 'belongs to another family or phase' }
    'failed-phase' = @{ Family = 'Workflows'; Change = { param($e) $e.Reports.seed.Passed = $false }; Guard = 'did not pass' }
    'missing-phase' = @{ Family = 'Search'; Change = { param($e) $e.Reports.Remove('rollback') }; Guard = 'missing the rollback phase' }
    'duplicated-effect' = @{ Family = 'Workflows'; Change = { param($e) $e.Reports.upgrade.Observations.ActivityExecutions = 7 }; Guard = "effect 'ActivityExecutions' was 7" }
    'lost-effect' = @{ Family = 'Projections'; Change = { param($e) $e.Reports.rollback.Observations.ActiveDocuments = 2 }; Guard = "effect 'ActiveDocuments' was 2" }
    'missing-effect' = @{ Family = 'Edge'; Change = { param($e) $e.Reports.upgrade.Observations.PSObject.Properties.Remove('StaleLeaseRejected') }; Guard = 'differ from the policy set' }
    'extra-effect' = @{ Family = 'Sql'; Change = { param($e) $e.Reports.seed.Observations | Add-Member -NotePropertyName Unplanned -NotePropertyValue 1 }; Guard = 'differ from the policy set' }
    'other-server' = @{ Family = 'Studio'; Change = { param($e) $e.Reports.upgrade.ServerVersionNum = [long]170006 }; Guard = 'PostgreSQL major' }
    'catalog-file-swap' = @{ Family = 'Schema'; Change = { param($e) $e.Digests.upgrade.after = 'f' * 64 }; Guard = 'differs from its retained catalog file' }
    'different-database' = @{ Family = 'Documents'; Change = { param($e) $e.Reports.upgrade.Fingerprints.before = 'a' * 64; $e.Reports.upgrade.Fingerprints.after = 'a' * 64
            $e.Digests.upgrade.before = 'a' * 64; $e.Digests.upgrade.after = 'a' * 64 }; Guard = 'did not open the schema the baseline left' }
    'undeclared-migration' = @{ Family = 'Projections'; Change = { param($e) $e.Reports.upgrade.Fingerprints.after = 'b' * 64; $e.Digests.upgrade.after = 'b' * 64
            $e.Reports.rollback.Fingerprints.before = 'b' * 64; $e.Reports.rollback.Fingerprints.after = 'b' * 64
            $e.Digests.rollback.before = 'b' * 64; $e.Digests.rollback.after = 'b' * 64 }; Guard = 'declares no migration' }
    'missing-migration' = @{ Family = 'Events'; Change = { param($e) $created = $e.Reports.upgrade.Fingerprints.before
            $e.Reports.upgrade.Fingerprints.after = $created; $e.Digests.upgrade.after = $created
            $e.Reports.rollback.Fingerprints.before = $created; $e.Reports.rollback.Fingerprints.after = $created
            $e.Digests.rollback.before = $created; $e.Digests.rollback.after = $created }; Guard = 'did not apply the migration' }
    'rollback-rewrote-schema' = @{ Family = 'Events'; Change = { param($e) $e.Reports.rollback.Fingerprints.after = 'c' * 64; $e.Digests.rollback.after = 'c' * 64 }; Guard = 'did not leave unchanged' }
    'preexisting-schema' = @{ Family = 'Search'; Change = { param($e) $e.Reports.seed.Fingerprints.before = 'd' * 64; $e.Digests.seed.before = 'd' * 64 }; Guard = 'did not start from an absent schema' }
}
foreach ($case in $substitutions.GetEnumerator())
{
    $evidence = New-Evidence $case.Value.Family ([string](Get-ExpansionUpgradePolicy -Family $case.Value.Family).Entry.schemaChange)
    & $case.Value.Change $evidence
    Assert-Rejected $case.Key { Test-Evidence $evidence } $case.Value.Guard
}

# The verifier's own pre-resolution guards (the build checkout is shallow, so resolution is tested
# on synthetic histories below rather than on this repository).
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the candidate for the no-database verifier test.' }
$verifierPath = Join-Path $PSScriptRoot 'verify-expansion-release-upgrade.ps1'
Assert-Rejected 'identical-commits' { & $verifierPath -Family Documents -Mode Preflight -ExpectedCommit $head -OldCommit $head } 'distinct exact old and candidate commits'
Assert-Rejected 'other-family-evidence-root' { & $verifierPath -Family Projections -Mode Verify -ExpectedCommit $head -EvidenceRoot 'artifacts/documents-release-upgrade/x' } 'must use artifacts/projections-release-upgrade'
Assert-Rejected 'jobs-evidence-root' { & $verifierPath -Family Edge -Mode Verify -ExpectedCommit $head -EvidenceRoot 'artifacts/jobs-release-upgrade/x' } 'must use artifacts/edge-release-upgrade'
Assert-Rejected 'jobs-family' { & $verifierPath -Family Jobs -Mode Preflight -ExpectedCommit $head } 'does not belong to the set'

# Baseline resolution on synthetic Git histories with a hermetic Git configuration.
$resolver = Join-Path $PSScriptRoot 'resolve-expansion-upgrade-baseline.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-expansion-baseline-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$savedGlobal = $env:GIT_CONFIG_GLOBAL
$savedNoSystem = $env:GIT_CONFIG_NOSYSTEM
function Invoke-FixtureGit([string] $Repository, [string[]] $Arguments)
{
    $output = @(& git -C $Repository @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Fixture git $($Arguments -join ' ') failed." }
    return ,$output
}
function Set-FixtureFile([string] $Repository, [string] $Path, [string] $Content)
{
    $file = Join-Path $Repository $Path
    [IO.Directory]::CreateDirectory((Split-Path $file -Parent)) | Out-Null
    [IO.File]::WriteAllText($file, $Content + "`n", [Text.UTF8Encoding]::new($false))
}
function New-FixtureCommit([string] $Repository, [string] $Message)
{
    Invoke-FixtureGit $Repository @('add', '-A') | Out-Null
    Invoke-FixtureGit $Repository @('commit', '-q', '-m', $Message) | Out-Null
    return ([string](Invoke-FixtureGit $Repository @('rev-parse', 'HEAD'))[0]).Trim()
}
function Set-FixtureManifest([string] $Repository, [string[]] $Packages)
{
    $manifest = [ordered]@{ schemaVersion = 2; families = [ordered]@{ Edge = [ordered]@{ packages = $Packages } } }
    Set-FixtureFile $Repository 'eng/product-families.json' ($manifest | ConvertTo-Json -Depth 5)
}
function Assert-Baseline([string] $Name, [string] $Repository, [string] $Candidate, [string] $Change, [string] $Baseline, [string[]] $Roots)
{
    $result = & $resolver -Family Edge -CandidateCommit $Candidate -RepositoryRoot $Repository
    $tree = { param($Specification) ([string](Invoke-FixtureGit $Repository @('rev-parse', $Specification))[0]).Trim() }
    if ($result.Rule -cne 'first-parent-of-most-recent-family-owned-source-change' -or $result.Family -cne 'Edge' -or
        $result.CandidateCommit -cne $Candidate -or $result.ChangeCommit -cne $Change -or $result.BaselineCommit -cne $Baseline -or
        (@($result.OwnedRoots) -join '|') -cne ($Roots -join '|') -or
        $result.CandidateSourceTree -cne (& $tree "$Candidate^{tree}") -or $result.BaselineSourceTree -cne (& $tree "$Baseline^{tree}") -or
        $result.CandidatePrimaryTree -cne (& $tree "${Candidate}:src/BlueTusk.Edge") -or
        $result.BaselinePrimaryTree -cne (& $tree "${Baseline}:src/BlueTusk.Edge") -or
        (@($result.CandidateFamilyTrees) -join '|') -ceq (@($result.BaselineFamilyTrees) -join '|'))
    {
        throw "Synthetic history '$Name' resolved the wrong Edge baseline: $($result | ConvertTo-Json -Compress)"
    }
}
try
{
    $gitConfig = Join-Path $temporaryRoot 'gitconfig'
    [IO.File]::WriteAllText($gitConfig,
        "[user]`n`tname = BlueTusk baseline fixture`n`temail = fixture@bluetusk.invalid`n[core]`n`tautocrlf = false`n",
        [Text.UTF8Encoding]::new($false))
    $env:GIT_CONFIG_GLOBAL = $gitConfig
    $env:GIT_CONFIG_NOSYSTEM = '1'
    $history = Join-Path $temporaryRoot 'history'
    [IO.Directory]::CreateDirectory($history) | Out-Null
    Invoke-FixtureGit $history @('init', '-q', '-b', 'main') | Out-Null
    Set-FixtureFile $history 'README.md' 'synthetic expansion baseline fixture'
    $two = @('src/BlueTusk.Edge/BlueTusk.Edge.csproj', 'src/BlueTusk.Edge.Server/BlueTusk.Edge.Server.csproj')
    Set-FixtureManifest $history $two
    $c1 = New-FixtureCommit $history 'Repository without Edge'
    Set-FixtureFile $history 'src/BlueTusk.Edge/BlueTusk.Edge.csproj' '<Project Sdk="Microsoft.NET.Sdk" />'
    Set-FixtureFile $history 'src/BlueTusk.Edge/EdgeContracts.cs' 'contracts one'
    Set-FixtureFile $history 'src/BlueTusk.Edge.Server/BlueTusk.Edge.Server.csproj' '<Project Sdk="Microsoft.NET.Sdk" />'
    $c2 = New-FixtureCommit $history 'Introduce Edge'
    Set-FixtureFile $history 'docs/notes.md' 'unrelated'
    $c3 = New-FixtureCommit $history 'Unrelated change'
    Set-FixtureFile $history 'src/BlueTusk.Edge.Server/Store.cs' 'server store one'
    $c4 = New-FixtureCommit $history 'Change a secondary Edge project'
    Set-FixtureFile $history 'clients/edge/index.ts' 'browser client'
    Set-FixtureFile $history 'src/BlueTusk.Edge/PublicAPI.Unshipped.txt' 'shipped surface'
    $c5 = New-FixtureCommit $history 'Change only the npm client and API baselines'
    $roots = @('src/BlueTusk.Edge', 'src/BlueTusk.Edge.Server')
    Assert-Baseline 'secondary-root-change' $history $c4 $c4 $c3 $roots
    Assert-Baseline 'npm-and-api-only' $history $c5 $c4 $c3 $roots
    # The manifest is read from the candidate commit, never from the working tree.
    Set-FixtureManifest $history @('src/BlueTusk.Edge/BlueTusk.Edge.csproj')
    Assert-Baseline 'working-tree-manifest-ignored' $history $c5 $c4 $c3 $roots
    Invoke-FixtureGit $history @('checkout', '-q', '--', 'eng/product-families.json') | Out-Null
    # Adding a family project changes the source set in the same commit that changes the manifest.
    Set-FixtureManifest $history ($two + 'src/BlueTusk.Edge.Sqlite/BlueTusk.Edge.Sqlite.csproj')
    Set-FixtureFile $history 'src/BlueTusk.Edge.Sqlite/BlueTusk.Edge.Sqlite.csproj' '<Project Sdk="Microsoft.NET.Sdk" />'
    $c6 = New-FixtureCommit $history 'Add an Edge project'
    Assert-Baseline 'added-project' $history $c6 $c6 $c5 ($roots + 'src/BlueTusk.Edge.Sqlite')
    # A merge that kept the side branch's Edge sources is followed into that branch.
    Invoke-FixtureGit $history @('checkout', '-q', '-b', 'side') | Out-Null
    Set-FixtureFile $history 'src/BlueTusk.Edge/EdgeContracts.cs' 'contracts two'
    $s1 = New-FixtureCommit $history 'Side-branch Edge change'
    Invoke-FixtureGit $history @('checkout', '-q', 'main') | Out-Null
    Set-FixtureFile $history 'docs/main.md' 'mainline'
    New-FixtureCommit $history 'Mainline unrelated change' | Out-Null
    Invoke-FixtureGit $history @('merge', '-q', '--no-ff', '--no-edit', 'side') | Out-Null
    $merge = ([string](Invoke-FixtureGit $history @('rev-parse', 'HEAD'))[0]).Trim()
    Assert-Baseline 'merged-side-branch' $history $merge $s1 $c6 ($roots + 'src/BlueTusk.Edge.Sqlite')

    Assert-Rejected 'introduced-only' { & $resolver -Family Edge -CandidateCommit $c3 -RepositoryRoot $history } 'introduced the Edge project'
    Assert-Rejected 'no-family-project' { & $resolver -Family Edge -CandidateCommit $c1 -RepositoryRoot $history } 'no Edge project'
    Assert-Rejected 'family-not-in-manifest' { & $resolver -Family Search -CandidateCommit $c5 -RepositoryRoot $history } 'no Search project'
    Assert-Rejected 'jobs-resolver' { & $resolver -Family Jobs -CandidateCommit $c5 -RepositoryRoot $history } 'does not belong to the set'
    $shallow = Join-Path $temporaryRoot 'shallow'
    & git clone -q --depth 1 --branch main ('file:///' + $history.Replace('\', '/')) $shallow
    if ($LASTEXITCODE -ne 0) { throw 'Shallow fixture clone failed.' }
    Assert-Rejected 'shallow-history' { & $resolver -Family Edge -CandidateCommit $merge -RepositoryRoot $shallow } 'complete candidate history'
}
finally
{
    $env:GIT_CONFIG_GLOBAL = $savedGlobal
    $env:GIT_CONFIG_NOSYSTEM = $savedNoSystem
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolvedRoot).StartsWith('bluetusk-expansion-baseline-tests-', [StringComparison]::Ordinal))
    {
        throw 'Refusing to remove a baseline test directory outside the explicit temporary root.'
    }
    # Git object files are read-only on Windows.
    Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force -File | ForEach-Object { $_.Attributes = [IO.FileAttributes]::Normal }
    [IO.Directory]::Delete($resolvedRoot, $true)
}

if (-not $SkipProbeBuild)
{
    $project = Join-Path $PSScriptRoot 'ExpansionUpgradeProbe/ExpansionUpgradeProbe.csproj'
    foreach ($family in $families)
    {
        & dotnet build $project -c Release "-p:UpgradeFamily=$family" "-p:FamilySourceRoot=$root/" `
            --artifacts-path (Join-Path $root "artifacts/expansion-upgrade-probe-policy-build/$($family.ToLowerInvariant())")
        if ($LASTEXITCODE -ne 0) { throw "The candidate $family upgrade probe failed to compile without a database." }
    }
}
Write-Output (
    "Expansion upgrade workflow, readiness mapping, policy, phase-evidence and baseline-resolution contracts passed " +
    "for $($families.Count) families ($rejected rejected substitutions); no PostgreSQL or release run was used.")
