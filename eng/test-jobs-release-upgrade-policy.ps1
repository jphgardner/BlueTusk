[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workflow = Get-Content (Join-Path $root '.github/workflows/jobs-release-upgrade.yml') -Raw
$verifier = Get-Content (Join-Path $PSScriptRoot 'verify-jobs-release-upgrade.ps1') -Raw
$resolver = Join-Path $PSScriptRoot 'resolve-jobs-upgrade-baseline.ps1'
$probe = Get-Content (Join-Path $PSScriptRoot 'JobsUpgradeProbe/Program.cs') -Raw
$project = Join-Path $PSScriptRoot 'JobsUpgradeProbe/JobsUpgradeProbe.csproj'
$candidateProject = Join-Path $root 'src/BlueTusk.Jobs/BlueTusk.Jobs.csproj'

if ($workflow -notmatch '(?m)^  workflow_dispatch:\s*$' -or
    $workflow -match '(?m)^  (?:push|pull_request|schedule):\s*$' -or
    $workflow -notmatch [regex]::Escape('ref: ${{ inputs.candidate_sha }}') -or
    $workflow -notmatch [regex]::Escape('name: expansion-jobs-upgrade-${{ inputs.candidate_sha }}') -or
    $workflow -notmatch 'fetch-depth: 0' -or
    $workflow -notmatch [regex]::Escape('$env:GITHUB_SHA -cne $env:CANDIDATE_SHA') -or
    $workflow -notmatch 'RUN-JOBS-RELEASE-UPGRADE' -or
    $workflow -notmatch 'name: jobs-upgrade-partial-')
{
    throw 'Jobs upgrade workflow lost its exact manual candidate contract.'
}
if ($workflow -match '(?i)old_sha|-OldCommit')
{
    throw 'Jobs upgrade workflow must not accept an operator-chosen old commit; the baseline is resolved deterministically.'
}
foreach ($mode in @('Preflight', 'Run', 'Verify'))
{
    if ($workflow -notmatch "-Mode $mode ") { throw "Jobs upgrade workflow omits $mode." }
}
foreach ($boundary in @('OldCommit', 'merge-base --is-ancestor', 'old-source.tar',
    'OldJobsTree', 'CandidateJobsTree', 'dotnet pack', 'dotnet publish',
    'OldJobsDllSha256', 'CandidateJobsDllSha256',
    'cross-binary rehearsal', 'PackageDllHash', 'AssertReport', 'PhaseHashes',
    'resolve-jobs-upgrade-baseline.ps1', 'BaselineResolverSha256', 'deterministic Jobs upgrade baseline'))
{
    if (-not $verifier.Contains($boundary)) { throw "Jobs upgrade verifier lost '$boundary'." }
}
foreach ($behavior in @('probe.held', 'probe.retry', 'probe.pending',
    'candidate-ready', 'old-done', 'probe.rollback', 'FormatVersion',
    '!await store.CompleteAsync', 'DeduplicationKey'))
{
    if (-not $probe.Contains($behavior)) { throw "Jobs upgrade probe lost '$behavior'." }
}

foreach ($script in @('verify-jobs-release-upgrade.ps1', 'resolve-jobs-upgrade-baseline.ps1'))
{
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot $script), [ref]$null, [ref]$errors) | Out-Null
    if ($null -ne $errors -and $errors.Count -gt 0) { throw "$script has a PowerShell parse error: $($errors[0])" }
}

