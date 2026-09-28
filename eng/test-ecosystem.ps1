[CmdletBinding()]
param(
    [switch] $RequireDatabase,
    [switch] $ComposeFixture,
    [switch] $Native,
    [switch] $RequireStableSource,
    [ValidateSet('postgres15', 'postgres16', 'postgres17', 'postgres18')]
    [string] $PostgreSqlService = 'postgres18',
    [string] $OutputRoot = 'artifacts/ecosystem-tests',
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$products = @('Events', 'Jobs', 'Documents', 'Projections', 'Search', 'Schema', 'Sql', 'Studio', 'Edge', 'Workflows')
$originalConnection = $env:BLUETUSK_TEST_CONNECTION_STRING
$originalVectorConnection = $env:BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING
$originalOpenSearchEndpoint = $env:BLUETUSK_SEARCH_OPENSEARCH_ENDPOINT
$sourceBefore = $null
$sourceAfter = $null
$sourceStable = $false
$resultDirectory = $null

function Get-OwnedFixtureConnection([string] $Service)
{
    # Read the credentials of the explicit disposable repository fixture, without
    # adding a second fixture-secret declaration or exposing them in output.
    $fixtureId = & docker compose -f (Join-Path $repositoryRoot 'eng/compose/postgres.yml') ps -q $Service
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($fixtureId)) { throw 'The PostgreSQL Compose fixture is not running.' }
    $fixture = (& docker inspect $fixtureId | ConvertFrom-Json)[0]
    if ($fixture.State.Health.Status -ne 'healthy') { throw 'The PostgreSQL Compose fixture is not healthy.' }
    $fixtureEnvironment = @{}
    foreach ($entry in $fixture.Config.Env)
    {
        $parts = $entry.Split('=', 2)
        $fixtureEnvironment[$parts[0]] = $parts[1]
    }
    $fixturePort = $fixture.NetworkSettings.Ports.'5432/tcp'[0].HostPort
    return 'Host=127.0.0.1;Port={0};Username={1};Password={2};Database={3};SSL Mode=Disable;Channel Binding=Disable' -f
        $fixturePort, $fixtureEnvironment.POSTGRES_USER, $fixtureEnvironment.POSTGRES_PASSWORD, $fixtureEnvironment.POSTGRES_DB
}

