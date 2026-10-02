[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$planner = Join-Path $PSScriptRoot 'get-application-postgresql-test-plan.ps1'
$verifier = Join-Path $PSScriptRoot 'verify-application-postgresql-results.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) "bluetusk-application-tracks-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch
$configurationNames = @('release-tracks.json', 'v1.1-candidate-readiness.json', 'postgresql19-programme.json')
$baseline = @{}
foreach ($name in $configurationNames)
{ $baseline[$name] = Get-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Raw }
$rejectedPlans = 0
$rejectedResults = 0

function Reset-Configuration
{
    foreach ($name in $configurationNames)
    { $baseline[$name] | Set-Content -LiteralPath (Join-Path $scratch $name) -Encoding utf8NoBOM }
}

function Write-TrxFixture
{
    param([string] $Track)
    $plan = & $planner -ReleaseTrack $Track
    $document = [Xml.XmlDocument]::new()
    $namespace = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
    $root = $document.CreateElement('TestRun', $namespace)
    $null = $document.AppendChild($root)
    $results = $document.CreateElement('Results', $namespace)
    $null = $root.AppendChild($results)
    foreach ($test in @($plan.expectedTests))
    {
        $result = $document.CreateElement('UnitTestResult', $namespace)
        $result.SetAttribute('testName', $test)
        $result.SetAttribute('outcome', 'Passed')
        $null = $results.AppendChild($result)
    }
    $summary = $document.CreateElement('ResultSummary', $namespace)
    $summary.SetAttribute('outcome', 'Completed')
    $null = $root.AppendChild($summary)
    $counters = $document.CreateElement('Counters', $namespace)
    foreach ($name in @('total', 'executed', 'passed'))
    { $counters.SetAttribute($name, [string]@($plan.expectedTests).Count) }
    foreach ($name in @('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notRunnable', 'notExecuted', 'disconnected', 'warning', 'inProgress', 'pending'))
    { $counters.SetAttribute($name, '0') }
    $null = $summary.AppendChild($counters)
    return $document
}

try
{
    Reset-Configuration
    $core = & $planner -ReleaseTrack Core -ConfigurationDirectory $scratch
    $preview = & $planner -ReleaseTrack ContinuousGraphPreview -ConfigurationDirectory $scratch
    if ($core.milestone -cne 'stable' -or @($core.expectedTests).Count -ne 1 -or
        $preview.milestone -cne '19beta3' -or @($preview.expectedTests).Count -ne 2 -or
        $core.image -eq $preview.image -or $core.stableGraphQualification -ne $false -or
        $preview.stableGraphQualification -ne $false)
    { throw 'Application test plans lost their separate stable/core and historical/preview identity.' }

    # Core must not read even a malformed or unavailable Graph milestone record.
    '{}' | Set-Content -LiteralPath (Join-Path $scratch 'postgresql19-programme.json') -Encoding utf8NoBOM
    $unblocked = & $planner -ReleaseTrack Core -ConfigurationDirectory $scratch
    if ($unblocked.image -cne $core.image) { throw 'Graph milestone state changed the core test plan.' }
    Reset-Configuration
    $planCases = @(
        @{ track = 'Core'; file = 'v1.1-candidate-readiness.json'; change = {param($c) $c.endurancePostgreSqlImage = 'postgres:18-alpine'} },
        @{ track = 'Core'; file = 'v1.1-candidate-readiness.json'; change = {param($c) $c.endurancePostgreSqlImage = "postgres:19beta3-alpine@sha256:$('a' * 64)"} },
        @{ track = 'Core'; file = 'v1.1-candidate-readiness.json'; change = {param($c) $c.endurancePostgreSqlImage = $null} },
        @{ track = 'Core'; file = 'v1.1-candidate-readiness.json'; change = {param($c) $c.releaseVersion = '1.0.0'} },
        @{ track = 'Core'; file = 'v1.1-candidate-readiness.json'; change = {param($c) $c.scope = 'ContinuousGraphPreview'} },
        @{ track = 'Core'; file = 'release-tracks.json'; change = {param($c) $c.postgresql19RequiredForCorePublication = $true} },
        @{ track = 'ContinuousGraphPreview'; file = 'postgresql19-programme.json'; change = {param($c) $c.lastVerifiedMilestone = '19beta4'} },
        @{ track = 'ContinuousGraphPreview'; file = 'postgresql19-programme.json'; change = {param($c) $c.milestones[1].status = 'pending'} },
        @{ track = 'ContinuousGraphPreview'; file = 'postgresql19-programme.json'; change = {param($c) $c.milestones += $c.milestones[1]} },
        @{ track = 'ContinuousGraphPreview'; file = 'postgresql19-programme.json'; change = {param($c) $c.milestones[1].image = 'postgres:19beta3-alpine'} },
        @{ track = 'ContinuousGraphPreview'; file = 'postgresql19-programme.json'; change = {param($c) $c.milestones[1].image = "postgres:19beta4-alpine@sha256:$('a' * 64)"} },
        @{ track = 'ContinuousGraphPreview'; file = 'release-tracks.json'; change = {param($c) $c.graph.stablePublicationEligible = $true} }
    )
    foreach ($case in $planCases)
    {
        Reset-Configuration
        $configuration = $baseline[$case.file] | ConvertFrom-Json
        & $case.change $configuration
        $configuration | ConvertTo-Json -Depth 12 |
            Set-Content -LiteralPath (Join-Path $scratch $case.file) -Encoding utf8NoBOM
        $rejected = $false
        try { & $planner -ReleaseTrack $case.track -ConfigurationDirectory $scratch | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'An invalid application test plan was accepted.' }
        $rejectedPlans++
    }

    $resultCases = @(
        {param($d) @($d.TestRun.Results.UnitTestResult)[0].SetAttribute('outcome', 'NotExecuted')},
        {param($d) @($d.TestRun.Results.UnitTestResult)[0].SetAttribute('outcome', 'Failed')},
        {param($d) @($d.TestRun.Results.UnitTestResult)[0].SetAttribute('testName', 'Unrelated.Passing.Test')},
        {param($d) $null = $d.TestRun.Results.RemoveChild(@($d.TestRun.Results.UnitTestResult)[0])},
        {param($d) $null = $d.TestRun.Results.AppendChild(@($d.TestRun.Results.UnitTestResult)[0].CloneNode($true))},
        {param($d) $d.TestRun.ResultSummary.SetAttribute('outcome', 'Failed')},
        {param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('total', '999')},
        {param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('executed', '0')},
        {param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('passed', '0')},
        {param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('notExecuted', '1')},
        {param($d) $d.TestRun.ResultSummary.Counters.SetAttribute('error', '1')},
        {param($d) $null = $d.TestRun.RemoveChild($d.TestRun.ResultSummary)}
    )
    $trxPath = Join-Path $scratch 'tests.trx'
    foreach ($track in @('Core', 'ContinuousGraphPreview'))
    {
        $document = Write-TrxFixture $track
        $document.Save($trxPath)
        & $verifier -TrxPath $trxPath -ReleaseTrack $track | Out-Null
        foreach ($change in $resultCases)
        {
            $document = Write-TrxFixture $track
            & $change $document
            $document.Save($trxPath)
            $rejected = $false
            try { & $verifier -TrxPath $trxPath -ReleaseTrack $track | Out-Null }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'An invalid application integration result was accepted.' }
            $rejectedResults++
        }
        $otherTrack = if ($track -eq 'Core') { 'ContinuousGraphPreview' } else { 'Core' }
        (Write-TrxFixture $otherTrack).Save($trxPath)
        $rejected = $false
        try { & $verifier -TrxPath $trxPath -ReleaseTrack $track | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'Graph/core test results were accepted as the other track.' }
        $rejectedResults++
    }

    $repositoryRoot = Split-Path $PSScriptRoot -Parent
    $build = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/build.yml') -Raw
    $previewWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/postgresql-preview.yml') -Raw
    if ($build -notmatch '(?m)^  postgresql19-provider-compatibility:' -or
        $build -notmatch '(?m)^    name: PostgreSQL 19 live matrix\s*$' -or
        -not $build.Contains('test-applications-postgresql.ps1 -ReleaseTrack Core', [StringComparison]::Ordinal) -or
        -not $build.Contains("--filter 'ReleaseTrack!=ContinuousGraphPreview'", [StringComparison]::Ordinal) -or
        $build -match 'test-applications-postgresql.ps1\s+-ReleaseTrack ContinuousGraphPreview' -or
        -not $previewWorkflow.Contains('-ReleaseTrack ContinuousGraphPreview', [StringComparison]::Ordinal))
    { throw 'Core CI must run stable application tests, preserve provider preview status, and isolate Graph application runtime tests.' }
    Write-Output "Application track self-tests passed: independent plans, two valid TRX fixtures, $rejectedPlans rejected plans and $rejectedResults rejected result sets. Synthetic fixtures are NOT release evidence."
}
finally
{
    $scratchPath = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ((Split-Path $scratchPath -Parent) -cne $tempRoot -or
        (Split-Path $scratchPath -Leaf) -notlike 'bluetusk-application-tracks-*')
    { throw 'Refusing unexpected application self-test cleanup target.' }
    Remove-Item -LiteralPath $scratchPath -Recurse -Force
}