$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve Jobs candidate for the no-DB policy test.' }
$rejection = $null
try {
    & (Join-Path $PSScriptRoot 'verify-jobs-release-upgrade.ps1') -Mode Preflight `
        -ExpectedCommit $head -OldCommit $head | Out-Null
} catch { $rejection = $_.Exception.Message }
if ($null -eq $rejection -or -not $rejection.Contains('distinct exact old and candidate commits'))
{
    throw "Identical old/candidate commits were not rejected: '$rejection'."
}

# Baseline resolution on synthetic Git histories. These repositories exist only inside this
# self-test and use a hermetic Git configuration, so no user hooks, signing or line-ending
# settings apply. They prove the resolution rule, never a real Jobs baseline.
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bluetusk-jobs-baseline-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$savedGlobal = $env:GIT_CONFIG_GLOBAL
$savedNoSystem = $env:GIT_CONFIG_NOSYSTEM
$rejected = 0
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
function New-FixtureRepository([string] $Name)
{
    $path = Join-Path $temporaryRoot $Name
    [IO.Directory]::CreateDirectory($path) | Out-Null
    Invoke-FixtureGit $path @('init', '-q', '-b', 'main') | Out-Null
    Set-FixtureFile $path 'README.md' 'synthetic baseline-resolution fixture'
    return $path
}
function Add-FixtureJobs([string] $Repository, [string] $Store)
{
    Set-FixtureFile $Repository 'src/BlueTusk.Jobs/BlueTusk.Jobs.csproj' '<Project Sdk="Microsoft.NET.Sdk" />'
    Set-FixtureFile $Repository 'src/BlueTusk.Jobs/PostgreSqlJobStore.cs' $Store
    Set-FixtureFile $Repository 'src/BlueTusk.Jobs/PublicAPI.Unshipped.txt' '#nullable enable'
}
function Resolve-Fixture([string] $Repository, [string] $Commit)
{
    return & $resolver -CandidateCommit $Commit -RepositoryRoot $Repository
}
function Assert-Baseline([string] $Name, [object] $Result, [string] $Repository, [string] $Candidate,
    [string] $Change, [string] $Baseline)
{
    $tree = { param($Specification) ([string](Invoke-FixtureGit $Repository @('rev-parse', $Specification))[0]).Trim() }
    if ($Result.Rule -cne 'first-parent-of-most-recent-jobs-owned-source-change' -or
        (@($Result.OwnedPathspec) -join '|') -cne
            'src/BlueTusk.Jobs|:(exclude)src/BlueTusk.Jobs/PublicAPI.Shipped.txt|:(exclude)src/BlueTusk.Jobs/PublicAPI.Unshipped.txt' -or
        $Result.CandidateCommit -cne $Candidate -or $Result.ChangeCommit -cne $Change -or
        $Result.BaselineCommit -cne $Baseline -or
        $Result.CandidateJobsTree -cne (& $tree "${Candidate}:src/BlueTusk.Jobs") -or
        $Result.BaselineJobsTree -cne (& $tree "${Baseline}:src/BlueTusk.Jobs") -or
        $Result.CandidateSourceTree -cne (& $tree "$Candidate^{tree}") -or
        $Result.BaselineSourceTree -cne (& $tree "$Baseline^{tree}"))
    {
        throw "Synthetic history '$Name' resolved the wrong Jobs baseline: $($Result | ConvertTo-Json -Compress)"
    }
}
function Assert-ResolutionRejected([string] $Name, [string] $Repository, [string] $Commit, [string] $Guard)
{
    $failure = $null
    try { Resolve-Fixture $Repository $Commit | Out-Null } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($Guard))
    {
        throw "Synthetic history '$Name' did not fail at '$Guard': '$failure'."
    }
    $script:rejected++
}

try
{
    $gitConfig = Join-Path $temporaryRoot 'gitconfig'
    [IO.File]::WriteAllText($gitConfig,
        "[user]`n`tname = BlueTusk baseline fixture`n`temail = fixture@bluetusk.invalid`n[core]`n`tautocrlf = false`n",
        [Text.UTF8Encoding]::new($false))
    $env:GIT_CONFIG_GLOBAL = $gitConfig
    $env:GIT_CONFIG_NOSYSTEM = '1'

    $history = New-FixtureRepository 'history'
    $c1 = New-FixtureCommit $history 'Repository without Jobs'
    Add-FixtureJobs $history 'reclaim by id'
    $c2 = New-FixtureCommit $history 'Introduce Jobs'
    Set-FixtureFile $history 'docs/notes.md' 'unrelated'
    $c3 = New-FixtureCommit $history 'Unrelated change'
    Set-FixtureFile $history 'src/BlueTusk.Jobs/PostgreSqlJobStore.cs' 'reclaim by lease expiry'
    $c4 = New-FixtureCommit $history 'Change Jobs durable store'
    Set-FixtureFile $history 'docs/notes.md' 'unrelated again'
    $c5 = New-FixtureCommit $history 'Later unrelated change'
    Assert-Baseline 'linear' (Resolve-Fixture $history $c5) $history $c5 $c4 $c3
    Assert-Baseline 'candidate-is-change' (Resolve-Fixture $history $c4) $history $c4 $c4 $c3

    # Shipping the API surface rewrites the analyzer baselines but not the Jobs implementation.
    Set-FixtureFile $history 'src/BlueTusk.Jobs/PublicAPI.Unshipped.txt' 'shipped'
    Set-FixtureFile $history 'src/BlueTusk.Jobs/PublicAPI.Shipped.txt' '#nullable enable'
    $c6 = New-FixtureCommit $history 'Ship the Jobs API surface'
    Assert-Baseline 'api-baselines-only' (Resolve-Fixture $history $c6) $history $c6 $c4 $c3

    # A merge that kept the side branch's Jobs sources is followed into that branch.
    Invoke-FixtureGit $history @('checkout', '-q', '-b', 'side') | Out-Null
    Set-FixtureFile $history 'src/BlueTusk.Jobs/PostgreSqlJobStore.cs' 'reclaim by lease expiry then id'
    $s1 = New-FixtureCommit $history 'Side-branch Jobs change'
    Invoke-FixtureGit $history @('checkout', '-q', 'main') | Out-Null
    Set-FixtureFile $history 'docs/main.md' 'mainline'
    New-FixtureCommit $history 'Mainline unrelated change' | Out-Null
    Invoke-FixtureGit $history @('merge', '-q', '--no-ff', '--no-edit', 'side') | Out-Null
    $merge = ([string](Invoke-FixtureGit $history @('rev-parse', 'HEAD'))[0]).Trim()
    Assert-Baseline 'merged-side-branch' (Resolve-Fixture $history $merge) $history $merge $s1 $c6

    # A merge that combined Jobs changes from both parents is itself the most recent change.
    Invoke-FixtureGit $history @('checkout', '-q', 'side') | Out-Null
    Set-FixtureFile $history 'src/BlueTusk.Jobs/JobWorker.cs' 'side worker'
    New-FixtureCommit $history 'Side worker change' | Out-Null
    Invoke-FixtureGit $history @('checkout', '-q', 'main') | Out-Null
    Set-FixtureFile $history 'src/BlueTusk.Jobs/JobHealth.cs' 'main health'
    $m2 = New-FixtureCommit $history 'Mainline Jobs health change'
    Invoke-FixtureGit $history @('merge', '-q', '--no-ff', '--no-edit', 'side') | Out-Null
    $combined = ([string](Invoke-FixtureGit $history @('rev-parse', 'HEAD'))[0]).Trim()
    Assert-Baseline 'combining-merge' (Resolve-Fixture $history $combined) $history $combined $combined $m2

    Assert-ResolutionRejected 'introduced-only' $history $c3 'introduced the Jobs project'
    Assert-ResolutionRejected 'no-jobs' $history $c1 'no Jobs project'
    $shallow = Join-Path $temporaryRoot 'shallow'
    & git clone -q --depth 1 --branch main ('file:///' + $history.Replace('\', '/')) $shallow
    if ($LASTEXITCODE -ne 0) { throw 'Shallow fixture clone failed.' }
    Assert-ResolutionRejected 'shallow-history' $shallow $combined 'complete candidate history'
}
finally
{
    $env:GIT_CONFIG_GLOBAL = $savedGlobal
    $env:GIT_CONFIG_NOSYSTEM = $savedNoSystem
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedRoot).StartsWith(
            'bluetusk-jobs-baseline-tests-', [StringComparison]::Ordinal)))
    {
        throw 'Refusing to remove a Jobs baseline test directory outside the explicit temporary root.'
    }
    # Git object files are read-only on Windows.
    Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force -File |
        ForEach-Object { $_.Attributes = [IO.FileAttributes]::Normal }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

& dotnet build $project -c Release -p:JobsProject=$candidateProject `
    --artifacts-path (Join-Path $root 'artifacts/jobs-upgrade-probe-policy-build')
if ($LASTEXITCODE -ne 0) { throw 'Candidate Jobs upgrade probe failed to compile without a database.' }
Write-Output (
    'Jobs upgrade source policy, deterministic baseline resolution (5 synthetic candidates, ' +
    "$rejected fail-closed cases) and candidate probe compile passed; no PostgreSQL or release run was used.")
