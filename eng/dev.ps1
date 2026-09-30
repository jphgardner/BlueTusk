[CmdletBinding()]
param(
    [ValidateSet('Doctor', 'Check', 'Test', 'Clients', 'Website', 'Docs')]
    [string] $Task = 'Doctor',

    [ValidateSet('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')]
    [string] $Family = 'Provider',

    [string] $Filter,
    [switch] $RequireDatabase,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent

function Invoke-DevCommand
{
    param([string] $Command, [string[]] $Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "'$Command' failed with exit code $LASTEXITCODE. See the output above."
    }
}

function Assert-DevTool
{
    param([string] $Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue))
    {
        throw "Missing '$Name'. See docs/contributing/development.md for setup."
    }
}

$testSets = @{
    Provider = @('BlueTusk.Transport.Tests', 'BlueTusk.Protocol.Tests',
        'BlueTusk.Security.Tests', 'BlueTusk.TypeSystem.Tests', 'BlueTusk.Data.Tests')
    Streams = @('BlueTusk.Streams.Tests', 'BlueTusk.Streams.DependencyInjection.Tests',
        'BlueTusk.Streams.EntityFrameworkCore.Tests', 'BlueTusk.Replication.PgOutput.Tests')
    Sync = @('BlueTusk.Sync.Tests', 'BlueTusk.Sync.DependencyInjection.Tests',
        'BlueTusk.Sync.Nats.Tests', 'BlueTusk.Sync.Redis.Tests', 'BlueTusk.Sync.OpenSearch.Tests',
        'BlueTusk.Sync.Kafka.Tests', 'BlueTusk.Sync.S3.Tests',
        'BlueTusk.Sync.Webhooks.Tests')
    Live = @('BlueTusk.Live.Tests')
    ControlPlane = @('BlueTusk.ControlPlane.Tests', 'BlueTusk.ControlPlane.Kubernetes.Tests')
    ContinuousGraph = @('BlueTusk.ContinuousGraph.Tests')
}

foreach ($projectName in @($testSets.Values | ForEach-Object { $_ }))
{
    $project = Join-Path $repositoryRoot "tests/$projectName/$projectName.csproj"
    if (-not (Test-Path -LiteralPath $project -PathType Leaf))
    {
        throw "Focused test project '$projectName' is missing. Update eng/dev.ps1 with the project rename."
    }
}

Push-Location $repositoryRoot
try
{
    if ($Task -eq 'Doctor')
    {
        $requiredTools = @('git', 'dotnet', 'node', 'npm')
        $missing = @($requiredTools | Where-Object {
            -not (Get-Command $_ -ErrorAction SilentlyContinue)
        })
        foreach ($tool in $requiredTools)
        {
            Write-Output "$tool`: $(if ($tool -in $missing) { 'missing' } else { 'available' })"
        }
        if ('dotnet' -notin $missing)
        {
            Invoke-DevCommand dotnet @('--version')
        }
        if ('node' -notin $missing)
        {
            Invoke-DevCommand node @('--version')
        }
        Write-Output "Browser dependencies: $(if (Test-Path node_modules) { 'installed; use npm ci to restore the lockfile' } else { 'run npm ci' })"
        Write-Output "Website dependencies: $(if (Test-Path website/node_modules) { 'installed' } else { 'run npm ci --prefix website' })"
        Write-Output "Database test configuration: $(if ([string]::IsNullOrWhiteSpace($env:BLUETUSK_TEST_CONNECTION_STRING)) { 'absent; database cases will skip' } else { 'set; value redacted' })"
        Write-Output 'Focused tests: ./eng/dev.ps1 -Task Test -Family Live'
        Write-Output 'Full contributor setup: docs/contributing/development.md'
        if ($missing.Count -ne 0)
        {
            throw "Install the missing tools: $($missing -join ', ')."
        }
        return
    }

    if ($Task -in @('Check', 'Docs'))
    {
        & (Join-Path $PSScriptRoot 'verify-documentation.ps1')
    }
    if ($Task -eq 'Check')
    {
        & (Join-Path $PSScriptRoot 'verify-solution-layout.ps1')
        & (Join-Path $PSScriptRoot 'verify-api-budgets.ps1')
    }
    if ($Task -in @('Clients', 'Website', 'Docs'))
    {
        Assert-DevTool node
        Assert-DevTool npm
        if ($Task -eq 'Clients')
        {
            Invoke-DevCommand npm @('run', 'check:clients')
        }
        else
        {
            Invoke-DevCommand npm @('run', 'docs:generate', '--prefix', 'website')
            Invoke-DevCommand npm @('run', 'docs:check', '--prefix', 'website')
            if ($Task -eq 'Website')
            {
                Invoke-DevCommand npm @('run', 'build', '--prefix', 'website')
            }
        }
        return
    }
    if ($Task -notin @('Test', 'Check'))
    {
        return
    }

    Assert-DevTool dotnet
    if ($RequireDatabase -and [string]::IsNullOrWhiteSpace($env:BLUETUSK_TEST_CONNECTION_STRING))
    {
        throw 'Set BLUETUSK_TEST_CONNECTION_STRING before requesting database validation. Its value will not be printed.'
    }
    $runRoot = Join-Path $repositoryRoot (
        'artifacts/dev/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' +
        [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($projectName in $testSets[$Family])
    {
        $project = "tests/$projectName/$projectName.csproj"
        if (-not (Test-Path -LiteralPath $project -PathType Leaf))
        {
            throw "Focused test project '$project' is missing. Update eng/dev.ps1 with the project rename."
        }
        $resultsDirectory = Join-Path $runRoot "results/$projectName"
        $arguments = @('test', $project, '-c', $Configuration, '--nologo',
            '--artifacts-path', (Join-Path $runRoot 'build'),
            '--logger', 'trx;LogFileName=tests.trx', '--results-directory', $resultsDirectory)
        if (-not [string]::IsNullOrWhiteSpace($Filter))
        {
            $arguments += @('--filter', $Filter)
        }
        Invoke-DevCommand dotnet $arguments
        $trxPath = Join-Path $resultsDirectory 'tests.trx'
        if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf))
        {
            throw "'$projectName' did not produce test results. A successful command without results is not a pass."
        }
        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        $total = [int]$counts.total
        $skipped = [int]$counts.notExecuted
        if ($total -eq 0 -or [int]$counts.failed -ne 0 -or [int]$counts.error -ne 0)
        {
            throw "'$projectName' did not complete a nonempty successful test run. See '$trxPath'."
        }
        $records.Add([pscustomobject]@{
            project = $projectName; total = $total; passed = [int]$counts.passed
            skipped = $skipped; results = $trxPath
            resultsSha256 = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash.ToLowerInvariant()
        })
        Write-Output "$projectName`: $($counts.passed) passed; $skipped skipped."
        if ($RequireDatabase -and $skipped -ne 0)
        {
            throw "'$projectName' skipped $skipped cases despite required database validation. See '$trxPath'."
        }
    }
    $summary = [pscustomobject]@{
        family = $Family; scope = 'Focused contributor tests; not release certification'
        headCommit = (& git rev-parse HEAD).Trim()
        worktreeClean = @(& git status --porcelain).Count -eq 0
        completedUtc = [DateTime]::UtcNow.ToString('O')
        databaseRequired = [bool]$RequireDatabase; tests = $records.ToArray()
    }
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
    Write-Output "Results: $runRoot"
}
finally
{
    Pop-Location
}