try
{
    if ($ComposeFixture)
    {
        $env:BLUETUSK_TEST_CONNECTION_STRING = Get-OwnedFixtureConnection $PostgreSqlService
        $env:BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING = Get-OwnedFixtureConnection 'pgvector18'
        $openSearchId = & docker compose -f (Join-Path $repositoryRoot 'eng/compose/opensearch.yml') ps -q opensearch
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($openSearchId)) { throw 'The OpenSearch Compose fixture is not running.' }
        $openSearchFixture = (& docker inspect $openSearchId | ConvertFrom-Json)[0]
        if ($openSearchFixture.State.Health.Status -ne 'healthy') { throw 'The OpenSearch Compose fixture is not healthy.' }
        $env:BLUETUSK_SEARCH_OPENSEARCH_ENDPOINT = 'http://127.0.0.1:' + $openSearchFixture.NetworkSettings.Ports.'9200/tcp'[0].HostPort
    }
    if ($RequireDatabase -and [string]::IsNullOrWhiteSpace($env:BLUETUSK_TEST_CONNECTION_STRING))
    {
        throw 'Live PostgreSQL evidence requires BLUETUSK_TEST_CONNECTION_STRING.'
    }
    if ($RequireDatabase -and [string]::IsNullOrWhiteSpace($env:BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING))
    {
        throw 'Live vector evidence requires BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING.'
    }
    if ($RequireDatabase -and [string]::IsNullOrWhiteSpace($env:BLUETUSK_SEARCH_OPENSEARCH_ENDPOINT))
    {
        throw 'Live OpenSearch evidence requires BLUETUSK_SEARCH_OPENSEARCH_ENDPOINT.'
    }
    $runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff') + '-' + [Guid]::NewGuid().ToString('N')
    $resultDirectory = Join-Path (Join-Path $repositoryRoot $OutputRoot) $runId
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    & python (Join-Path $PSScriptRoot 'capture-ecosystem-source.py') --output (Join-Path $resultDirectory 'source-before.json')
    if ($LASTEXITCODE -ne 0) { throw 'Could not capture the candidate source fingerprint.' }
    $sourceBefore = Get-Content -LiteralPath (Join-Path $resultDirectory 'source-before.json') -Raw | ConvertFrom-Json
    $projects = @()
    foreach ($product in $products)
    {
        $requiredProject = Join-Path $repositoryRoot "tests/BlueTusk.$product.Tests/BlueTusk.$product.Tests.csproj"
        if (-not (Test-Path -LiteralPath $requiredProject -PathType Leaf)) { throw "The $product core test contract is missing." }
        $projects += @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Directory |
            Where-Object { $_.Name.EndsWith('.Tests', [StringComparison]::Ordinal) -and
                ($_.Name -eq "BlueTusk.$product.Tests" -or $_.Name.StartsWith("BlueTusk.$product.", [StringComparison]::Ordinal)) } |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter '*.csproj' -File })
    }
    if ($projects.Count -eq 0) { throw 'No ecosystem test projects have been implemented.' }
    foreach ($project in $projects | Sort-Object FullName -Unique)
    {
        $resultName = $project.BaseName + '.trx'
        & dotnet test $project.FullName -c $Configuration -nr:false --verbosity quiet --logger "trx;LogFileName=$resultName" --results-directory $resultDirectory
        if ($LASTEXITCODE -ne 0) { throw "Ecosystem test project '$($project.BaseName)' failed." }
        $resultPath = Join-Path $resultDirectory $resultName
        if (-not (Test-Path -LiteralPath $resultPath)) { throw "Test evidence is missing for '$($project.BaseName)'." }
        [xml] $trx = Get-Content -LiteralPath $resultPath -Raw
        $counters = $trx.SelectSingleNode('//*[local-name()="Counters"]')
        if ($null -eq $counters -or [int]$counters.total -lt 1 -or [int]$counters.failed -ne 0 -or
            ($RequireDatabase -and [int]$counters.passed -ne [int]$counters.total))
        {
            throw "Incomplete, skipped or failed test evidence for '$($project.BaseName)'."
        }
    }
    Write-Output "Verified all ten ecosystem products across $($projects.Count) test projects; results: $resultDirectory"
    Write-Output 'This gate does not establish completion of all ten products or production qualification.'
    if ($Native)
    {
        & (Join-Path $PSScriptRoot 'test-ecosystem-native.ps1')
    }
}
finally
{
    try
    {
        if ($null -ne $sourceBefore)
        {
            & python (Join-Path $PSScriptRoot 'capture-ecosystem-source.py') --output (Join-Path $resultDirectory 'source-after.json')
            if ($LASTEXITCODE -ne 0) { throw 'Could not capture the final candidate source fingerprint.' }
            $sourceAfter = Get-Content -LiteralPath (Join-Path $resultDirectory 'source-after.json') -Raw | ConvertFrom-Json
            $sourceStable = $sourceBefore.commit -eq $sourceAfter.commit -and $sourceBefore.sourceTreeSha256 -eq $sourceAfter.sourceTreeSha256
            [ordered]@{
                formatVersion = 1
                commit = $sourceBefore.commit
                sourceTreeSha256Before = $sourceBefore.sourceTreeSha256
                sourceTreeSha256After = $sourceAfter.sourceTreeSha256
                sourceStable = $sourceStable
                dirty = $sourceBefore.dirty -or $sourceAfter.dirty
                productionQualified = $false
            } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultDirectory 'source-comparison.json') -Encoding utf8NoBOM
        }
    }
    finally
    {
        $env:BLUETUSK_TEST_CONNECTION_STRING = $originalConnection
        $env:BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING = $originalVectorConnection
        $env:BLUETUSK_SEARCH_OPENSEARCH_ENDPOINT = $originalOpenSearchEndpoint
    }
}
if ($RequireStableSource -and -not $sourceStable) { throw 'Candidate inputs changed during verification; evidence cannot qualify one source tree.' }
